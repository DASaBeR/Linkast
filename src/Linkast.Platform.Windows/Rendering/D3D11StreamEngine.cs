// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using Linkast.Core;
using Linkast.Core.Audio;
using Linkast.Core.Camera;
using Linkast.Core.Rendering;
using Linkast.Engine.FFmpeg;
using Linkast.Platform.Windows.Audio;
using Linkast.Platform.Windows.Camera;
using Linkast.Platform.Windows.Capture;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Linkast.Platform.Windows.Rendering;

/// <summary>
/// Next-generation streaming engine powered by an in-app Direct3D 11 GPU compositor and single-pipe FFmpeg architecture (ADR 0010).
/// Eliminates FFmpeg filter_complex, ZMQ sockets, and process restarts on scene or window switches.
/// </summary>
public sealed partial class D3D11StreamEngine : IStreamEngine, ILiveSceneControl, ILiveAudioControl
{
    private readonly FfmpegInstallation _ffmpeg;
    private readonly IOutputTransport _transport;
    private readonly AudioCaptureHub _audioHub;
    private readonly ILogger? _logger;
    private readonly Redactor _redactor = new();
    private readonly EngineOptions _options;
    private readonly TimeProvider _time;
    private readonly StreamStateMachine _state = new();
    private readonly Lock _gate = new();

    private readonly D3D11DeviceManager _deviceManager;
    private readonly bool _ownsDeviceManager;
    private readonly ICameraService? _cameraService;
    private readonly ISegmentationService? _segmentation;
    private readonly BackgroundImageCache? _backgroundImages;
    private readonly IFramingService? _framing;
    private readonly AudioLevelBus? _audioLevels;
    private D3D11SceneCompositor? _compositor;
    private D3D11SceneCompositor? _verticalCompositor;
    private SourceFrameCache? _frameCache;
    private MediaOutputScheduler? _scheduler;
    private UnifiedAudioMixer? _audioMixer;
    private volatile IReadOnlyList<RecordingOutput> _secondaryOutputs = [];

    private readonly ConcurrentDictionary<string, ID3D11SceneSource> _sources = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, NormalizedRect> _layerCrops = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, NormalizedRect> _layerPlacements = new(StringComparer.Ordinal);
    private volatile IReadOnlyList<string> _stackOrder = [];
    private readonly Lock _layoutLock = new();
    private SceneLayout _currentLayout = new();
    private volatile CanvasLayout? _verticalLayout;

    // What the running stream shows now, changed live and kept across automatic reconnects. A reconnect used
    // to rebuild everything from the session as it was at the start: layers dragged, hidden or switched while
    // live jumped back, a closed game came back, and volume changes were lost.
    private IReadOnlyList<CaptureBinding>? _liveCaptures;
    private readonly ConcurrentDictionary<string, AudioSourceSpec> _liveAudio = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, double> _liveGains = new(StringComparer.OrdinalIgnoreCase);
    private bool _layoutApplied;

    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private int _ffmpegProcessId;
    private long _sessionStart;
    private int _reconnectCount;
    private StreamError? _lastError;
    private EncoderProgress? _latestProgress;
    private long? _liveSince;

    public D3D11StreamEngine(
        FfmpegInstallation ffmpeg,
        IOutputTransport transport,
        AudioCaptureHub? audioHub = null,
        ILogger? logger = null,
        D3D11DeviceManager? deviceManager = null,
        TimeProvider? timeProvider = null,
        EngineOptions? options = null,
        ICameraService? cameraService = null,
        ISegmentationService? segmentation = null,
        BackgroundImageCache? backgroundImages = null,
        IFramingService? framing = null,
        AudioLevelBus? audioLevels = null)
    {
        _ffmpeg = ffmpeg ?? throw new ArgumentNullException(nameof(ffmpeg));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _audioHub = audioHub ?? new AudioCaptureHub();
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _options = options ?? new EngineOptions();
        _cameraService = cameraService;
        _segmentation = segmentation;
        _backgroundImages = backgroundImages;
        _framing = framing;
        _audioLevels = audioLevels;

        if (deviceManager is not null)
        {
            _deviceManager = deviceManager;
            _ownsDeviceManager = false;
        }
        else
        {
            _deviceManager = new D3D11DeviceManager();
            _ownsDeviceManager = true;
        }

        _state.Changed += (s, e) => StateChanged?.Invoke(this, e);
    }

    public StreamState State => _state.Current;

    internal MediaOutputScheduler? Scheduler => _scheduler;

    public int FfmpegProcessId => Volatile.Read(ref _ffmpegProcessId);

    public StreamError? LastError => _lastError;

    public FfmpegInstallation Installation => _ffmpeg;

    public D3D11DeviceManager DeviceManager => _deviceManager;

    public Func<VideoSourceSpec, (int Width, int Height)?>? SourceSize { get; set; }

    public Func<OverlayLayerBinding, int, int, ID3D11SceneSource?>? OverlaySourceFactory { get; set; }

    public Func<MediaLayerBinding, int, int, ID3D11SceneSource?>? MediaSourceFactory { get; set; }

    public event EventHandler<StreamStateChangedEventArgs>? StateChanged;

    public event EventHandler<StreamTelemetry>? TelemetryUpdated;

    public event EventHandler<string>? DiagnosticLine;

    public event EventHandler<string>? RecordingSaved;

    public event EventHandler<RecordingFailedEventArgs>? RecordingFailed;

    public void RegisterSource(string key, ID3D11SceneSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _sources[key] = source;
    }

    public Task StartAsync(StreamSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var errors = SessionValidator.Validate(session);
        if (errors.Count > 0)
        {
            throw new StreamFailureException(errors[0]);
        }

        lock (_gate)
        {
            if (StreamStateMachine.IsActive(_state.Current))
            {
                throw new InvalidOperationException("A stream is already active.");
            }

            _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _sessionStart = _time.GetTimestamp();
            _reconnectCount = 0;
            _lastError = null;
            _latestProgress = null;

            _liveCaptures = null;
            _layoutApplied = false;
            _verticalLayout = session.VerticalRecording?.Layout;
            _liveAudio.Clear();
            _liveGains.Clear();
            foreach (var a in session.Audio)
            {
                _liveAudio.TryAdd(ChannelOf(a), a);
            }

            // The microphone's noise removal as the session asks; switching it live changes it from here on.
            _noiseRemoval = session.Audio.OfType<MicrophoneSource>().FirstOrDefault()?.EffectiveNoiseRemoval ?? NoiseRemoval.Off;
            _microphoneBoostDb = session.Audio.OfType<MicrophoneSource>().FirstOrDefault()?.BoostDb ?? 0;

            _state.TryTransition(StreamState.Preparing);
            var token = _runCts.Token;
            _runTask = Task.Run(() => RunSessionAsync(session, token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? run;
        lock (_gate)
        {
            run = _runTask;
            if (run is null || !StreamStateMachine.IsActive(_state.Current))
            {
                return;
            }

            _state.TryTransition(StreamState.Stopping);
            _runCts?.Cancel();
        }

        await run.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _runCts?.Dispose();
        _frameCache?.Dispose();
        _compositor?.Dispose();
        _verticalCompositor?.Dispose();

        foreach (var src in _sources.Values)
        {
            await src.DisposeAsync().ConfigureAwait(false);
        }
        _sources.Clear();

        if (_ownsDeviceManager)
        {
            _deviceManager.Dispose();
        }
    }

    private async Task RunSessionAsync(StreamSession session, CancellationToken ct)
    {
        var backoff = new ReconnectBackoff(session.Reconnect);
        var captureRestarts = new ReconnectBackoff(_options.CaptureRestart);
        var attempt = 0;
        var wasLive = false;
        try
        {
            await PruneStaleSourcesAsync(session).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                _state.TryTransition(StreamState.Connecting);
                attempt++;
                var attemptBegan = _time.GetTimestamp();
                if (_logger is not null)
                {
                    LogAttemptStarted(_logger, attempt, _reconnectCount);
                }

                var error = await RunAttemptAsync(session, [backoff, captureRestarts], ct).ConfigureAwait(false);
                if (_logger is not null)
                {
                    LogAttemptEnded(_logger, attempt, _time.GetElapsedTime(attemptBegan).TotalSeconds,
                        error is null ? "stopped" : $"{error.Category}/{error.Code}", error is null ? string.Empty : Short(_redactor.Redact(error.Detail ?? error.UserMessage)));
                    if (wasLive && error?.Detail?.Contains("right away", StringComparison.Ordinal) == true)
                    {
                        // Seen in testers' logs: after a dropped connection the server often still holds the old
                        // publish for some seconds and refuses the same key; it is not a wrong key.
                        LogRefusedAfterDrop(_logger);
                    }
                }

                wasLive |= _state.Current == StreamState.Live || _liveSince is not null;
                _liveSince = null;

                if (error is null || ct.IsCancellationRequested)
                {
                    break;
                }

                _lastError = error;
                var budget = error.Category == ErrorCategory.Capture ? captureRestarts : backoff;
                if (error.IsRetryable && budget.NextDelay() is { } nextDelay)
                {
                    _reconnectCount++;
                    if (_logger is not null)
                    {
                        LogRetry(_logger, _reconnectCount, nextDelay.TotalSeconds);
                    }

                    _state.TryTransition(StreamState.Reconnecting, error);
                    PublishTelemetry(session, null, null, null);
                    try
                    {
                        await Task.Delay(nextDelay, _time, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    continue;
                }

                if (_logger is not null)
                {
                    LogGaveUp(_logger, $"{error.Category}/{error.Code}", error.IsRetryable);
                }

                _state.TryTransition(StreamState.Failed, error);
                PublishTelemetry(session, null, null, null);
                return;
            }

            _state.TryTransition(StreamState.Stopping);
            _state.TryTransition(StreamState.Stopped);
            PublishTelemetry(session, null, null, null);
        }
        catch (Exception ex)
        {
            if (_logger is not null)
            {
                LogUnexpected(_logger, ex);
            }
            var error = new StreamError(ErrorCategory.Unknown, "engine.unexpected", "An unexpected error stopped the stream.", _redactor.Redact(ex.Message));
            _lastError = error;
            _state.TryTransition(StreamState.Failed, error);
        }
        finally
        {
            _verticalLayout = null;
        }
    }

    private async Task<StreamError?> RunAttemptAsync(StreamSession session, ReconnectBackoff[] backoffs, CancellationToken ct)
    {
        IOutputLease? lease = null;
        FfmpegProcess? process = null;
        D3D11OutputTarget? primaryTarget = null;
        var secondaryOutputs = new List<RecordingOutput>();
        var savedRecordingPaths = new List<string>();

        // Each attempt starts a new FFmpeg whose frame count starts at 0. The last attempt's progress (frame
        // 1800, say) left here made every new attempt look stalled: its frames never passed the old count,
        // and the stream was cut 11 s into each reconnect ("no new frames for 10 s") in a tester's log.
        Volatile.Write(ref _latestProgress, null);

        try
        {
            lease = session.Broadcast
                ? BufferedOutputLease.Start(_ffmpeg, await _transport.OpenAsync(session.Destination, session.Network, ct).ConfigureAwait(false), _logger ?? NullLogger.Instance, line =>
                {
                    var redacted = _redactor.Redact("sender: " + line);
                    DiagnosticLine?.Invoke(this, redacted);
                })
                : new RecordOnlyLease();

            var attemptNow = _time.GetLocalNow().DateTime;
            string? primaryRecordingPath = null;
            if (session.Recording is { } recording)
            {
                try
                {
                    Directory.CreateDirectory(recording.Folder);
                    primaryRecordingPath = RecordingFiles.NewPath(recording.Folder, attemptNow);
                    if (!session.Broadcast || recording.Encoder is null)
                    {
                        savedRecordingPaths.Add(primaryRecordingPath);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                {
                    if (!session.Broadcast)
                    {
                        return new StreamError(ErrorCategory.Configuration, "recording.folder", "The recordings folder can't be created. Choose another folder.", _redactor.Redact(ex.Message));
                    }

                    // A recording folder failure while broadcasting must not prevent going live.
                    var folderError = new StreamError(ErrorCategory.Configuration, "recording.folder", "The recordings folder can't be created. Choose another folder.", _redactor.Redact(ex.Message));
                    RecordingFailed?.Invoke(this, new RecordingFailedEventArgs("recording", folderError));
                    primaryRecordingPath = null;
                }
            }

            var primaryEncoder = session.Broadcast ? session.Encoder : (session.Recording?.Encoder ?? session.Encoder);
            var (canvasWidth, canvasHeight) = ResolveMainCanvasSize(session, primaryEncoder);

            string? verticalPath = null;
            VerticalRecordingSpec? verticalSpec = null;
            if (session.VerticalRecording is { } vertical)
            {
                if (canvasWidth <= canvasHeight)
                {
                    if (_logger is not null)
                    {
                        LogVerticalSkipped(_logger);
                    }
                    DiagnosticLine?.Invoke(this, "Vertical recording skipped: the stream is already vertical.");
                }
                else
                {
                    try
                    {
                        Directory.CreateDirectory(vertical.Folder);
                        verticalPath = RecordingFiles.NewPath(vertical.Folder, attemptNow, "vertical");
                        verticalSpec = vertical;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                    {
                        var folderError = new StreamError(ErrorCategory.Configuration, "recording.folder", "The recordings folder can't be created. Choose another folder.", _redactor.Redact(ex.Message));
                        RecordingFailed?.Invoke(this, new RecordingFailedEventArgs("vertical", folderError));
                    }
                }
            }

            // The compositor outlives a stream; a new stream may have another size (a vertical scene, a new
            // resolution), and FFmpeg must get frames of the size it was told.
            _compositor ??= new D3D11SceneCompositor(_deviceManager, canvasWidth, canvasHeight);
            _compositor.ResizeCanvas(canvasWidth, canvasHeight);
            if (verticalSpec is not null)
            {
                _verticalCompositor ??= new D3D11SceneCompositor(_deviceManager, verticalSpec.Encoder.Width, verticalSpec.Encoder.Height);
                _verticalCompositor.ResizeCanvas(verticalSpec.Encoder.Width, verticalSpec.Encoder.Height);
            }
            _frameCache ??= new SourceFrameCache(_deviceManager);

            SetupSources(session, canvasWidth, canvasHeight);

            if (_layoutApplied)
            {
                if (_logger is not null)
                {
                    lock (_layoutLock)
                    {
                        LogLayoutKept(_logger, _layerPlacements.Count, string.Join(", ", _layerPlacements.Keys.Order(StringComparer.Ordinal)));
                    }
                }
            }
            else
            lock (_layoutLock)
            {
                _layoutApplied = true;
                _layerPlacements.Clear();
                _layerCrops.Clear();
                _stackOrder = session.Stack.Count > 0 ? [.. session.Stack] : [];

                if (session.ShowCapture)
                {
                    _layerPlacements["capture"] = session.BasePlacement;
                    _layerCrops["capture"] = session.BaseCrop;
                }

                foreach (var c in session.Captures)
                {
                    if (!c.StartHidden)
                    {
                        _layerPlacements[c.Key] = c.Placement;
                    }
                    _layerCrops[c.Key] = c.Crop;
                }

                foreach (var m in session.Media)
                {
                    if (!m.StartHidden)
                    {
                        _layerPlacements[m.LayerId] = NormalizedRect.Full;
                    }
                    _layerCrops[m.LayerId] = NormalizedRect.Full;
                }

                var cameraMap = new Dictionary<string, NormalizedRect>();
                foreach (var b in session.CameraLayers)
                {
                    if (!b.StartHidden)
                    {
                        cameraMap[b.Layer.LayerId] = b.Placement;
                        _layerPlacements[b.Layer.LayerId] = b.Placement;
                    }
                    _layerCrops[b.Layer.LayerId] = b.Crop;
                }

                foreach (var o in session.Overlays)
                {
                    if (!o.StartHidden)
                    {
                        _layerPlacements[o.LayerId] = o.Placement;
                    }
                    _layerCrops[o.LayerId] = o.Crop;
                }

                _currentLayout = new SceneLayout
                {
                    ShowCapture = session.ShowCapture,
                    BasePlacement = session.BasePlacement,
                    BaseCrop = session.BaseCrop,
                    BaseFit = session.BaseFit,
                    CameraPlacements = cameraMap,
                };
            }

            var videoPipeName = $"linkast-video-{Guid.NewGuid():N}";
            var audioPipeName = $"linkast-audio-{Guid.NewGuid():N}";
            var videoPipePath = @"\\.\pipe\" + videoPipeName;
            var audioPipePath = @"\\.\pipe\" + audioPipeName;

            var primaryNv12Size = (int)(primaryEncoder.Width * primaryEncoder.Height * 1.5);
            await using var videoPipe = new NamedPipeServerStream(
                videoPipeName,
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                0,
                primaryNv12Size * 4);

            await using var audioPipe = new NamedPipeServerStream(
                audioPipeName,
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                0,
                192000);

            var primaryName = session.Broadcast ? "stream" : "recording";
            primaryTarget = new D3D11OutputTarget(_deviceManager, primaryName, primaryEncoder.Width, primaryEncoder.Height, videoPipe, isPrimary: true, _logger);

            var primaryRecordingArg = session.Broadcast
                ? (session.Recording is { Encoder: null } ? primaryRecordingPath : null)
                : primaryRecordingPath;

            var job = new SinglePipeFfmpegJob(
                VideoPipePath: videoPipePath,
                Width: primaryEncoder.Width,
                Height: primaryEncoder.Height,
                Fps: primaryEncoder.Fps,
                AudioPipePath: audioPipePath,
                Encoder: primaryEncoder,
                PublishUrl: session.Broadcast ? lease.PublishUrl : null,
                TcUrl: session.Broadcast && lease.TcUrl.Length > 0 ? lease.TcUrl : null,
                RecordingPath: primaryRecordingArg);

            var args = FfmpegArgumentBuilder.BuildSinglePipe(job);
            DiagnosticLine?.Invoke(this, _redactor.Redact("ffmpeg: " + string.Join(' ', args)));

            var recentLines = new ConcurrentQueue<string>();
            var parser = new ProgressParser();

            process = FfmpegProcess.Start(
                _ffmpeg.Path,
                args,
                line =>
                {
                    if (parser.Feed(line) is { } p)
                    {
                        Volatile.Write(ref _latestProgress, p);
                    }
                },
                line =>
                {
                    var redacted = _redactor.Redact(line);
                    recentLines.Enqueue(redacted);
                    while (recentLines.Count > 60 && recentLines.TryDequeue(out _)) { }
                    DiagnosticLine?.Invoke(this, redacted);
                });

            Volatile.Write(ref _ffmpegProcessId, process.ProcessId);

            await using var scheduler = new MediaOutputScheduler(
                _compositor,
                _frameCache,
                () => { lock (_layoutLock) return _currentLayout; },
                () => _sources,
                [primaryTarget],
                primaryEncoder.Fps,
                _logger,
                cropsProvider: () => _layerCrops,
                placementsProvider: () => _layerPlacements,
                stackOrderProvider: () => _stackOrder);

            _scheduler = scheduler;

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(_options.ConnectTimeout);

            try
            {
                // Handshake step 1: Wait for videoPipe to connect, racing against FFmpeg early exit
                var videoConnect = videoPipe.WaitForConnectionAsync(connectCts.Token);
                var first = await Task.WhenAny(videoConnect, process.Exited).ConfigureAwait(false);
                if (first == process.Exited)
                {
                    await connectCts.CancelAsync().ConfigureAwait(false);
                    return EarlyExitError(process, recentLines);
                }
                await videoConnect.ConfigureAwait(false);

                // Handshake step 2: Unblock FFmpeg's avformat_find_stream_info probe on Input 0 by pumping
                // the initial composited frame, so FFmpeg can proceed to open Input 1 (audioPipe).
                scheduler.RenderSingleFrame();

                // Handshake step 3: Wait for audioPipe to connect, racing against FFmpeg early exit
                var audioConnect = audioPipe.WaitForConnectionAsync(connectCts.Token);
                first = await Task.WhenAny(audioConnect, process.Exited).ConfigureAwait(false);
                if (first == process.Exited)
                {
                    await connectCts.CancelAsync().ConfigureAwait(false);
                    return EarlyExitError(process, recentLines);
                }
                await audioConnect.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                bool videoConnected = videoPipe.IsConnected;
                bool audioConnected = audioPipe.IsConnected;
                bool isAlive = !process.HasExited;
                int? exitCode = process.ExitCode;
                string lastErrorText = string.Join(Environment.NewLine, recentLines);

                process.Kill();

                var detail = $"Pipes: video={(videoConnected ? "connected" : "waiting")}, audio={(audioConnected ? "connected" : "waiting")}. " +
                    $"Process: {(isAlive ? $"running (PID {process.ProcessId})" : $"exited with code {exitCode}")}." +
                    (string.IsNullOrWhiteSpace(lastErrorText) ? "" : $" Stderr: {lastErrorText}");

                return new StreamError(ErrorCategory.Process, "pipe.connect_timeout", "FFmpeg failed to connect to internal media pipes.", detail);
            }
            catch (IOException ex)
            {
                await connectCts.CancelAsync().ConfigureAwait(false);
                if (process.HasExited)
                {
                    return EarlyExitError(process, recentLines);
                }

                var winner = await Task.WhenAny(process.Exited, Task.Delay(500, CancellationToken.None)).ConfigureAwait(false);
                if (winner == process.Exited)
                {
                    return EarlyExitError(process, recentLines);
                }

                return new StreamError(ErrorCategory.Process, "pipe.io_error", "Failed to communicate with FFmpeg media pipes.", ex.Message);
            }

            // The audio clock starts only now that FFmpeg is reading both pipes: a start before the
            // handshake would make the mixer pad the handshake's duration as silence and put audio behind video.
            // The sound follows the picture's clock: wall time less the time the render loop lost, so a stall
            // costs both the same and they stay together.
            var streamStart = _time.GetTimestamp();
            await using var mixer = new UnifiedAudioMixer(
                _audioHub,
                audioPipe,
                streamStart,
                _logger,
                () => scheduler.LostTime);

            _audioMixer = mixer;

            foreach (var a in _liveAudio.Values)
            {
                var ch = ChannelOf(a);
                try
                {
                    mixer.AddSource(ch, a, _liveGains.TryGetValue(ch, out var liveGain) ? liveGain : a.Gain);
                }
                catch (Exception ex) when (a.Optional)
                {
                    DiagnosticLine?.Invoke(this, $"[warning] Optional audio source '{ch}' failed: {ex.Message}");
                }
            }

            _ = mixer.SetNoiseRemovalAsync(_noiseRemoval, ct);
            _ = mixer.SetMicrophoneBoostAsync(_microphoneBoostDb, ct);
            if (_logger is not null)
            {
                LogAudioStarted(_logger, string.Join(", ", _liveAudio.Keys.Order(StringComparer.OrdinalIgnoreCase).Select(k =>
                    string.Create(CultureInfo.InvariantCulture, $"{k} {(_liveGains.TryGetValue(k, out var g) ? g : _liveAudio[k].Gain) * 100:0}%"))), _noiseRemoval.ToString());
            }

            // Secondary main recording (separate quality while broadcasting). Started after the stream's handshake
            // and before the scheduler/mixer loops tick so every output begins on the exact same frame and slice.
            if (session.Broadcast && session.Recording is { Encoder: { } recEncoder } && primaryRecordingPath is not null)
            {
                await TryStartSecondaryRecordingAsync(
                    "recording", "main", primaryRecordingPath, recEncoder, scheduler, mixer, secondaryOutputs, savedRecordingPaths, ct).ConfigureAwait(false);
            }

            // Secondary vertical recording (portrait canvas alongside a landscape stream or recording).
            if (verticalSpec is not null && verticalPath is not null && _verticalCompositor is not null)
            {
                _verticalLayout ??= verticalSpec.Layout;
                scheduler.AddCanvas(new SchedulerCanvas(
                    "vertical",
                    _verticalCompositor,
                    () => _verticalLayout!.Placements,
                    () => _verticalLayout!.Crops,
                    () => _verticalLayout!.Stack,
                    [],
                    () => _verticalLayout!.Fits));
                await TryStartSecondaryRecordingAsync(
                    "vertical", "vertical", verticalPath, verticalSpec.Encoder, scheduler, mixer, secondaryOutputs, savedRecordingPaths, ct).ConfigureAwait(false);
            }

            if (_logger is not null)
            {
                LogOutputsStarted(_logger, FormatOutputsSummary(primaryName, primaryEncoder, secondaryOutputs));
            }

            using var secondaryWakeup = new SemaphoreSlim(0);
            scheduler.OutputDetached += (_, e) =>
            {
                if (secondaryOutputs.FirstOrDefault(r => string.Equals(r.Name, e.Name, StringComparison.Ordinal)) is { } rec)
                {
                    rec.NoteDetachReason(e.Reason);
                    mixer.RemoveSink(rec.Name);
                    secondaryWakeup.Release();
                }
            };
            mixer.SinkDetached += (_, name) =>
            {
                if (secondaryOutputs.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal)) is { } rec)
                {
                    rec.NoteDetachReason("Audio pipe closed.");
                    rec.Target.TryDetach();
                    secondaryWakeup.Release();
                }
            };

            _secondaryOutputs = [.. secondaryOutputs];
            scheduler.Start();
            mixer.Start();

            return await SuperviseAsync(
                primaryEncoder,
                session.Broadcast,
                backoffs,
                lease,
                process,
                recentLines,
                () => Volatile.Read(ref _latestProgress),
                secondaryOutputs,
                secondaryWakeup,
                scheduler,
                mixer,
                videoPipe,
                audioPipe,
                ct).ConfigureAwait(false);
        }
        catch (StreamFailureException ex)
        {
            return ex.Error;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            _secondaryOutputs = [];
            _scheduler = null;
            _audioMixer = null;
            Volatile.Write(ref _ffmpegProcessId, 0);

            var runningSecondaryStops = secondaryOutputs
                .Where(r => !r.Exited.IsCompleted)
                .Select(r => r.StopAsync(_options.GracefulStopTimeout))
                .ToArray();
            if (runningSecondaryStops.Length > 0)
            {
                await Task.WhenAll(runningSecondaryStops).ConfigureAwait(false);
            }

            foreach (var r in secondaryOutputs)
            {
                await r.DisposeAsync().ConfigureAwait(false);
            }

            primaryTarget?.Dispose();

            if (process is not null)
            {
                await process.DisposeAsync().ConfigureAwait(false);
            }

            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }

            foreach (var path in savedRecordingPaths)
            {
                if (File.Exists(path))
                {
                    RecordingSaved?.Invoke(this, path);
                }
            }
        }
    }

    private async Task<StreamError?> SuperviseAsync(
        EncoderSettings primaryEncoder,
        bool broadcast,
        ReconnectBackoff[] backoffs,
        IOutputLease lease,
        FfmpegProcess process,
        ConcurrentQueue<string> recent,
        Func<EncoderProgress?> latestProgress,
        List<RecordingOutput> secondaryOutputs,
        SemaphoreSlim secondaryWakeup,
        MediaOutputScheduler scheduler,
        UnifiedAudioMixer mixer,
        NamedPipeServerStream primaryVideoPipe,
        NamedPipeServerStream primaryAudioPipe,
        CancellationToken ct)
    {
        var attemptStart = _time.GetTimestamp();
        long? liveSince = null;
        long lastFrame = -1, lastBytes = -1;
        var lastFrameAdvance = attemptStart;
        var lastBytesAdvance = attemptStart;

        while (true)
        {
            var tick = Task.Delay(_options.TelemetryInterval, _time, ct);
            Task done;
            if (secondaryOutputs.Count == 0)
            {
                done = await Task.WhenAny(process.Exited, lease.Completion, tick).ConfigureAwait(false);
            }
            else
            {
                var waitTasks = new List<Task>(4 + secondaryOutputs.Count)
                {
                    process.Exited,
                    lease.Completion,
                    tick,
                    secondaryWakeup.WaitAsync(ct),
                };
                foreach (var r in secondaryOutputs)
                {
                    if (!r.HasFailed)
                    {
                        waitTasks.Add(r.Exited);
                    }
                }

                done = await Task.WhenAny(waitTasks).ConfigureAwait(false);
            }

            if (ct.IsCancellationRequested)
            {
                // Stop producers first so every output ends on the exact same video frame and audio slice,
                // then close pipes (EOF) so no FFmpeg blocks on ReadFile while finalizing its container.
                await scheduler.DisposeAsync().ConfigureAwait(false);
                await mixer.DisposeAsync().ConfigureAwait(false);
                await primaryVideoPipe.DisposeAsync().ConfigureAwait(false);
                await primaryAudioPipe.DisposeAsync().ConfigureAwait(false);

                var stops = new List<Task>(1 + secondaryOutputs.Count) { process.StopAsync(_options.GracefulStopTimeout) };
                foreach (var r in secondaryOutputs)
                {
                    if (!r.HasFailed && !r.Exited.IsCompleted)
                    {
                        stops.Add(r.StopAsync(_options.GracefulStopTimeout));
                    }
                }

                await Task.WhenAll(stops).ConfigureAwait(false);
                return null;
            }

            if (done == process.Exited)
            {
                await Task.WhenAny(lease.Completion, Task.Delay(500, CancellationToken.None)).ConfigureAwait(false);
                if (_logger is not null)
                {
                    LogEncoderExited(_logger, process.Exited.Result, Short(_redactor.Redact(string.Join(" | ", recent.TakeLast(4)))), lease.Completion.IsCompleted);
                }

                return LeaseError(lease) ?? ErrorClassifier.ClassifyExit(process.Exited.Result, [.. recent]);
            }

            if (done == lease.Completion)
            {
                if (_logger is not null)
                {
                    LogOutputEnded(_logger, lease.Completion.IsFaulted ? Short(_redactor.Redact(lease.Completion.Exception?.GetBaseException().Message)) : "closed without an error");
                }

                process.Kill();
                return LeaseError(lease) ?? new StreamError(ErrorCategory.Network, "network.closed", "The server closed the connection.");
            }

            if (secondaryOutputs.Count > 0)
            {
                while (secondaryWakeup.CurrentCount > 0)
                {
                    secondaryWakeup.Wait(0, CancellationToken.None);
                }

                await HandleSecondaryOutputsAsync(secondaryOutputs, mixer).ConfigureAwait(false);
                if (done != tick)
                {
                    continue;
                }
            }

            var now = _time.GetTimestamp();
            var progress = latestProgress();
            var net = lease.Snapshot();

            if (progress is not null && progress.Frame > lastFrame)
            {
                lastFrame = progress.Frame;
                lastFrameAdvance = now;
            }

            if (net.BytesSent > lastBytes)
            {
                lastBytes = net.BytesSent;
                lastBytesAdvance = now;
            }

            if (_state.Current == StreamState.Connecting)
            {
                var isLive = broadcast
                    ? net.BytesSent >= _options.MinBytesForLive && progress is { Frame: > 0 }
                    : (progress?.Frame ?? 0) > primaryEncoder.Fps;

                if (isLive)
                {
                    liveSince = now;
                    _liveSince = now;
                    if (_logger is not null)
                    {
                        LogWentLive(_logger, _time.GetElapsedTime(attemptStart, now).TotalSeconds, net.BytesSent, progress?.Frame ?? 0);
                    }

                    _state.TryTransition(StreamState.Live);
                }
                else if (_time.GetElapsedTime(attemptStart, now) > _options.ConnectTimeout)
                {
                    if (_logger is not null)
                    {
                        LogConnectTimeout(_logger, _time.GetElapsedTime(attemptStart, now).TotalSeconds, net.BytesSent, progress?.Frame ?? 0, net.Connected);
                    }

                    process.Kill();
                    return new StreamError(ErrorCategory.Network, "network.connect_timeout", "Connection attempt timed out waiting for ingest server.");
                }
            }
            else if (_state.Current == StreamState.Live && liveSince is { } since)
            {
                foreach (var backoff in backoffs)
                {
                    backoff.ReportStable(_time.GetElapsedTime(since, now));
                }

                var frameStall = _time.GetElapsedTime(lastFrameAdvance, now) > _options.StallTimeout;
                var byteStall = lease.IsMeasured && _time.GetElapsedTime(lastBytesAdvance, now) > _options.StallTimeout;
                if (frameStall || byteStall)
                {
                    if (_logger is not null)
                    {
                        LogStalled(_logger, frameStall ? "no new frames" : "no bytes sent", _time.GetElapsedTime(frameStall ? lastFrameAdvance : lastBytesAdvance, now).TotalSeconds, net.BufferedBytes);
                    }

                    process.Kill();
                    return new StreamError(
                        ErrorCategory.Network,
                        "network.stalled",
                        byteStall ? "The connection stopped sending data." : "The stream stopped making progress.");
                }
            }

            PublishTelemetry(primaryEncoder.Fps, progress, net, liveSince is { } s2 ? _time.GetElapsedTime(s2, now) : TimeSpan.Zero);
        }
    }

    private async Task TryStartSecondaryRecordingAsync(
        string name,
        string canvasName,
        string path,
        EncoderSettings encoder,
        MediaOutputScheduler scheduler,
        UnifiedAudioMixer mixer,
        List<RecordingOutput> secondaryOutputs,
        List<string> savedRecordingPaths,
        CancellationToken ct)
    {
        RecordingOutput? output = null;
        try
        {
            output = RecordingOutput.Start(
                _deviceManager,
                _ffmpeg.Path,
                _redactor,
                name,
                path,
                encoder,
                line => DiagnosticLine?.Invoke(this, line),
                _logger);

            scheduler.AddTarget(canvasName, output.Target);
            var error = await output.HandshakeAsync(scheduler, _options.ConnectTimeout, ct).ConfigureAwait(false);
            if (error is not null)
            {
                if (_logger is not null)
                {
                    LogSecondaryRecordingFailed(_logger, name, error.Code, error.UserMessage);
                }

                output.Target.TryDetach();
                await output.DisposeAsync().ConfigureAwait(false);
                RecordingFailed?.Invoke(this, new RecordingFailedEventArgs(name, error));
                return;
            }

            mixer.AddSink(output.Name, output.AudioPipe);
            secondaryOutputs.Add(output);
            savedRecordingPaths.Add(output.Path);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (output is not null)
            {
                output.Target.TryDetach();
                await output.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
        catch (StreamFailureException ex)
        {
            if (output is not null)
            {
                output.Target.TryDetach();
                await output.DisposeAsync().ConfigureAwait(false);
            }

            if (_logger is not null)
            {
                LogSecondaryRecordingFailed(_logger, name, ex.Error.Code, ex.Error.UserMessage);
            }

            RecordingFailed?.Invoke(this, new RecordingFailedEventArgs(name, ex.Error));
        }
    }

    private async Task HandleSecondaryOutputsAsync(List<RecordingOutput> secondaryOutputs, UnifiedAudioMixer mixer)
    {
        foreach (var r in secondaryOutputs)
        {
            if (r.HasFailed)
            {
                continue;
            }

            if (!r.Exited.IsCompleted && r.Target.IsAttached && r.DetachReason is null)
            {
                continue;
            }

            r.Target.TryDetach();
            mixer.RemoveSink(r.Name);
            if (!r.Exited.IsCompleted)
            {
                await r.StopAsync(_options.GracefulStopTimeout).ConfigureAwait(false);
            }

            if (r.TryMarkFailed())
            {
                var error = r.Exited.IsCompleted
                    ? ClassifySecondaryExit(r)
                    : new StreamError(ErrorCategory.Process, "recording.pipe", "The recording stopped unexpectedly.", r.DetachReason);
                if (_logger is not null)
                {
                    LogSecondaryRecordingFailed(_logger, r.Name, error.Code, error.UserMessage);
                }

                RecordingFailed?.Invoke(this, new RecordingFailedEventArgs(r.Name, error));
            }
        }
    }

    private static StreamError ClassifySecondaryExit(RecordingOutput output)
    {
        var exitCode = output.Exited.Result;
        var classified = ErrorClassifier.ClassifyExit(exitCode, output.RecentLines);
        if (classified.Code != "process.exit")
        {
            return classified;
        }

        return new StreamError(
            ErrorCategory.Process,
            "recording.exit",
            $"The recording encoder exited unexpectedly (exit code {exitCode}).",
            classified.Detail);
    }

    private static (int Width, int Height) ResolveMainCanvasSize(StreamSession session, EncoderSettings primaryEncoder)
    {
        if (session.CanvasWidth > 0 && session.CanvasHeight > 0)
        {
            return (session.CanvasWidth, session.CanvasHeight);
        }

        var w = primaryEncoder.Width;
        var h = primaryEncoder.Height;
        if (session.Recording?.Encoder is { } rec && (long)rec.Width * rec.Height > (long)w * h)
        {
            w = rec.Width;
            h = rec.Height;
        }

        return (w, h);
    }

    private static string FormatOutputsSummary(string primaryName, EncoderSettings primaryEncoder, List<RecordingOutput> secondaryOutputs)
    {
        var parts = new List<string>(1 + secondaryOutputs.Count)
        {
            string.Create(CultureInfo.InvariantCulture, $"{primaryName} {primaryEncoder.Width}x{primaryEncoder.Height} {primaryEncoder.VideoKbps} kbps ({primaryEncoder.EncoderId})"),
        };
        foreach (var r in secondaryOutputs)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{r.Name} {r.Encoder.Width}x{r.Encoder.Height} {r.Encoder.VideoKbps} kbps ({r.Encoder.EncoderId})"));
        }

        return string.Join("; ", parts);
    }

    private void SetupSources(StreamSession session, int canvasWidth, int canvasHeight)
    {
        // After a live switch, the captures the stream has now, not those it started with.
        var bindings = _liveCaptures is { } live ? live
            : session.Captures.Count > 0 ? session.Captures
            : session.Video is not BlankCanvasSource
                ? [new CaptureBinding("capture", session.Video, session.BasePlacement, session.BaseCrop, session.BaseFit, !session.ShowCapture)]
                : [];

        foreach (var b in bindings)
        {
            if (_sources.ContainsKey(b.Key))
            {
                continue;
            }

            var source = CreateSceneSource(b.Key, b.Source);
            if (source is not null)
            {
                _sources[b.Key] = source;
            }
        }

        if (_cameraService is not null)
        {
            foreach (var b in session.CameraLayers)
            {
                if (_sources.ContainsKey(b.Layer.LayerId))
                {
                    continue;
                }

                var px = LayerGeometry.ToPixels(b.Placement, canvasWidth, canvasHeight);
                var camWidth = px.Width;
                var camHeight = px.Height;
                if (session.VerticalRecording is { } vert
                    && vert.Layout.Placements.TryGetValue(b.Layer.LayerId, out var vPlacement))
                {
                    var vPx = LayerGeometry.ToPixels(vPlacement, vert.Encoder.Width, vert.Encoder.Height);
                    camWidth = Math.Max(camWidth, vPx.Width);
                    camHeight = Math.Max(camHeight, vPx.Height);
                }

                var camSource = new CameraD3D11SceneSource(
                    b.Layer.LayerId,
                    _deviceManager,
                    b.Layer,
                    _cameraService,
                    Math.Max(64, camWidth),
                    Math.Max(64, camHeight),
                    _segmentation,
                    _backgroundImages,
                    _framing,
                    _audioLevels,
                    _logger);
                _sources[b.Layer.LayerId] = camSource;
            }
        }

        foreach (var overlay in session.Overlays)
        {
            if (_sources.ContainsKey(overlay.LayerId))
            {
                continue;
            }

            var px = LayerGeometry.ToPixels(overlay.Placement, canvasWidth, canvasHeight);
            int w = Math.Max(64, px.Width);
            int h = Math.Max(64, px.Height);

            var src = OverlaySourceFactory?.Invoke(overlay, w, h);
            if (src is null && overlay.Source is ImageOverlaySource img)
            {
                src = new ImageD3D11SceneSource(overlay.LayerId, _deviceManager, img.Path, _backgroundImages, _logger);
            }

            if (src is not null)
            {
                _sources[overlay.LayerId] = src;
            }
        }

        foreach (var media in session.Media)
        {
            if (_sources.ContainsKey(media.LayerId))
            {
                continue;
            }

            var src = MediaSourceFactory?.Invoke(media, canvasWidth, canvasHeight);
            if (src is null && !string.IsNullOrWhiteSpace(media.FilePath))
            {
                src = new ImageD3D11SceneSource(media.LayerId, _deviceManager, media.FilePath, _backgroundImages, _logger);
            }

            if (src is not null)
            {
                _sources[media.LayerId] = src;
            }
        }
    }

    private WindowsGraphicsCaptureSource? CreateSceneSource(string key, VideoSourceSpec spec)
    {
        try
        {
            switch (spec)
            {
                case WindowSource w:
                    return new WindowsGraphicsCaptureSource(
                        key,
                        _deviceManager,
                        checked((nint)w.WindowHandle),
                        isWindow: true,
                        captureCursor: w.CaptureCursor,
                        logger: _logger);

                case DisplaySource d:
                    nint hMon = 0;
                    if (d.MonitorHandle is { } h && h != 0)
                    {
                        hMon = checked((nint)h);
                    }
                    else
                    {
                        var monitors = VideoSourceCatalog.ListMonitors();
                        MonitorInfo? match = null;
                        for (int i = 0; i < monitors.Count; i++)
                        {
                            if (monitors[i].Index == d.MonitorIndex)
                            {
                                match = monitors[i];
                                break;
                            }
                        }

                        if (match is null)
                        {
                            for (int i = 0; i < monitors.Count; i++)
                            {
                                if (monitors[i].IsPrimary)
                                {
                                    match = monitors[i];
                                    break;
                                }
                            }
                        }

                        match ??= monitors.Count > 0 ? monitors[0] : null;

                        if (match is not null)
                        {
                            hMon = checked((nint)match.Handle);
                        }
                    }

                    if (hMon != 0)
                    {
                        return new WindowsGraphicsCaptureSource(
                            key,
                            _deviceManager,
                            hMon,
                            isWindow: false,
                            captureCursor: d.CaptureCursor,
                            logger: _logger);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            if (_logger is not null)
            {
                LogSourceInitFailed(_logger, key, ex);
            }
        }

        return null;
    }

    public bool ReplaceSources(IReadOnlyList<CaptureBinding> captures, IReadOnlyList<AudioSourceSpec> audio, IReadOnlyList<string>? stack = null)
    {
        ArgumentNullException.ThrowIfNull(captures);
        ArgumentNullException.ThrowIfNull(audio);

        lock (_gate)
        {
            if (!StreamStateMachine.IsActive(_state.Current) || _state.Current == StreamState.Stopping)
            {
                return false;
            }

            _liveCaptures = [.. captures];
            foreach (var a in audio)
            {
                _liveAudio.TryAdd(ChannelOf(a), a);
            }

            var activeKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in captures)
            {
                activeKeys.Add(c.Key);
                if (!_sources.ContainsKey(c.Key))
                {
                    var src = CreateSceneSource(c.Key, c.Source);
                    if (src is not null)
                    {
                        _sources[c.Key] = src;
                        if (_logger is not null)
                        {
                            LogCaptureAdded(_logger, c.Key, src.State.ToString(), src.Width, src.Height, !c.StartHidden);
                        }
                    }
                    else if (_logger is not null)
                    {
                        LogCaptureNotCreated(_logger, c.Key);
                    }
                }

                lock (_layoutLock)
                {
                    if (!c.StartHidden)
                    {
                        _layerPlacements[c.Key] = c.Placement;
                    }
                    else
                    {
                        _layerPlacements.TryRemove(c.Key, out _);
                    }
                    _layerCrops[c.Key] = c.Crop;
                }
            }

            foreach (var key in _sources.Keys.ToArray())
            {
                if (_sources.TryGetValue(key, out var oldSource) && oldSource is WindowsGraphicsCaptureSource)
                {
                    if (!activeKeys.Contains(key) && key != "capture")
                    {
                        if (_sources.TryRemove(key, out _))
                        {
                            _ = oldSource.DisposeAsync().AsTask();
                            if (_logger is not null)
                            {
                                LogCaptureRemoved(_logger, key);
                            }
                        }
                        _frameCache?.Remove(key);
                        lock (_layoutLock)
                        {
                            _layerPlacements.TryRemove(key, out _);
                            _layerCrops.TryRemove(key, out _);
                        }
                    }
                }
            }

            if (_audioMixer is not null)
            {
                foreach (var a in audio)
                {
                    var ch = ChannelOf(a);
                    if (_audioMixer.HasChannel(ch))
                    {
                        continue;
                    }

                    try
                    {
                        _audioMixer.AddSource(ch, a, _liveGains.TryGetValue(ch, out var liveGain) ? liveGain : a.Gain);
                    }
                    catch (Exception ex)
                    {
                        // Source already registered or optional
                        if (_logger is not null)
                        {
                            LogAudioNotAdded(_logger, ch, ex.Message);
                        }
                    }
                }
            }
            if (stack is not null && stack.Count > 0)
            {
                lock (_layoutLock)
                {
                    _stackOrder = [.. stack];
                }
            }
        }

        return true;
    }

    public bool HasCapture(string key) => _sources.ContainsKey(key);

    /// <summary>
    /// For the stats line: how far the sound trails the clock the picture is paced by, and how many frames the
    /// render loop had to skip. Null when no stream is running.
    /// </summary>
    public string? SyncInfo()
    {
        var mixer = _audioMixer;
        var scheduler = _scheduler;
        if (mixer is null && scheduler is null)
        {
            return null;
        }

        var parts = new List<string>();
        if (mixer is not null)
        {
            parts.Add(mixer.Describe());
        }

        if (scheduler is not null)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"picture: {scheduler.FramesWritten} frames out, render loop skipped ahead {scheduler.FramesDropped} time(s), {scheduler.LostTime.TotalMilliseconds:0} ms of picture lost, {scheduler.SlowFrames} slow frame(s) (longest {scheduler.LongestFrame.TotalMilliseconds:0} ms)"));
        }

        foreach (var rec in _secondaryOutputs)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{rec.Name}: {rec.Target.FramesWritten} frames out{(rec.HasFailed ? " (stopped)" : string.Empty)}"));
        }

        return string.Join("; ", parts);
    }

    public Task<bool> SetCaptureAsync(string key, NormalizedRect? placement, NormalizedRect crop, BaseFit fit, CancellationToken cancellationToken = default)
    {
        NoteLiveChange(key, placement is { } shown ? $"placed at {shown}, crop {crop}, {fit}" : "hidden");
        lock (_layoutLock)
        {
            _currentLayout = _currentLayout with
            {
                BasePlacement = placement ?? new NormalizedRect(-2, -2, 0, 0),
                ShowCapture = placement is not null,
                BaseCrop = crop,
                BaseFit = fit,
            };

            if (placement is { } p)
            {
                _layerPlacements[key] = p;
                _layerCrops[key] = crop;
            }
            else
            {
                _layerPlacements.TryRemove(key, out _);
            }
        }
        return Task.FromResult(true);
    }

    public Task<bool> SetBaseAsync(NormalizedRect? placement, NormalizedRect crop, BaseFit fit, CancellationToken cancellationToken = default) =>
        SetCaptureAsync("capture", placement, crop, fit, cancellationToken);

    public Task<bool> SetCanvasLayoutAsync(string canvasName, CanvasLayout layout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!string.Equals(canvasName, "vertical", StringComparison.Ordinal) || _verticalLayout is null)
        {
            return Task.FromResult(false);
        }

        _verticalLayout = layout;
        return Task.FromResult(true);
    }

    public Task<bool> MoveLayerAsync(string layerId, NormalizedRect placement, CancellationToken cancellationToken = default)
    {
        NoteLiveChange(layerId, $"moved to {placement}");
        lock (_layoutLock)
        {
            _currentLayout = _currentLayout.WithPlacement(layerId, placement);
            _layerPlacements[layerId] = placement;
        }
        return Task.FromResult(true);
    }

    public Task<bool> HideLayerAsync(string layerId, CancellationToken cancellationToken = default)
    {
        NoteLiveChange(layerId, "hidden");
        lock (_layoutLock)
        {
            _currentLayout = _currentLayout.Without(layerId);
            _layerPlacements.TryRemove(layerId, out _);
        }
        return Task.FromResult(true);
    }

    public Task<bool> SetLayerCropAsync(string layerId, NormalizedRect crop, CancellationToken cancellationToken = default)
    {
        NoteLiveChange(layerId, $"cropped to {crop}");
        _layerCrops[layerId] = crop;
        return Task.FromResult(true);
    }

    public Task<bool> SetGainAsync(string channel, double gain, CancellationToken cancellationToken = default)
    {
        if (StreamStateMachine.IsActive(_state.Current))
        {
            _liveGains[channel] = gain; // kept for a reconnect's new mixer
        }

        if (_audioMixer is not null)
        {
            return _audioMixer.SetGainAsync(channel, gain, cancellationToken);
        }
        return Task.FromResult(false);
    }

    private volatile NoiseRemoval _noiseRemoval;

    public Task<bool> SetNoiseSuppressionAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SetNoiseRemovalAsync(enabled ? NoiseRemoval.Standard : NoiseRemoval.Off, cancellationToken);

    public Task<bool> SetNoiseRemovalAsync(NoiseRemoval level, CancellationToken cancellationToken = default)
    {
        _noiseRemoval = level;
        if (_logger is not null && StreamStateMachine.IsActive(_state.Current))
        {
            LogNoiseRemovalChanged(_logger, level.ToString());
        }

        if (_audioMixer is not null)
        {
            return _audioMixer.SetNoiseRemovalAsync(level, cancellationToken);
        }
        return Task.FromResult(true);
    }

    private double _microphoneBoostDb;

    public Task<bool> SetMicrophoneBoostAsync(double db, CancellationToken cancellationToken = default)
    {
        _microphoneBoostDb = db; // kept for a reconnect's new mixer
        if (_audioMixer is not null)
        {
            return _audioMixer.SetMicrophoneBoostAsync(db, cancellationToken);
        }

        return Task.FromResult(true);
    }

    private static string ChannelOf(AudioSourceSpec a) => a.Channel ?? (a is MicrophoneSource ? AudioChannels.Microphone : AudioChannels.System);

    /// <summary>
    /// Logs a change to the running stream's layout, and warns when the layer is not in the stream: the change
    /// then shows nowhere, which is what a streamer sees as "my move did not apply".
    /// </summary>
    private void NoteLiveChange(string layerId, string what)
    {
        if (_logger is null || !StreamStateMachine.IsActive(_state.Current))
        {
            return;
        }

        if (_sources.ContainsKey(layerId))
        {
            LogLiveChange(_logger, layerId, what);
        }
        else
        {
            LogLiveChangeNoSource(_logger, layerId, what, string.Join(", ", _sources.Keys.Order(StringComparer.Ordinal)));
        }
    }

    /// <summary>
    /// Sources live as long as the engine, so a new stream reuses them. Those the new stream doesn't have are
    /// released, and overlays and scene pictures are made again: they are drawn once when made, so an overlay
    /// changed since the last stream (another picture, another page size) showed as it was before.
    /// </summary>
    private async Task PruneStaleSourcesAsync(StreamSession session)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal);
        if (session.Captures.Count > 0)
        {
            keep.UnionWith(session.Captures.Select(c => c.Key));
        }
        else if (session.Video is not BlankCanvasSource)
        {
            keep.Add("capture");
        }

        keep.UnionWith(session.CameraLayers.Select(b => b.Layer.LayerId));
        var remade = new HashSet<string>(session.Overlays.Select(o => o.LayerId).Concat(session.Media.Select(m => m.LayerId)), StringComparer.Ordinal);

        foreach (var key in _sources.Keys.ToArray())
        {
            if (keep.Contains(key) && !remade.Contains(key))
            {
                continue;
            }

            if (_sources.TryRemove(key, out var stale))
            {
                _frameCache?.Remove(key);
                try
                {
                    await stale.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
                {
                    // Already gone with its device or window.
                }

                if (_logger is not null)
                {
                    LogSourceReleased(_logger, key);
                }
            }
        }
    }

    private void PublishTelemetry(StreamSession session, EncoderProgress? progress, NetworkSnapshot? net, TimeSpan? connectionUptime)
    {
        var primaryFps = (session.Broadcast ? session.Encoder : session.Recording?.Encoder ?? session.Encoder).Fps;
        PublishTelemetry(primaryFps, progress, net, connectionUptime);
    }

    private void PublishTelemetry(int fps, EncoderProgress? progress, NetworkSnapshot? net, TimeSpan? connectionUptime)
    {
        var telemetry = new StreamTelemetry(
            _state.Current,
            _time.GetElapsedTime(_sessionStart),
            connectionUptime ?? TimeSpan.Zero,
            fps,
            progress,
            net,
            _reconnectCount,
            _lastError);

        TelemetryUpdated?.Invoke(this, telemetry);
    }

    private static StreamError? LeaseError(IOutputLease lease)
    {
        if (lease.Completion.IsFaulted)
        {
            var ex = lease.Completion.Exception?.GetBaseException();
            return new StreamError(ErrorCategory.Network, "network.closed", "The stream destination closed the connection.", ex?.Message);
        }
        return null;
    }

    private sealed class RecordOnlyLease : IOutputLease
    {
        public string PublishUrl => string.Empty;
        public string TcUrl => string.Empty;
        public bool IsMeasured => false;
        public Task Completion { get; } = new TaskCompletionSource().Task;
        public NetworkSnapshot Snapshot() => new(0, 0, 0, null, null, null, false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static StreamError EarlyExitError(FfmpegProcess process, ConcurrentQueue<string> recentLines)
    {
        var exitCode = process.ExitCode ?? -1;
        var lines = recentLines.ToArray();
        var classified = ErrorClassifier.ClassifyExit(exitCode, lines);
        if (classified.Code != "process.exit")
        {
            return classified;
        }

        var stderr = string.Join(Environment.NewLine, lines);
        return new StreamError(
            ErrorCategory.Process,
            "ffmpeg.early_exit",
            "FFmpeg exited unexpectedly during startup.",
            string.IsNullOrWhiteSpace(stderr) ? $"Exit code {exitCode}." : $"Exit code {exitCode}: {stderr}");
    }

    private static string Short(string? text, int max = 400) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Length <= max ? text : text[..max] + "…";

    [LoggerMessage(LogLevel.Information, "Stream attempt {Attempt} starts ({Reconnects} reconnects so far).")]
    private static partial void LogAttemptStarted(ILogger logger, int attempt, int reconnects);

    [LoggerMessage(LogLevel.Information, "Stream attempt {Attempt} ended after {Seconds:0.0} s: {Result}. {Detail}")]
    private static partial void LogAttemptEnded(ILogger logger, int attempt, double seconds, string result, string detail);

    [LoggerMessage(LogLevel.Information, "Reconnect {Count}: trying again in {Delay:0.0} s.")]
    private static partial void LogRetry(ILogger logger, int count, double delay);

    [LoggerMessage(LogLevel.Warning, "Giving up: {Error} (retryable: {Retryable}).")]
    private static partial void LogGaveUp(ILogger logger, string error, bool retryable);

    [LoggerMessage(LogLevel.Information, "Stream is live {Seconds:0.0} s after the attempt began ({Bytes} bytes sent, {Frames} frames encoded).")]
    private static partial void LogWentLive(ILogger logger, double seconds, long bytes, long frames);

    [LoggerMessage(LogLevel.Warning, "Connecting took longer than allowed ({Seconds:0.0} s, {Bytes} bytes sent, {Frames} frames encoded, connected: {Connected}).")]
    private static partial void LogConnectTimeout(ILogger logger, double seconds, long bytes, long frames, bool connected);

    [LoggerMessage(LogLevel.Warning, "Encoder FFmpeg exited with code {Code} (output ended: {OutputEnded}). Last messages: {Lines}")]
    private static partial void LogEncoderExited(ILogger logger, int code, string lines, bool outputEnded);

    [LoggerMessage(LogLevel.Warning, "The output to the server ended: {Reason}")]
    private static partial void LogOutputEnded(ILogger logger, string reason);

    [LoggerMessage(LogLevel.Warning, "Stream stalled: {Kind} for {Seconds:0.0} s ({Buffered} bytes waiting to be sent).")]
    private static partial void LogStalled(ILogger logger, string kind, double seconds, long buffered);

    [LoggerMessage(LogLevel.Information, "Capture '{Key}' added to the running stream ({State}, {Width}x{Height}, shown: {Shown}).")]
    private static partial void LogCaptureAdded(ILogger logger, string key, string state, int width, int height, bool shown);

    [LoggerMessage(LogLevel.Warning, "Capture '{Key}' could not be created, so it will not appear in the stream.")]
    private static partial void LogCaptureNotCreated(ILogger logger, string key);

    [LoggerMessage(LogLevel.Information, "Capture '{Key}' removed from the running stream.")]
    private static partial void LogCaptureRemoved(ILogger logger, string key);

    [LoggerMessage(LogLevel.Warning, "Sound '{Channel}' could not be added to the running stream: {Reason}")]
    private static partial void LogAudioNotAdded(ILogger logger, string channel, string reason);

    [LoggerMessage(LogLevel.Debug, "Live layout: '{Layer}' {What}.")]
    private static partial void LogLiveChange(ILogger logger, string layer, string what);

    [LoggerMessage(LogLevel.Warning, "Live layout: '{Layer}' {What}, but that layer is not in the running stream, so viewers don't see it (the stream has: {Keys}).")]
    private static partial void LogLiveChangeNoSource(ILogger logger, string layer, string what, string keys);

    [LoggerMessage(LogLevel.Information, "Reconnect keeps the live layout: {Count} layer(s) shown ({Layers}).")]
    private static partial void LogLayoutKept(ILogger logger, int count, string layers);

    [LoggerMessage(LogLevel.Information, "Sound for this connection: {Channels}; microphone noise removal {NoiseRemoval}.")]
    private static partial void LogAudioStarted(ILogger logger, string channels, string noiseRemoval);

    [LoggerMessage(LogLevel.Information, "Microphone noise removal switched to {Level} while live.")]
    private static partial void LogNoiseRemovalChanged(ILogger logger, string level);

    [LoggerMessage(LogLevel.Information, "The server refused the new connection right after the previous one dropped. Servers often hold the old one for some seconds; the next tries wait longer.")]
    private static partial void LogRefusedAfterDrop(ILogger logger);

    [LoggerMessage(LogLevel.Debug, "Source '{Key}' released: the new stream does not use it as it was.")]
    private static partial void LogSourceReleased(ILogger logger, string key);

    [LoggerMessage(LogLevel.Error, "Unexpected error occurred during D3D11 streaming session.")]
    private static partial void LogUnexpected(ILogger logger, Exception ex);

    [LoggerMessage(LogLevel.Warning, "Failed to initialize capture source '{Key}'.")]
    private static partial void LogSourceInitFailed(ILogger logger, string key, Exception ex);

    [LoggerMessage(LogLevel.Information, "Outputs: {Summary}")]
    private static partial void LogOutputsStarted(ILogger logger, string summary);

    [LoggerMessage(LogLevel.Warning, "Secondary recording '{Name}' stopped ({Code}): {Message}")]
    private static partial void LogSecondaryRecordingFailed(ILogger logger, string name, string code, string message);

    [LoggerMessage(LogLevel.Information, "Vertical recording skipped: the stream is already vertical.")]
    private static partial void LogVerticalSkipped(ILogger logger);
}

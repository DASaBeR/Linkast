// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Linkast.Core;
using Linkast.Core.Audio;
using Linkast.Core.Camera;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Linkast.Engine.FFmpeg;

/// <summary>Health and timing thresholds. Defaults suit real streaming; tests may shorten them.</summary>
public sealed record EngineOptions
{
    /// <summary>Maximum time from launch to a healthy stream before the attempt is treated as failed.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Time without encoder progress (or socket progress) before a live stream counts as stalled.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Frames and bytes must have advanced within this window to count as healthy.</summary>
    public TimeSpan HealthWindow { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Bytes the relay must have forwarded before Live is declared (well past the RTMP handshake).</summary>
    public long MinBytesForLive { get; init; } = 32 * 1024;

    public TimeSpan GracefulStopTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan TelemetryInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Restarts after Desktop Duplication loses the display. Kept apart from the network budget and
    /// quicker at first; long enough in total (about 45 s) to wait out a UAC prompt or the lock screen.
    /// </summary>
    public ReconnectPolicy CaptureRestart { get; init; } = new(15, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(30));

    /// <summary>How long a captured window must stay black before telemetry says so; loading screens are shorter.</summary>
    public TimeSpan BlackAfter { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// <see cref="IStreamEngine"/> backed by a supervised ffmpeg.exe. Owns the session state machine,
/// health evaluation ("process running" is never treated as "stream healthy"), and bounded reconnect.
/// </summary>
public sealed partial class FfmpegStreamEngine : IStreamEngine, ILiveSceneControl, ILiveAudioControl
{
    private const int RecentLineCapacity = 60;

    /// <summary>Highest frame rate camera layers are rendered and piped at.</summary>
    public const int MaxLayerFrameRate = 30;

    private const string SourceSwitchCode = "capture.switch";

    /// <summary>A source switch only waits briefly for FFmpeg to flush; the next attempt starts right after.</summary>
    private static readonly TimeSpan SourceSwitchStopTimeout = TimeSpan.FromSeconds(1);

    private readonly FfmpegInstallation _ffmpeg;
    private readonly IOutputTransport _transport;
    private readonly IAudioInputProvider? _audioProvider;
    private readonly ILogger _logger;
    private readonly Redactor _redactor;
    private readonly EngineOptions _options;
    private readonly TimeProvider _time;
    private readonly StreamStateMachine _state = new();
    private readonly Lock _gate = new();

    private readonly IVideoLayerInputProvider? _layerProvider;
    private readonly IOverlayInputProvider? _overlayProvider;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private ActiveComposition? _composition;

    // Live audio changes (mute, volume, noise removal) for the current session, re-applied on reconnect.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> _liveGain = new(StringComparer.Ordinal);
    private bool? _liveNoiseSuppression;

    // Live moves and hides of layers for the current session (null = hidden), re-applied on reconnect.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, NormalizedRect?> _livePlacement = new(StringComparer.Ordinal);

    // Live trims of layers for the current session, re-applied on reconnect.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, NormalizedRect> _liveCrop = new(StringComparer.Ordinal);

    // Each capture's last live placement (null placement = hidden), by capture key, re-applied on reconnect.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, LiveBase> _liveBases = new(StringComparer.Ordinal);

    // Whether a scene video has sound, by file; asked of FFmpeg once per file.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _hasAudio = new(StringComparer.OrdinalIgnoreCase);
    private int _reconnectCount;

    // A new game or screen (and its sound) chosen while live; the running attempt is restarted with it.
    private LiveSources? _pendingSources;
    private TaskCompletionSource? _sourceSwitch;
    private long _sessionStart;
    private StreamError? _lastError;
    private string? _currentRecording;

    // When the black probe last saw black start in this attempt (a timestamp), or 0.
    private long _blackSince;

    public FfmpegStreamEngine(
        FfmpegInstallation ffmpeg,
        IOutputTransport transport,
        IAudioInputProvider? audioProvider = null,
        ILogger<FfmpegStreamEngine>? logger = null,
        Redactor? redactor = null,
        EngineOptions? options = null,
        TimeProvider? timeProvider = null,
        IVideoLayerInputProvider? layerProvider = null,
        IOverlayInputProvider? overlayProvider = null)
    {
        _layerProvider = layerProvider;
        _overlayProvider = overlayProvider;
        _ffmpeg = ffmpeg ?? throw new ArgumentNullException(nameof(ffmpeg));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _audioProvider = audioProvider;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _redactor = redactor ?? Redactor.Shared;
        _options = options ?? new EngineOptions();
        _time = timeProvider ?? TimeProvider.System;
        _state.Changed += (_, e) =>
        {
            LogStateChanged(e.Previous, e.Current, e.Error?.ToString());
            StateChanged?.Invoke(this, e);
        };
    }

    public event EventHandler<StreamStateChangedEventArgs>? StateChanged;

    public event EventHandler<StreamTelemetry>? TelemetryUpdated;

    public event EventHandler<string>? DiagnosticLine;

    /// <summary>Raised on the engine's thread when a recording file is closed (one per connection attempt).</summary>
    public event EventHandler<string>? RecordingSaved;

    /// <summary>The file being recorded into right now, or null.</summary>
    public string? CurrentRecording => Volatile.Read(ref _currentRecording);

    public StreamState State => _state.Current;

    public FfmpegInstallation Installation => _ffmpeg;

    public Task StartAsync(StreamSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            if (StreamStateMachine.IsActive(_state.Current))
            {
                throw new InvalidOperationException("A stream is already running.");
            }

            if (session.Broadcast)
            {
                _redactor.Register(session.Destination.StreamKey);
            }

            _state.Transition(StreamState.Preparing);

            var errors = SessionValidator.Validate(session);
            if (errors.Count > 0)
            {
                _lastError = errors[0];
                _state.Transition(StreamState.Failed, errors[0]);
                throw new StreamFailureException(errors[0]);
            }

            _reconnectCount = 0;
            _lastError = null;
            _liveGain.Clear();
            _livePlacement.Clear();
            _liveCrop.Clear();
            _liveBases.Clear();
            _liveNoiseSuppression = null;
            _pendingSources = null;
            _sessionStart = _time.GetTimestamp();
            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();
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
    }

    /// <summary>
    /// Changes what is captured (another game, window or display) and the sounds that go with it while
    /// the stream runs. FFmpeg's capture is fixed when it starts, so the pipeline restarts at once with
    /// the new source: viewers see a pause of a second or two instead of the stream ending. Layers,
    /// live moves and volumes are kept. False when nothing is running.
    /// </summary>
    public bool ReplaceSources(IReadOnlyList<CaptureBinding> captures, IReadOnlyList<AudioSourceSpec> audio)
    {
        ArgumentNullException.ThrowIfNull(captures);
        ArgumentNullException.ThrowIfNull(audio);
        lock (_gate)
        {
            if (!StreamStateMachine.IsActive(_state.Current) || _state.Current == StreamState.Stopping)
            {
                return false;
            }

            Volatile.Write(ref _pendingSources, new LiveSources(captures, audio));
            Volatile.Read(ref _sourceSwitch)?.TrySetResult();
        }

        return true;
    }

    private sealed record LiveSources(IReadOnlyList<CaptureBinding> Captures, IReadOnlyList<AudioSourceSpec> Audio);

    /// <summary>The session's captures: its list, or the single <see cref="StreamSession.Video"/> placed by its base fields.</summary>
    private static IReadOnlyList<CaptureBinding> CapturesOf(StreamSession session) =>
        session.Captures.Count > 0 ? session.Captures
        : FfmpegArgumentBuilder.HasCapture(session.Video) ? [new CaptureBinding(SingleCaptureKey, session.Video, session.BasePlacement, session.BaseCrop, session.BaseFit, !session.ShowCapture)]
        : [];

    private const string SingleCaptureKey = "capture";

    private async Task RunSessionAsync(StreamSession session, CancellationToken ct)
    {
        var backoff = new ReconnectBackoff(session.Reconnect);
        var captureRestarts = new ReconnectBackoff(_options.CaptureRestart);
        try
        {
            while (true)
            {
                if (Interlocked.Exchange(ref _pendingSources, null) is { } sources)
                {
                    session = session with
                    {
                        Captures = sources.Captures,
                        Video = sources.Captures.Count > 0 ? sources.Captures[0].Source : new BlankCanvasSource(),
                        Audio = sources.Audio,
                    };
                }

                _state.TryTransition(StreamState.Connecting);
                var error = await RunAttemptAsync(session, [backoff, captureRestarts], ct).ConfigureAwait(false);
                if (error is null || ct.IsCancellationRequested)
                {
                    break;
                }

                if (error.Code == SourceSwitchCode)
                {
                    // Not a failure: relaunch at once with the new source, without using the retry budget.
                    _state.TryTransition(StreamState.Reconnecting);
                    continue;
                }

                _lastError = error;
                var budget = error.Category == ErrorCategory.Capture ? captureRestarts : backoff;
                if (error.IsRetryable && budget.NextDelay() is { } delay)
                {
                    _reconnectCount++;
                    LogReconnecting(budget.Attempts, budget.Policy.MaxAttempts, delay.TotalSeconds, error.ToString());
                    _state.TryTransition(StreamState.Reconnecting, error);
                    PublishTelemetry(session, null, null, null);
                    try
                    {
                        await Task.Delay(delay, _time, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    continue;
                }

                if (error.Code == StreamError.CaptureLostCode)
                {
                    error = error with { UserMessage = "Desktop Duplication could not get the display back. Check that the display is on and unlocked, then start again." };
                    _lastError = error;
                }

                _state.TryTransition(StreamState.Failed, error);
                PublishTelemetry(session, null, null, null);
                return;
            }

            _state.TryTransition(StreamState.Stopping);
            _state.TryTransition(StreamState.Stopped);
            PublishTelemetry(session, null, null, null);
        }
#pragma warning disable CA1031 // The supervisor must never crash the app; report and fail the session instead.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogUnexpected(ex);
            var error = new StreamError(ErrorCategory.Unknown, "engine.unexpected", "An unexpected error stopped the stream.", _redactor.Redact(ex.Message));
            _lastError = error;
            _state.TryTransition(StreamState.Failed, error);
        }
    }

    /// <summary>Runs one connection attempt. Returns null when stopped by the user, else the reason it ended.</summary>
    private async Task<StreamError?> RunAttemptAsync(StreamSession session, ReconnectBackoff[] backoffs, CancellationToken ct)
    {
        IOutputLease? lease = null;
        FfmpegProcess? process = null;
        var audioInputs = new List<IAudioInput>();
        var layerInputs = new List<IVideoLayerInput>();
        var recent = new ConcurrentQueue<string>();
        var parser = new ProgressParser();
        EncoderProgress? latest = null;

        try
        {
            lease = session.Broadcast
                ? await _transport.OpenAsync(session.Destination, session.Network, ct).ConfigureAwait(false)
                : new RecordOnlyLease();

            string? recordingPath = null;
            if (session.Recording is { } recording)
            {
                try
                {
                    Directory.CreateDirectory(recording.Folder);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                {
                    return new StreamError(ErrorCategory.Configuration, "recording.folder", "The recordings folder can't be created. Choose another folder.", _redactor.Redact(ex.Message));
                }

                recordingPath = RecordingFiles.NewPath(recording.Folder, _time.GetLocalNow().DateTime);
            }

            var jobAudio = new List<AudioJobInput>();
            foreach (var spec in session.Audio)
            {
                if (spec is TestToneSource)
                {
                    jobAudio.Add(new ToneAudioJobInput(spec.Gain));
                    continue;
                }

                if (_audioProvider is null || !_audioProvider.CanOpen(spec))
                {
                    return new StreamError(ErrorCategory.Audio, "audio.unsupported", $"Audio source type {spec.GetType().Name} is not supported on this system.");
                }

                var input = _audioProvider.Open(spec);
                audioInputs.Add(input);
                await input.StartAsync(ct).ConfigureAwait(false);
                jobAudio.Add(LiveAudioInput(spec, input));
            }

            var jobLayers = new List<VideoLayerJobInput>();
            var layerIds = new List<string>();
            var fixedLayers = new HashSet<int>();

            // Scene pictures and videos, directly above the captured game: FFmpeg reads the files itself.
            foreach (var media in session.Media)
            {
                if (!File.Exists(media.FilePath))
                {
                    LogMediaMissing(media.LayerId);
                    continue; // a missing file only leaves that scene without its picture
                }

                var px = PlaceLayer(session, media.LayerId, new NormalizedRect(0, 0, 1, 1), media.StartHidden);
                var sound = media.IsVideo && media.AudioChannel is not null && await HasAudioAsync(media.FilePath, ct).ConfigureAwait(false);
                fixedLayers.Add(jobLayers.Count);
                jobLayers.Add(new VideoLayerJobInput(string.Empty, session.Encoder.Width, session.Encoder.Height, 0, px)
                {
                    Kind = media.IsVideo ? LayerInputKind.LoopingVideo : LayerInputKind.Still,
                    FilePath = media.FilePath,
                    Cover = true,
                    AudioName = sound ? media.AudioChannel : null,
                    AudioGain = sound && media.AudioChannel is { } ch && _liveGain.TryGetValue(ch, out var g) ? g : media.AudioGain,
                });
                layerIds.Add(media.LayerId);
            }

            // Camera layers: rendered by Linkast, overlaid by FFmpeg, movable live through zmq.
            if (session.CameraLayers.Count > 0)
            {
                if (_layerProvider is null)
                {
                    return new StreamError(ErrorCategory.Configuration, "layers.unsupported", "Camera layers are not available in this configuration.");
                }

                // Cameras rarely deliver more than 30 fps; the overlay repeats the latest camera frame
                // for a 60 fps stream. Rendering layers at 60 fps doubled the CPU and pipe bandwidth.
                var layerFps = Math.Min(session.Encoder.Fps, MaxLayerFrameRate);
                foreach (var binding in session.CameraLayers)
                {
                    var px = PlaceLayer(session, binding.Layer.LayerId, binding.Placement, binding.StartHidden);
                    var input = _layerProvider.Open(binding.Layer, px.Width, px.Height, layerFps);
                    layerInputs.Add(input);
                    await input.StartAsync(ct).ConfigureAwait(false);
                    jobLayers.Add(new VideoLayerJobInput(input.PipePath, px.Width, px.Height, layerFps, px)
                    {
                        Crop = _liveCrop.TryGetValue(binding.Layer.LayerId, out var crop) ? crop : binding.Crop,
                    });
                    layerIds.Add(binding.Layer.LayerId);
                }
            }

            // Overlays above the cameras: pictures, web alerts and the away screen (last, so on top).
            if (session.Overlays.Count > 0)
            {
                if (_overlayProvider is null)
                {
                    return new StreamError(ErrorCategory.Configuration, "overlays.unsupported", "Overlays are not available in this configuration.");
                }

                var maxFps = Math.Min(session.Encoder.Fps, MaxLayerFrameRate);
                foreach (var overlay in session.Overlays)
                {
                    var px = PlaceLayer(session, overlay.LayerId, overlay.Placement, overlay.StartHidden);
                    var input = _overlayProvider.Open(overlay, px.Width, px.Height, maxFps);
                    layerInputs.Add(input);
                    await input.StartAsync(ct).ConfigureAwait(false);
                    // A web page renders at its own size; FFmpeg scales it into its place.
                    jobLayers.Add(new VideoLayerJobInput(input.PipePath, input.Width, input.Height, input.FrameRate, px)
                    {
                        Crop = _liveCrop.TryGetValue(overlay.LayerId, out var crop) ? crop : overlay.Crop,
                    });
                    layerIds.Add(overlay.LayerId);
                }
            }

            // Always open the command channel: layers move through it, and audio is muted and adjusted live.
            int? commandPort = FfmpegCommandChannel.FindFreePort();

            // Each capture starts where the viewer last saw it (after a reconnect), else as the session says.
            var captures = CapturesOf(session);
            var jobCaptures = captures.Select(c =>
            {
                var now = _liveBases.TryGetValue(c.Key, out var live) ? live : new LiveBase(c.StartHidden ? null : c.Placement, c.Crop, c.Fit);
                return new CaptureJobInput(c.Source, now.Placement ?? c.Placement, now.Crop, now.Fit, now.Placement is null);
            }).ToList();
            var args = FfmpegArgumentBuilder.Build(new FfmpegJob(session.Video, jobAudio, session.Encoder,
                session.Broadcast ? lease.PublishUrl : null, session.Broadcast ? lease.TcUrl : null)
            {
                RecordingPath = recordingPath,
                Layers = jobLayers,
                Captures = jobCaptures,
                CommandPort = commandPort,
            });
            LogLaunching(_redactor.Redact(string.Join(' ', args.Select(QuoteForLog))));
            Interlocked.Exchange(ref _blackSince, 0);

            process = FfmpegProcess.Start(
                _ffmpeg.Path,
                args,
                line =>
                {
                    if (parser.Feed(line) is { } p)
                    {
                        Volatile.Write(ref latest, p);
                    }
                },
                line =>
                {
                    if (BlackProbe.IsProbeLine(line))
                    {
                        if (BlackProbe.Parse(line) is { } black)
                        {
                            Interlocked.Exchange(ref _blackSince, black ? _time.GetTimestamp() : 0);
                            LogBlackProbe(black);
                        }

                        return;
                    }

                    var redacted = _redactor.Redact(line);
                    recent.Enqueue(redacted);
                    while (recent.Count > RecentLineCapacity && recent.TryDequeue(out _))
                    {
                    }

                    LogFfmpegLine(redacted);
                    DiagnosticLine?.Invoke(this, redacted);
                });

            Volatile.Write(ref _currentRecording, recordingPath);
            if (commandPort is { } port)
            {
                Volatile.Write(ref _composition, new ActiveComposition(new FfmpegCommandChannel(port), layerIds, fixedLayers,
                    [.. captures.Select(c => c.Key)], session.Encoder.Width, session.Encoder.Height));
            }

            return await SuperviseAsync(session, backoffs, lease,process, audioInputs, recent, () => Volatile.Read(ref latest), ct).ConfigureAwait(false);
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
            Interlocked.Exchange(ref _blackSince, 0);
            Interlocked.Exchange(ref _composition, null)?.Commands.Dispose();
            if (process is not null)
            {
                await process.DisposeAsync().ConfigureAwait(false);
            }

            if (Interlocked.Exchange(ref _currentRecording, null) is { } saved && File.Exists(saved))
            {
                RecordingSaved?.Invoke(this, saved);
            }

            foreach (var l in layerInputs)
            {
                await l.DisposeAsync().ConfigureAwait(false);
            }

            foreach (var a in audioInputs)
            {
                await a.DisposeAsync().ConfigureAwait(false);
            }

            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<StreamError?> SuperviseAsync(
        StreamSession session,
        ReconnectBackoff[] backoffs,
        IOutputLease lease,
        FfmpegProcess process,
        List<IAudioInput> audioInputs,
        ConcurrentQueue<string> recent,
        Func<EncoderProgress?> latestProgress,
        CancellationToken ct)
    {
        var attemptStart = _time.GetTimestamp();
        long? liveSince = null;
        long lastFrame = -1, lastBytes = -1;
        var lastFrameAdvance = attemptStart;
        var lastBytesAdvance = attemptStart;
        var audioFault = FirstFault(audioInputs.Select(a => a.Completion));
        var sourceSwitch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _sourceSwitch, sourceSwitch);
        if (Volatile.Read(ref _pendingSources) is not null)
        {
            sourceSwitch.TrySetResult(); // chosen while this attempt was starting
        }

        while (true)
        {
            var tick = Task.Delay(_options.TelemetryInterval, _time, ct);
            var done = await Task.WhenAny(process.Exited, lease.Completion, audioFault, sourceSwitch.Task, tick).ConfigureAwait(false);

            if (ct.IsCancellationRequested)
            {
                await process.StopAsync(_options.GracefulStopTimeout).ConfigureAwait(false);
                return null;
            }

            if (done == sourceSwitch.Task)
            {
                await process.StopAsync(SourceSwitchStopTimeout).ConfigureAwait(false);
                return new StreamError(ErrorCategory.Capture, SourceSwitchCode, "Switching to the new capture source.");
            }

            if (done == process.Exited)
            {
                // The relay usually knows the precise network reason; give it a moment to report.
                await Task.WhenAny(lease.Completion, Task.Delay(500, CancellationToken.None)).ConfigureAwait(false);
                return LeaseError(lease) ?? ErrorClassifier.ClassifyExit(process.Exited.Result, [.. recent]);
            }

            if (done == lease.Completion)
            {
                process.Kill();
                return LeaseError(lease) ?? new StreamError(ErrorCategory.Network, "network.closed", "The server closed the connection.");
            }

            if (done == audioFault)
            {
                process.Kill();
                var detail = audioFault.Exception?.GetBaseException().Message;
                return new StreamError(ErrorCategory.Audio, "audio.device", "An audio device stopped working or was unplugged.", _redactor.Redact(detail));
            }

            // Periodic health evaluation.
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

            var framesFresh = progress is not null && _time.GetElapsedTime(lastFrameAdvance, now) <= _options.HealthWindow;
            var bytesFresh = !lease.IsMeasured
                || (net.Connected && net.BytesSent >= _options.MinBytesForLive && _time.GetElapsedTime(lastBytesAdvance, now) <= _options.HealthWindow);

            if (_state.Current == StreamState.Connecting)
            {
                if (framesFresh && bytesFresh)
                {
                    liveSince = now;
                    _state.TryTransition(StreamState.Live);
                }
                else if (_time.GetElapsedTime(attemptStart, now) > _options.ConnectTimeout)
                {
                    process.Kill();
                    return FirstSpecificError(recent)
                        ?? new StreamError(ErrorCategory.Network, "network.connect_timeout", "Could not start streaming to the server in time.");
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
                    process.Kill();
                    return FirstSpecificError(recent) ?? new StreamError(
                        ErrorCategory.Network,
                        "network.stalled",
                        byteStall ? "The connection stopped sending data." : "The stream stopped making progress (the capture source may be minimized or closed).");
                }
            }

            PublishTelemetry(session, progress, net, liveSince is { } s2 ? _time.GetElapsedTime(s2, now) : TimeSpan.Zero);
        }
    }

    /// <summary>Stands in for the network output when only recording: nothing to measure or lose.</summary>
    private sealed class RecordOnlyLease : IOutputLease
    {
        public string PublishUrl => string.Empty;

        public string TcUrl => string.Empty;

        public bool IsMeasured => false;

        public Task Completion { get; } = new TaskCompletionSource().Task;

        public NetworkSnapshot Snapshot() => new(0, 0, 0, null, null, null, false);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private void PublishTelemetry(StreamSession session, EncoderProgress? progress, NetworkSnapshot? net, TimeSpan? connectionUptime)
    {
        var telemetry = new StreamTelemetry(
            _state.Current,
            _time.GetElapsedTime(_sessionStart),
            connectionUptime ?? TimeSpan.Zero,
            session.Encoder.Fps,
            progress,
            net,
            _reconnectCount,
            _lastError)
        {
            CaptureLooksBlack = Interlocked.Read(ref _blackSince) is var since and not 0 && _time.GetElapsedTime(since) >= _options.BlackAfter,
        };
        TelemetryUpdated?.Invoke(this, telemetry);
    }

    public async Task<bool> MoveLayerAsync(string layerId, NormalizedRect placement, CancellationToken cancellationToken = default)
    {
        var composition = Volatile.Read(ref _composition);
        var index = composition is null ? -1 : composition.LayerIds.IndexOf(layerId);
        if (composition is null || index < 0)
        {
            return false;
        }

        // Remembered even while reconnecting, so the next attempt starts where the viewer last saw it.
        _livePlacement[layerId] = placement;
        if (_state.Current != StreamState.Live)
        {
            return false;
        }

        var px = LayerGeometry.ToPixels(placement, composition.Width, composition.Height);
        try
        {
            bool ok;
            if (composition.FixedLayers.Contains(index))
            {
                // Scene media keep their size (resizing ahead of their fixed crop would read outside the picture).
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var target = $"overlay@cam{index.ToString(inv)}";
                ok = await composition.Commands.SendAsync(target, "x", px.X.ToString(inv), cancellationToken).ConfigureAwait(false);
                ok &= await composition.Commands.SendAsync(target, "y", px.Y.ToString(inv), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                ok = await composition.Commands.MoveLayerAsync(index, px, cancellationToken).ConfigureAwait(false);
            }

            LogLayerMoved(layerId, px.X, px.Y, px.Width, px.Height, ok);
            return ok;
        }
        catch (ObjectDisposedException)
        {
            return false; // the attempt ended while the command was in flight
        }
    }

    /// <summary>
    /// Trims a camera or overlay in the running stream to the part <paramref name="crop"/> of it. Its place
    /// and size are set separately (<see cref="MoveLayerAsync"/>). False when it is not live.
    /// </summary>
    public async Task<bool> SetLayerCropAsync(string layerId, NormalizedRect crop, CancellationToken cancellationToken = default)
    {
        var composition = Volatile.Read(ref _composition);
        var index = composition is null ? -1 : composition.LayerIds.IndexOf(layerId);
        if (composition is null || index < 0 || composition.FixedLayers.Contains(index))
        {
            return false;
        }

        _liveCrop[layerId] = crop;
        if (_state.Current != StreamState.Live)
        {
            return false;
        }

        var target = "crop@cam" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var (w, h, x, y) = FfmpegArgumentBuilder.LayerCrop(crop);
        try
        {
            var ok = await composition.Commands.SendAsync(target, "w", w, cancellationToken).ConfigureAwait(false);
            ok &= await composition.Commands.SendAsync(target, "h", h, cancellationToken).ConfigureAwait(false);
            ok &= await composition.Commands.SendAsync(target, "x", x, cancellationToken).ConfigureAwait(false);
            ok &= await composition.Commands.SendAsync(target, "y", y, cancellationToken).ConfigureAwait(false);
            return ok;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public Task<bool> SetBaseAsync(NormalizedRect? placement, NormalizedRect crop, BaseFit fit, CancellationToken cancellationToken = default) =>
        Volatile.Read(ref _composition) is { CaptureKeys: [var first, ..] }
            ? SetCaptureAsync(first, placement, crop, fit, cancellationToken)
            : Task.FromResult(false);

    /// <summary>True when the running stream has the capture <paramref name="key"/>, so a scene can show it without a restart.</summary>
    public bool HasCapture(string key) => Volatile.Read(ref _composition)?.CaptureKeys.Contains(key) == true;

    /// <summary>
    /// Moves, refits, trims, shows or hides one capture in the running stream (null placement hides it).
    /// False when nothing is live or the stream does not have that capture.
    /// </summary>
    public async Task<bool> SetCaptureAsync(string key, NormalizedRect? placement, NormalizedRect crop, BaseFit fit, CancellationToken cancellationToken = default)
    {
        var composition = Volatile.Read(ref _composition);
        var index = composition is null ? -1 : composition.CaptureKeys.IndexOf(key);
        if (composition is null || index < 0)
        {
            return false;
        }

        // Remembered even while reconnecting, so the next attempt starts where the viewer last saw it.
        _liveBases.TryGetValue(key, out var previous);
        if (previous == new LiveBase(placement, crop, fit) && _state.Current == StreamState.Live)
        {
            return true; // nothing to send (a scene switch re-sends every capture)
        }

        _liveBases[key] = new LiveBase(placement, crop, fit);
        if (_state.Current != StreamState.Live)
        {
            return false;
        }

        // Hiding keeps the last shape and only moves the picture away.
        var shape = placement ?? previous?.Placement ?? new NormalizedRect(0, 0, 1, 1);
        var args = BaseLayerArgs.For(shape, crop, fit, composition.Width, composition.Height, hidden: placement is null);
        try
        {
            var ok = true;
            foreach (var (target, command, argument) in args.Commands(index))
            {
                ok &= await composition.Commands.SendAsync(target, command, argument, cancellationToken).ConfigureAwait(false);
            }

            LogBaseChanged(placement is not null, ok);
            return ok;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>True when the video file has a sound track. FFmpeg is asked once per file; a failed probe means no sound.</summary>
    private async Task<bool> HasAudioAsync(string path, CancellationToken ct)
    {
        if (_hasAudio.TryGetValue(path, out var known))
        {
            return known;
        }

        var found = await MediaProbe.HasAudioAsync(_ffmpeg.Path, path, ct).ConfigureAwait(false);
        _hasAudio[path] = found;
        return found;
    }

    private sealed record LiveBase(NormalizedRect? Placement, NormalizedRect Crop, BaseFit Fit);

    public async Task<bool> HideLayerAsync(string layerId, CancellationToken cancellationToken = default)
    {
        var composition = Volatile.Read(ref _composition);
        var index = composition is null ? -1 : composition.LayerIds.IndexOf(layerId);
        if (composition is null || index < 0)
        {
            return false;
        }

        _livePlacement[layerId] = null;
        if (_state.Current != StreamState.Live)
        {
            return false;
        }

        try
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return await composition.Commands.SendAsync($"overlay@cam{index.ToString(inv)}", "x", (composition.Width + 16).ToString(inv), cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Pixel rectangle for a layer in this attempt: where it was last moved or hidden while live (so a
    /// reconnect keeps what the viewer saw), otherwise its starting placement. Hidden layers keep their
    /// size and sit just off the right edge of the canvas, ready to be moved back in.
    /// </summary>
    private PixelRect PlaceLayer(StreamSession session, string layerId, NormalizedRect placement, bool startHidden)
    {
        var (w, h) = (session.Encoder.Width, session.Encoder.Height);
        if (_livePlacement.TryGetValue(layerId, out var live))
        {
            return live is { } moved
                ? LayerGeometry.ToPixels(moved, w, h)
                : LayerGeometry.ToPixels(placement, w, h) with { X = w + 16 };
        }

        var px = LayerGeometry.ToPixels(placement, w, h);
        return startHidden ? px with { X = w + 16 } : px;
    }

    public Task<bool> SetGainAsync(string channel, double gain, CancellationToken cancellationToken = default)
    {
        if (!FfmpegArgumentBuilder.IsValidChannelName(channel))
        {
            return Task.FromResult(false);
        }

        gain = Math.Clamp(gain, 0, 4);
        _liveGain[channel] = gain;
        return SendLiveAsync("volume@" + channel, "volume", gain.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
    }

    public Task<bool> SetNoiseSuppressionAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        _liveNoiseSuppression = enabled;
        return SendLiveAsync("afftdn@mic", "nr", FfmpegArgumentBuilder.NoiseReduction(enabled), cancellationToken);
    }

    private async Task<bool> SendLiveAsync(string target, string command, string argument, CancellationToken cancellationToken)
    {
        var composition = Volatile.Read(ref _composition);
        if (composition is null || _state.Current != StreamState.Live)
        {
            return false;
        }

        try
        {
            return await composition.Commands.SendAsync(target, command, argument, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// The FFmpeg view of one audio source: named so it can be changed live, with this session's live
    /// changes (mute, volume, noise removal) applied so they survive a reconnect.
    /// </summary>
    private PipeAudioJobInput LiveAudioInput(AudioSourceSpec spec, IAudioInput input)
    {
        var channel = spec.Channel ?? (spec is MicrophoneSource ? AudioChannels.Microphone : AudioChannels.System);
        var gain = _liveGain.TryGetValue(channel, out var live) ? live : spec.Gain;
        return new PipeAudioJobInput(input.PipePath, input.Format, gain)
        {
            Name = channel,
            NoiseSuppression = spec is MicrophoneSource ? _liveNoiseSuppression ?? spec.NoiseSuppression : null,
        };
    }

    /// <param name="FixedLayers">Indices of layers that keep their size (scene media); they are only shown and hidden.</param>
    /// <param name="HasCapture">Whether the graph has the captured picture (<c>overlay@base</c>).</param>
    private sealed record ActiveComposition(FfmpegCommandChannel Commands, List<string> LayerIds, HashSet<int> FixedLayers, List<string> CaptureKeys, int Width, int Height);

    [LoggerMessage(Level = LogLevel.Information, Message = "Layer {LayerId} moved live to {X},{Y} {W}x{H} (ok: {Ok})")]
    private partial void LogLayerMoved(string layerId, int x, int y, int w, int h, bool ok);

    [LoggerMessage(Level = LogLevel.Information, Message = "Captured picture changed live (shown: {Shown}, ok: {Ok})")]
    private partial void LogBaseChanged(bool shown, bool ok);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scene picture or video for {LayerId} is missing; that scene shows without it")]
    private partial void LogMediaMissing(string layerId);

    /// <summary>Completes (faulted) only when one of <paramref name="tasks"/> faults; normal completion is ignored.</summary>
    private static Task FirstFault(IEnumerable<Task> tasks)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var t in tasks)
        {
            _ = t.ContinueWith(
                x => tcs.TrySetException(x.Exception!.InnerExceptions),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        return tcs.Task;
    }

    private static StreamError? LeaseError(IOutputLease lease) =>
        lease.Completion is { IsFaulted: true } t && t.Exception?.GetBaseException() is StreamFailureException sfe ? sfe.Error : null;

    private static StreamError? FirstSpecificError(ConcurrentQueue<string> recent)
    {
        foreach (var line in recent)
        {
            if (ErrorClassifier.Classify(line) is { } e)
            {
                return e;
            }
        }

        return null;
    }

    private static string QuoteForLog(string a) => a.Contains(' ', StringComparison.Ordinal) || a.Contains(';', StringComparison.Ordinal) ? $"\"{a}\"" : a;

    [LoggerMessage(Level = LogLevel.Information, Message = "Stream state {Previous} -> {Current} {Error}")]
    private partial void LogStateChanged(StreamState previous, StreamState current, string? error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Launching ffmpeg: {Arguments}")]
    private partial void LogLaunching(string arguments);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ffmpeg: {Line}")]
    private partial void LogFfmpegLine(string line);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconnect attempt {Attempt}/{Max} in {DelaySeconds:0.#}s after {Error}")]
    private partial void LogReconnecting(int attempt, int max, double delaySeconds, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Captured window black: {Black}")]
    private partial void LogBlackProbe(bool black);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected engine failure")]
    private partial void LogUnexpected(Exception ex);
}

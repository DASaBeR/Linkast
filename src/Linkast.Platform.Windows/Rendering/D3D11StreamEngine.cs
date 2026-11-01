// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using Linkast.Core;
using Linkast.Core.Audio;
using Linkast.Core.Camera;
using Linkast.Core.Rendering;
using Linkast.Engine.FFmpeg;
using Linkast.Platform.Windows.Audio;
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
    private D3D11SceneCompositor? _compositor;
    private SourceFrameCache? _frameCache;
    private MediaOutputScheduler? _scheduler;
    private UnifiedAudioMixer? _audioMixer;

    private readonly ConcurrentDictionary<string, ID3D11SceneSource> _sources = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, NormalizedRect> _layerCrops = new(StringComparer.Ordinal);
    private readonly Lock _layoutLock = new();
    private SceneLayout _currentLayout = new();

    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private int _ffmpegProcessId;
    private long _sessionStart;
    private int _reconnectCount;
    private StreamError? _lastError;
    private EncoderProgress? _latestProgress;

    public D3D11StreamEngine(
        FfmpegInstallation ffmpeg,
        IOutputTransport transport,
        AudioCaptureHub? audioHub = null,
        ILogger? logger = null,
        D3D11DeviceManager? deviceManager = null,
        TimeProvider? timeProvider = null,
        EngineOptions? options = null)
    {
        _ffmpeg = ffmpeg ?? throw new ArgumentNullException(nameof(ffmpeg));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _audioHub = audioHub ?? new AudioCaptureHub();
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _options = options ?? new EngineOptions();

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

    public int FfmpegProcessId => Volatile.Read(ref _ffmpegProcessId);

    public FfmpegInstallation Installation => _ffmpeg;

    public Func<VideoSourceSpec, (int Width, int Height)?>? SourceSize { get; set; }

    public event EventHandler<StreamStateChangedEventArgs>? StateChanged;

    public event EventHandler<StreamTelemetry>? TelemetryUpdated;

    public event EventHandler<string>? DiagnosticLine;

    public event EventHandler<string>? RecordingSaved;

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

        if (_ownsDeviceManager)
        {
            _deviceManager.Dispose();
        }

        foreach (var src in _sources.Values)
        {
            await src.DisposeAsync().ConfigureAwait(false);
        }
        _sources.Clear();
    }

    private async Task RunSessionAsync(StreamSession session, CancellationToken ct)
    {
        var backoff = new ReconnectBackoff(session.Reconnect);
        var captureRestarts = new ReconnectBackoff(_options.CaptureRestart);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                _state.TryTransition(StreamState.Connecting);
                var error = await RunAttemptAsync(session, [backoff, captureRestarts], ct).ConfigureAwait(false);
                if (error is null || ct.IsCancellationRequested)
                {
                    break;
                }

                _lastError = error;
                var budget = error.Category == ErrorCategory.Capture ? captureRestarts : backoff;
                if (error.IsRetryable && budget.NextDelay() is { } nextDelay)
                {
                    _reconnectCount++;
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
    }

    private async Task<StreamError?> RunAttemptAsync(StreamSession session, ReconnectBackoff[] backoffs, CancellationToken ct)
    {
        IOutputLease? lease = null;
        FfmpegProcess? process = null;
        string? recordingPath = null;

        try
        {
            lease = session.Broadcast
                ? BufferedOutputLease.Start(_ffmpeg, await _transport.OpenAsync(session.Destination, session.Network, ct).ConfigureAwait(false), _logger ?? NullLogger.Instance, line =>
                {
                    var redacted = _redactor.Redact("sender: " + line);
                    DiagnosticLine?.Invoke(this, redacted);
                })
                : new RecordOnlyLease();

            if (session.Recording is { } recording)
            {
                try
                {
                    Directory.CreateDirectory(recording.Folder);
                    recordingPath = RecordingFiles.NewPath(recording.Folder, _time.GetLocalNow().DateTime);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                {
                    return new StreamError(ErrorCategory.Configuration, "recording.folder", "The recordings folder can't be created. Choose another folder.", _redactor.Redact(ex.Message));
                }
            }

            _compositor ??= new D3D11SceneCompositor(_deviceManager, session.Encoder.Width, session.Encoder.Height);
            _frameCache ??= new SourceFrameCache();

            SetupSources(session);

            lock (_layoutLock)
            {
                var cameraMap = new Dictionary<string, NormalizedRect>();
                foreach (var b in session.CameraLayers)
                {
                    if (!b.StartHidden)
                    {
                        cameraMap[b.Layer.LayerId] = b.Placement;
                    }
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

            await using var videoPipe = new NamedPipeServerStream(
                videoPipeName,
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                0,
                (int)_compositor.Nv12ByteSize * 4);

            await using var audioPipe = new NamedPipeServerStream(
                audioPipeName,
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                0,
                192000);

            var job = new SinglePipeFfmpegJob(
                VideoPipePath: videoPipePath,
                Width: session.Encoder.Width,
                Height: session.Encoder.Height,
                Fps: session.Encoder.Fps,
                AudioPipePath: audioPipePath,
                Encoder: session.Encoder,
                PublishUrl: session.Broadcast ? lease.PublishUrl : null,
                TcUrl: session.Broadcast && lease.TcUrl.Length > 0 ? lease.TcUrl : null,
                RecordingPath: recordingPath);

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

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(_options.ConnectTimeout);

            try
            {
                await Task.WhenAll(
                    videoPipe.WaitForConnectionAsync(connectCts.Token),
                    audioPipe.WaitForConnectionAsync(connectCts.Token)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                process.Kill();
                return new StreamError(ErrorCategory.Process, "pipe.connect_timeout", "FFmpeg failed to connect to internal media pipes.");
            }

            var streamStart = _time.GetTimestamp();
            await using var scheduler = new MediaOutputScheduler(
                _compositor,
                _frameCache,
                () => { lock (_layoutLock) return _currentLayout; },
                () => _sources,
                videoPipe,
                session.Encoder.Fps,
                _logger);

            await using var mixer = new UnifiedAudioMixer(
                _audioHub,
                audioPipe,
                streamStart,
                _logger);

            _scheduler = scheduler;
            _audioMixer = mixer;

            foreach (var a in session.Audio)
            {
                var ch = a.Channel ?? (a is MicrophoneSource ? AudioChannels.Microphone : AudioChannels.System);
                try
                {
                    mixer.AddSource(ch, a, a.Gain);
                }
                catch (Exception ex) when (a.Optional)
                {
                    DiagnosticLine?.Invoke(this, $"[warning] Optional audio source '{ch}' failed: {ex.Message}");
                }
            }

            scheduler.Start();
            mixer.Start();

            return await SuperviseAsync(session, backoffs, lease, process, recentLines, () => Volatile.Read(ref _latestProgress), recordingPath, ct).ConfigureAwait(false);
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
            _scheduler = null;
            _audioMixer = null;
            Volatile.Write(ref _ffmpegProcessId, 0);

            if (process is not null)
            {
                await process.DisposeAsync().ConfigureAwait(false);
            }

            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }

            if (recordingPath is not null && File.Exists(recordingPath))
            {
                RecordingSaved?.Invoke(this, recordingPath);
            }
        }
    }

    private async Task<StreamError?> SuperviseAsync(
        StreamSession session,
        ReconnectBackoff[] backoffs,
        IOutputLease lease,
        FfmpegProcess process,
        ConcurrentQueue<string> recent,
        Func<EncoderProgress?> latestProgress,
        string? recordingPath,
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
            var done = await Task.WhenAny(process.Exited, lease.Completion, tick).ConfigureAwait(false);

            if (ct.IsCancellationRequested)
            {
                await process.StopAsync(_options.GracefulStopTimeout).ConfigureAwait(false);
                return null;
            }

            if (done == process.Exited)
            {
                await Task.WhenAny(lease.Completion, Task.Delay(500, CancellationToken.None)).ConfigureAwait(false);
                return LeaseError(lease) ?? ErrorClassifier.ClassifyExit(process.Exited.Result, [.. recent]);
            }

            if (done == lease.Completion)
            {
                process.Kill();
                return LeaseError(lease) ?? new StreamError(ErrorCategory.Network, "network.closed", "The server closed the connection.");
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
                var isLive = session.Broadcast
                    ? net.BytesSent >= _options.MinBytesForLive && progress is { Frame: > 0 }
                    : (progress?.Frame ?? 0) > session.Encoder.Fps;

                if (isLive)
                {
                    liveSince = now;
                    _state.TryTransition(StreamState.Live);
                }
                else if (_time.GetElapsedTime(attemptStart, now) > _options.ConnectTimeout)
                {
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
                    process.Kill();
                    return new StreamError(
                        ErrorCategory.Network,
                        "network.stalled",
                        byteStall ? "The connection stopped sending data." : "The stream stopped making progress.");
                }
            }

            PublishTelemetry(session, progress, net, liveSince is { } s2 ? _time.GetElapsedTime(s2, now) : TimeSpan.Zero);
        }
    }

    private void SetupSources(StreamSession session)
    {
        var bindings = session.Captures.Count > 0 ? session.Captures
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
                    }
                }
            }

            foreach (var key in _sources.Keys.ToArray())
            {
                if (!activeKeys.Contains(key) && key != "capture")
                {
                    if (_sources.TryRemove(key, out var oldSource))
                    {
                        _ = oldSource.DisposeAsync().AsTask();
                    }
                    _frameCache?.Remove(key);
                }
            }

            if (_audioMixer is not null)
            {
                foreach (var a in audio)
                {
                    var ch = a.Channel ?? (a is MicrophoneSource ? AudioChannels.Microphone : AudioChannels.System);
                    try
                    {
                        _audioMixer.AddSource(ch, a, a.Gain);
                    }
                    catch
                    {
                        // Source already registered or optional
                    }
                }
            }
        }

        return true;
    }

    public bool HasCapture(string key) => _sources.ContainsKey(key);

    public Task<bool> SetCaptureAsync(string key, NormalizedRect? placement, NormalizedRect crop, BaseFit fit, CancellationToken cancellationToken = default)
    {
        lock (_layoutLock)
        {
            _currentLayout = _currentLayout with
            {
                BasePlacement = placement ?? new NormalizedRect(-2, -2, 0, 0),
                ShowCapture = placement is not null,
                BaseCrop = crop,
                BaseFit = fit,
            };
        }
        return Task.FromResult(true);
    }

    public Task<bool> SetBaseAsync(NormalizedRect? placement, NormalizedRect crop, BaseFit fit, CancellationToken cancellationToken = default) =>
        SetCaptureAsync("capture", placement, crop, fit, cancellationToken);

    public Task<bool> MoveLayerAsync(string layerId, NormalizedRect placement, CancellationToken cancellationToken = default)
    {
        lock (_layoutLock)
        {
            _currentLayout = _currentLayout.WithPlacement(layerId, placement);
        }
        return Task.FromResult(true);
    }

    public Task<bool> HideLayerAsync(string layerId, CancellationToken cancellationToken = default)
    {
        lock (_layoutLock)
        {
            _currentLayout = _currentLayout.Without(layerId);
        }
        return Task.FromResult(true);
    }

    public Task<bool> SetLayerCropAsync(string layerId, NormalizedRect crop, CancellationToken cancellationToken = default)
    {
        _layerCrops[layerId] = crop;
        return Task.FromResult(true);
    }

    public Task<bool> SetGainAsync(string channel, double gain, CancellationToken cancellationToken = default)
    {
        if (_audioMixer is not null)
        {
            return _audioMixer.SetGainAsync(channel, gain, cancellationToken);
        }
        return Task.FromResult(false);
    }

    public Task<bool> SetNoiseSuppressionAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (_audioMixer is not null)
        {
            return _audioMixer.SetNoiseSuppressionAsync(enabled, cancellationToken);
        }
        return Task.FromResult(false);
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

    [LoggerMessage(LogLevel.Error, "Unexpected error occurred during D3D11 streaming session.")]
    private static partial void LogUnexpected(ILogger logger, Exception ex);

    [LoggerMessage(LogLevel.Warning, "Failed to initialize capture source '{Key}'.")]
    private static partial void LogSourceInitFailed(ILogger logger, string key, Exception ex);
}

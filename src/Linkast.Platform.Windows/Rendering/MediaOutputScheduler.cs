// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using Linkast.Core.Camera;
using Linkast.Core.Rendering;
using Microsoft.Extensions.Logging;
using Vortice.Mathematics;

namespace Linkast.Platform.Windows.Rendering;

public sealed class OutputDetachedEventArgs(string name, string reason) : EventArgs
{
    public string Name { get; } = name;

    public string Reason { get; } = reason;
}

internal sealed record SchedulerCanvas(
    string Name,
    D3D11SceneCompositor Compositor,
    Func<IReadOnlyDictionary<string, NormalizedRect>> Placements,
    Func<IReadOnlyDictionary<string, NormalizedRect>> Crops,
    Func<IReadOnlyList<string>> Stack,
    IReadOnlyList<D3D11OutputTarget> Targets);

/// <summary>
/// High-precision monotonic frame scheduler for scene composition and video output delivery.
/// Runs on a dedicated render thread, decoupling GPU composition from source capture rates.
/// </summary>
public sealed partial class MediaOutputScheduler : IAsyncDisposable
{
    private sealed class CanvasState
    {
        public CanvasState(
            string name,
            D3D11SceneCompositor compositor,
            Func<SceneLayout>? layoutProvider,
            Func<IReadOnlyDictionary<string, NormalizedRect>>? placementsProvider,
            Func<IReadOnlyDictionary<string, NormalizedRect>>? cropsProvider,
            Func<IReadOnlyList<string>>? stackOrderProvider,
            IReadOnlyList<D3D11OutputTarget> targets,
            ILogger? logger)
        {
            Name = name;
            Compositor = compositor;
            LayoutProvider = layoutProvider;
            PlacementsProvider = placementsProvider;
            CropsProvider = cropsProvider;
            StackOrderProvider = stackOrderProvider;
            Targets = [.. targets];
            _logger = logger;
        }

        private readonly ILogger? _logger;
        private IReadOnlyList<string>? _lastStackOrder;
        private int _lastStackCount = -1;
        private int _lastSourcesCount = -1;
        private IReadOnlyList<string>? _cachedOrderedKeys;
        private readonly HashSet<string> _cachedSourceKeys = new(StringComparer.Ordinal);

        public string Name { get; }
        public D3D11SceneCompositor Compositor { get; }
        public Func<SceneLayout>? LayoutProvider { get; }
        public Func<IReadOnlyDictionary<string, NormalizedRect>>? PlacementsProvider { get; }
        public Func<IReadOnlyDictionary<string, NormalizedRect>>? CropsProvider { get; }
        public Func<IReadOnlyList<string>>? StackOrderProvider { get; }
        public List<D3D11OutputTarget> Targets { get; }

        public bool HasAttachedTargets()
        {
            foreach (var t in Targets)
            {
                if (t.IsAttached)
                {
                    return true;
                }
            }

            return false;
        }

        public IReadOnlyList<string> GetOrderedKeys(IReadOnlyDictionary<string, ID3D11SceneSource> sources, IReadOnlyList<string>? stackOrder)
        {
            bool needsUpdate = _cachedOrderedKeys is null
                || sources.Count != _lastSourcesCount
                || (stackOrder?.Count ?? 0) != _lastStackCount
                || !ReferenceEquals(stackOrder, _lastStackOrder);

            if (!needsUpdate)
            {
                foreach (var k in sources.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (!_cachedSourceKeys.Contains(k))
                    {
                        needsUpdate = true;
                        break;
                    }
                }
            }

            if (needsUpdate)
            {
                _cachedSourceKeys.Clear();
                var presentList = new List<string>(sources.Count);
                foreach (var k in sources.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    _cachedSourceKeys.Add(k);
                    presentList.Add(k);
                }

                _lastSourcesCount = sources.Count;
                _lastStackOrder = stackOrder;
                _lastStackCount = stackOrder?.Count ?? 0;

                _cachedOrderedKeys = stackOrder is not null && stackOrder.Count > 0
                    ? SceneStacking.Resolve(stackOrder, presentList)
                    : presentList;

                if (_logger is not null)
                {
                    LogOrderedKeysRebuilt(_logger, string.Join(", ", _cachedOrderedKeys));
                }
            }

            return _cachedOrderedKeys!;
        }
    }

    private readonly D3D11SceneCompositor _compositor;
    private readonly SourceFrameCache _frameCache;
    private readonly Func<IReadOnlyDictionary<string, ID3D11SceneSource>> _sourcesProvider;
    private readonly List<CanvasState> _canvases = [];
    private readonly List<D3D11OutputTarget> _ownedTargets = [];
    private readonly int _targetFps;
    private readonly ILogger? _logger;

    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _renderLoopFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _renderThread;
    private volatile bool _isRunning;

    private long _framesRendered;
    private long _framesDropped;
    private long _framesWritten;
    private long _lostTicks;
    private long _slowFrames;
    private long _longestFrameTicks;
    private int _disposed;

    /// <summary>Raised when a secondary output target's pipe fails and is detached without stopping the stream.</summary>
    public event EventHandler<OutputDetachedEventArgs>? OutputDetached;

    public MediaOutputScheduler(
        D3D11SceneCompositor compositor,
        SourceFrameCache frameCache,
        Func<SceneLayout> layoutProvider,
        Func<IReadOnlyDictionary<string, ID3D11SceneSource>> sourcesProvider,
        IReadOnlyList<D3D11OutputTarget> targets,
        int targetFps = 60,
        ILogger? logger = null,
        Func<IReadOnlyDictionary<string, NormalizedRect>>? cropsProvider = null,
        Func<IReadOnlyDictionary<string, NormalizedRect>>? placementsProvider = null,
        Func<IReadOnlyList<string>>? stackOrderProvider = null)
    {
        _compositor = compositor ?? throw new ArgumentNullException(nameof(compositor));
        _frameCache = frameCache ?? throw new ArgumentNullException(nameof(frameCache));
        ArgumentNullException.ThrowIfNull(layoutProvider);
        _sourcesProvider = sourcesProvider ?? throw new ArgumentNullException(nameof(sourcesProvider));
        ArgumentNullException.ThrowIfNull(targets);
        _targetFps = targetFps > 0 ? targetFps : 60;
        _logger = logger;

        _canvases.Add(new CanvasState(
            "main",
            compositor,
            layoutProvider,
            placementsProvider,
            cropsProvider,
            stackOrderProvider,
            targets,
            logger));
    }

    public MediaOutputScheduler(
        D3D11SceneCompositor compositor,
        SourceFrameCache frameCache,
        Func<SceneLayout> layoutProvider,
        Func<IReadOnlyDictionary<string, ID3D11SceneSource>> sourcesProvider,
        Stream videoPipeStream,
        int targetFps = 60,
        ILogger? logger = null,
        Func<IReadOnlyDictionary<string, NormalizedRect>>? cropsProvider = null,
        Func<IReadOnlyDictionary<string, NormalizedRect>>? placementsProvider = null,
        Func<IReadOnlyList<string>>? stackOrderProvider = null)
        : this(
            compositor,
            frameCache,
            layoutProvider,
            sourcesProvider,
            [CreateDefaultTarget(compositor, videoPipeStream, logger)],
            targetFps,
            logger,
            cropsProvider,
            placementsProvider,
            stackOrderProvider)
    {
        _ownedTargets.Add(_canvases[0].Targets[0]);
    }

    private static D3D11OutputTarget CreateDefaultTarget(D3D11SceneCompositor compositor, Stream videoPipeStream, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(videoPipeStream);
        return new D3D11OutputTarget(
            compositor.DeviceManager,
            "stream",
            compositor.CanvasWidth,
            compositor.CanvasHeight,
            videoPipeStream,
            isPrimary: true,
            logger);
    }

    /// <summary>Adds an output target to the main canvas before <see cref="Start"/> is called.</summary>
    public void AddTarget(D3D11OutputTarget target) => AddTarget("main", target);

    /// <summary>Adds an output target to the named canvas before <see cref="Start"/> is called.</summary>
    internal void AddTarget(string canvasName, D3D11OutputTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var canvas = _canvases.FirstOrDefault(c => string.Equals(c.Name, canvasName, StringComparison.Ordinal))
            ?? throw new ArgumentException($"Unknown canvas '{canvasName}'.", nameof(canvasName));
        canvas.Targets.Add(target);
    }

    /// <summary>Adds an extra canvas to render from shared sources before <see cref="Start"/> is called.</summary>
    internal void AddCanvas(SchedulerCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        _canvases.Add(new CanvasState(
            canvas.Name,
            canvas.Compositor,
            null,
            canvas.Placements,
            canvas.Crops,
            canvas.Stack,
            canvas.Targets,
            _logger));
    }

    public long FramesRendered => Interlocked.Read(ref _framesRendered);

    /// <summary>Times the render loop fell more than two frames behind and skipped ahead.</summary>
    public long FramesDropped => Interlocked.Read(ref _framesDropped);

    /// <summary>Frames that reached FFmpeg.</summary>
    public long FramesWritten => Interlocked.Read(ref _framesWritten);

    /// <summary>
    /// The time the picture lost against the wall clock: frame slots that were skipped or not delivered.
    /// FFmpeg times frames by count, so this is how far the picture's timeline trails real time; the sound
    /// drops the same time to stay with it.
    /// </summary>
    public TimeSpan LostTime => Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _lostTicks));

    /// <summary>Frames that took longer than 100 ms to render and hand over (FFmpeg not reading, or a busy GPU).</summary>
    public long SlowFrames => Interlocked.Read(ref _slowFrames);

    /// <summary>The longest a single frame took to render and hand over.</summary>
    public TimeSpan LongestFrame => Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _longestFrameTicks));

    private void NoteFrameTime(long ticks)
    {
        if (ticks > Stopwatch.Frequency / 10)
        {
            Interlocked.Increment(ref _slowFrames);
        }

        long seen;
        while (ticks > (seen = Interlocked.Read(ref _longestFrameTicks)) && Interlocked.CompareExchange(ref _longestFrameTicks, ticks, seen) != seen)
        {
        }
    }

    public bool IsRunning => _isRunning;

    /// <summary>
    /// Starts the high-precision monotonic rendering thread.
    /// </summary>
    public void Start()
    {
        if (_isRunning)
        {
            return;
        }

        _isRunning = true;
        _renderThread = new Thread(RenderLoop)
        {
            Name = "Linkast-D3D11Compositor",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _renderThread.Start();
    }

    private void RenderLoop()
    {
        long ticksPerSecond = Stopwatch.Frequency;
        long targetIntervalTicks = ticksPerSecond / _targetFps;

        long nextFrameTicks = Stopwatch.GetTimestamp();

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                long currentTicks = Stopwatch.GetTimestamp();

                // If running behind by more than 2 frames, catch up to prevent runaway pipeline backlog.
                // The slots skipped are time the picture's timeline loses; the sound loses it too.
                if (currentTicks > nextFrameTicks + 2 * targetIntervalTicks)
                {
                    Interlocked.Increment(ref _framesDropped);
                    Interlocked.Add(ref _lostTicks, currentTicks - nextFrameTicks);
                    nextFrameTicks = currentTicks;
                }

                // Render current frame
                var began = Stopwatch.GetTimestamp();
                if (!RenderSingleFrame())
                {
                    Interlocked.Add(ref _lostTicks, targetIntervalTicks); // the slot stays empty
                }

                NoteFrameTime(Stopwatch.GetTimestamp() - began);
                Interlocked.Increment(ref _framesRendered);

                nextFrameTicks += targetIntervalTicks;
                long sleepTicks = nextFrameTicks - Stopwatch.GetTimestamp();

                if (sleepTicks > 0)
                {
                    int sleepMs = (int)(sleepTicks * 1000 / ticksPerSecond);
                    if (sleepMs > 1)
                    {
                        Thread.Sleep(sleepMs - 1);
                    }

                    // Spin-wait the remaining fraction of a millisecond for sub-millisecond precision
                    while (Stopwatch.GetTimestamp() < nextFrameTicks)
                    {
                        Thread.SpinWait(10);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ex)
            {
                // The video pipe is gone: FFmpeg ended (a stop, or a reconnect that starts a new pipeline).
                // A broken pipe never comes back, so this is the end of the loop, not an error.
                if (_logger is not null)
                {
                    LogOutputClosed(_logger, ex.Message);
                }

                break;
            }
            catch (Exception ex)
            {
                // A single frame failing (e.g. a transient device-removed during loss recovery) must not
                // kill the render thread and tear down the stream. Log, pause briefly to avoid a hot loop,
                // keep the video pipe alive with a black fallback frame so FFmpeg doesn't see EOF,
                // and resync the clock so the next frame starts fresh.
                if (_logger is not null)
                {
                    LogRenderFrameFailed(_logger, ex);
                }

                if (!PumpBlackFrame())
                {
                    Interlocked.Add(ref _lostTicks, targetIntervalTicks);
                }

                if (_compositor.DeviceManager is { } dm && (!dm.CheckDeviceStatus() || dm.IsDeviceLost))
                {
                    if (_logger is not null)
                    {
                        LogAttemptingDeviceRestore(_logger);
                    }

                    bool restored = dm.RecreateDevice();
                    if (_logger is not null)
                    {
                        if (restored)
                        {
                            LogDeviceRestoredSuccess(_logger);
                        }
                        else
                        {
                            LogDeviceRestoreFailed(_logger);
                        }
                    }
                }

                if (_cts.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(100)))
                {
                    break;
                }

                // One slot was filled (black); the wait loses the rest up to now.
                var resumed = Stopwatch.GetTimestamp();
                nextFrameTicks += targetIntervalTicks;
                if (resumed > nextFrameTicks)
                {
                    Interlocked.Add(ref _lostTicks, resumed - nextFrameTicks);
                }

                nextFrameTicks = resumed;
            }
        }

        _isRunning = false;
        _renderLoopFinished.TrySetResult();
    }

    /// <summary>
    /// Acquires each active source once per tick, composes every canvas with attached targets, and writes
    /// each canvas's frames to its attached outputs.
    /// False when no frame reached the primary target (the device is lost or the frame could not begin).
    /// </summary>
    public bool RenderSingleFrame()
    {
        var sources = _sourcesProvider();
        var snapshots = new List<(CanvasState Canvas, SceneLayout? Layout, IReadOnlyDictionary<string, NormalizedRect>? Placements, IReadOnlyDictionary<string, NormalizedRect>? Crops, IReadOnlyList<string>? StackOrder)>(_canvases.Count);
        var keysNeeded = new HashSet<string>(StringComparer.Ordinal);

        foreach (var canvas in _canvases)
        {
            if (!canvas.HasAttachedTargets())
            {
                continue;
            }

            var layout = canvas.LayoutProvider?.Invoke();
            var placements = canvas.PlacementsProvider?.Invoke();
            var crops = canvas.CropsProvider?.Invoke();
            var stackOrder = canvas.StackOrderProvider?.Invoke();
            snapshots.Add((canvas, layout, placements, crops, stackOrder));
            CollectNeededKeys(sources, layout, placements, keysNeeded);
        }

        // 1. Acquire phase: each shared source is acquired once per tick across all canvases.
        AcquireNeededSources(sources, keysNeeded);

        // 2. Draw + Output phase per canvas.
        bool primaryWritten = false;
        foreach (var (canvas, layout, placements, crops, stackOrder) in snapshots)
        {
            lock (canvas.Compositor.RenderGate)
            {
                if (!DrawCanvasFrame(canvas, sources, layout, placements, crops, stackOrder))
                {
                    continue;
                }

                foreach (var target in canvas.Targets)
                {
                    if (!target.IsAttached)
                    {
                        continue;
                    }

                    try
                    {
                        if (target.WriteFrame(canvas.Compositor) && target.IsPrimary)
                        {
                            primaryWritten = true;
                            Interlocked.Increment(ref _framesWritten);
                        }
                    }
                    catch (Exception ex) when (!target.IsPrimary && ex is IOException or ObjectDisposedException)
                    {
                        DetachTarget(target, ex.Message);
                    }
                }
            }
        }

        return primaryWritten;
    }

    /// <summary>
    /// Renders a single composited frame of the canvas owning <paramref name="only"/> and writes it only to
    /// <paramref name="only"/>. Used for the FFmpeg pipe handshake of a secondary output before the loop starts.
    /// </summary>
    public void RenderSingleFrame(D3D11OutputTarget only)
    {
        ArgumentNullException.ThrowIfNull(only);
        var canvas = _canvases.FirstOrDefault(c => c.Targets.Contains(only)) ?? _canvases[0];
        var sources = _sourcesProvider();
        var layout = canvas.LayoutProvider?.Invoke();
        var placements = canvas.PlacementsProvider?.Invoke();
        var crops = canvas.CropsProvider?.Invoke();
        var stackOrder = canvas.StackOrderProvider?.Invoke();

        var keysNeeded = new HashSet<string>(StringComparer.Ordinal);
        CollectNeededKeys(sources, layout, placements, keysNeeded);
        AcquireNeededSources(sources, keysNeeded);

        lock (canvas.Compositor.RenderGate)
        {
            if (!DrawCanvasFrame(canvas, sources, layout, placements, crops, stackOrder))
            {
                return;
            }

            only.WriteFrame(canvas.Compositor);
        }
    }

    private void AcquireNeededSources(IReadOnlyDictionary<string, ID3D11SceneSource> sources, HashSet<string> keysNeeded)
    {
        foreach (var key in keysNeeded)
        {
            if (!sources.TryGetValue(key, out var source))
            {
                continue;
            }

            if (IsCapture(source) && source.State != SceneSourceState.Capturing)
            {
                _frameCache.Remove(key);
                continue;
            }

            if (source.State == SceneSourceState.Lost)
            {
                continue;
            }

            var task = source.AcquireFrameAsync();
            var acquired = task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
            if (acquired is not null)
            {
                _frameCache.Store(key, acquired, Stopwatch.GetTimestamp());
            }
        }
    }

    private static void CollectNeededKeys(
        IReadOnlyDictionary<string, ID3D11SceneSource> sources,
        SceneLayout? layout,
        IReadOnlyDictionary<string, NormalizedRect>? placements,
        HashSet<string> keysNeeded)
    {
        if (placements is not null)
        {
            foreach (var key in placements.Keys)
            {
                if (sources.ContainsKey(key))
                {
                    keysNeeded.Add(key);
                }
            }

            return;
        }

        if (layout is null)
        {
            return;
        }

        if (layout.ShowCapture)
        {
            if (sources.ContainsKey("capture"))
            {
                keysNeeded.Add("capture");
            }
            else
            {
                foreach (var (key, src) in sources)
                {
                    if (!layout.CameraPlacements.ContainsKey(key) && src.State != SceneSourceState.Lost)
                    {
                        keysNeeded.Add(key);
                        break;
                    }
                }
            }
        }

        foreach (var cameraId in layout.CameraPlacements.Keys)
        {
            if (sources.ContainsKey(cameraId))
            {
                keysNeeded.Add(cameraId);
            }
        }
    }

    private bool DrawCanvasFrame(
        CanvasState canvas,
        IReadOnlyDictionary<string, ID3D11SceneSource> sources,
        SceneLayout? layout,
        IReadOnlyDictionary<string, NormalizedRect>? placements,
        IReadOnlyDictionary<string, NormalizedRect>? crops,
        IReadOnlyList<string>? stackOrder)
    {
        var compositor = canvas.Compositor;
        if (compositor.DeviceManager.IsDeviceLost || !compositor.BeginFrame(new Color4(0f, 0f, 0f, 1f)))
        {
            return false;
        }

        if (placements is not null)
        {
            var orderedKeys = canvas.GetOrderedKeys(sources, stackOrder);
            foreach (var key in orderedKeys)
            {
                if (!sources.TryGetValue(key, out var source) || (source.State == SceneSourceState.Lost && !IsCapture(source)))
                {
                    continue;
                }

                if (!placements.TryGetValue(key, out var placement))
                {
                    continue;
                }

                var crop = crops is not null && crops.TryGetValue(key, out var c) ? c : NormalizedRect.Full;
                DrawCachedSource(compositor, key, source, placement, crop);
            }
        }
        else if (layout is not null)
        {
            if (layout.ShowCapture)
            {
                if (sources.TryGetValue("capture", out var defaultCapture))
                {
                    DrawCachedSource(compositor, "capture", defaultCapture, layout.BasePlacement, layout.BaseCrop);
                }
                else
                {
                    foreach (var (key, src) in sources)
                    {
                        if (!layout.CameraPlacements.ContainsKey(key) && src.State != SceneSourceState.Lost)
                        {
                            DrawCachedSource(compositor, key, src, layout.BasePlacement, layout.BaseCrop);
                            break;
                        }
                    }
                }
            }

            foreach (var (cameraId, placement) in layout.CameraPlacements)
            {
                if (sources.TryGetValue(cameraId, out var camSource))
                {
                    var crop = crops is not null && crops.TryGetValue(cameraId, out var c) ? c : NormalizedRect.Full;
                    DrawCachedSource(compositor, cameraId, camSource, placement, crop);
                }
            }
        }

        return true;
    }

    private void DrawCachedSource(
        D3D11SceneCompositor compositor,
        string key,
        ID3D11SceneSource source,
        NormalizedRect placement,
        NormalizedRect crop)
    {
        // A capture that cut out (its window closed, minimized, or restarting after Windows ended it) shows
        // black in its place: not its last picture frozen, nor the layers under it flashing through.
        if (IsCapture(source) && source.State != SceneSourceState.Capturing)
        {
            compositor.DrawBlack(placement);
            return;
        }

        var (tex, srv, _, _) = _frameCache.AcquireWithSrv(key);
        if (source.State != SceneSourceState.Lost)
        {
            if (srv is not null)
            {
                compositor.DrawLayer(srv, placement, crop);
            }
            else if (tex is not null)
            {
                compositor.DrawLayer(tex, placement, crop);
            }
            else if (IsCapture(source))
            {
                compositor.DrawBlack(placement); // started, no picture yet
            }
        }
    }

    private void DetachTarget(D3D11OutputTarget target, string reason)
    {
        if (target.TryDetach())
        {
            OutputDetached?.Invoke(this, new OutputDetachedEventArgs(target.Name, reason));
        }
    }

    internal IReadOnlyList<string> GetOrderedKeys(IReadOnlyDictionary<string, ID3D11SceneSource> sources, IReadOnlyList<string>? stackOrder) =>
        _canvases[0].GetOrderedKeys(sources, stackOrder);

    /// <summary>A capture of a game, window or screen (not a camera, picture or overlay).</summary>
    private static bool IsCapture(ID3D11SceneSource source) => source is WindowsGraphicsCaptureSource;

    private bool PumpBlackFrame()
    {
        bool primaryWritten = false;
        foreach (var canvas in _canvases)
        {
            foreach (var target in canvas.Targets)
            {
                if (!target.IsAttached)
                {
                    continue;
                }

                try
                {
                    target.WriteBlackFrame();
                    if (target.IsPrimary)
                    {
                        primaryWritten = true;
                        Interlocked.Increment(ref _framesWritten);
                    }
                }
                catch (Exception ex) when (!target.IsPrimary && ex is IOException or ObjectDisposedException)
                {
                    DetachTarget(target, ex.Message);
                }
                catch
                {
                    // If the primary pipe is broken or disconnected, the outer cancellation/stop flow handles it
                }
            }
        }

        return primaryWritten;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }

        if (_isRunning)
        {
            await _renderLoopFinished.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }

        foreach (var target in _ownedTargets)
        {
            target.Dispose();
        }

        _cts.Dispose();
    }

    [LoggerMessage(LogLevel.Debug, "Ordered scene keys rebuilt: {Keys}")]
    private static partial void LogOrderedKeysRebuilt(ILogger logger, string keys);

    [LoggerMessage(LogLevel.Debug, "Video output closed; the render loop ends ({Reason}).")]
    private static partial void LogOutputClosed(ILogger logger, string reason);

    [LoggerMessage(LogLevel.Error, "Render loop frame error occurred.")]
    private static partial void LogRenderFrameFailed(ILogger logger, Exception ex);

    [LoggerMessage(LogLevel.Information, "Direct3D 11 device lost or removed. Attempting device recreation.")]
    private static partial void LogAttemptingDeviceRestore(ILogger logger);

    [LoggerMessage(LogLevel.Information, "Direct3D 11 device successfully restored.")]
    private static partial void LogDeviceRestoredSuccess(ILogger logger);

    [LoggerMessage(LogLevel.Error, "Failed to restore Direct3D 11 device following device loss.")]
    private static partial void LogDeviceRestoreFailed(ILogger logger);
}

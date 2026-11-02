// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Linkast.Core.Camera;
using Linkast.Core.Rendering;
using Microsoft.Extensions.Logging;
using Vortice.Mathematics;

namespace Linkast.Platform.Windows.Rendering;

/// <summary>
/// High-precision monotonic frame scheduler for scene composition and video output delivery.
/// Runs on a dedicated render thread, decoupling GPU composition from source capture rates.
/// </summary>
public sealed partial class MediaOutputScheduler : IAsyncDisposable
{
    private readonly D3D11SceneCompositor _compositor;
    private readonly SourceFrameCache _frameCache;
    private readonly Func<SceneLayout> _layoutProvider;
    private readonly Func<IReadOnlyDictionary<string, ID3D11SceneSource>> _sourcesProvider;
    private readonly Func<IReadOnlyDictionary<string, NormalizedRect>>? _cropsProvider;
    private readonly Func<IReadOnlyDictionary<string, NormalizedRect>>? _placementsProvider;
    private readonly Func<IReadOnlyList<string>>? _stackOrderProvider;
    private readonly Stream _videoPipeStream;
    private readonly int _targetFps;
    private readonly ILogger? _logger;

    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _renderLoopFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _renderThread;
    private volatile bool _isRunning;

    private long _framesRendered;
    private long _framesDropped;
    private byte[]? _blackFrame;

    private IReadOnlyList<string>? _lastStackOrder;
    private int _lastStackCount = -1;
    private int _lastSourcesCount = -1;
    private IReadOnlyList<string>? _cachedOrderedKeys;
    private readonly HashSet<string> _cachedSourceKeys = new(StringComparer.Ordinal);

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
    {
        _compositor = compositor ?? throw new ArgumentNullException(nameof(compositor));
        _frameCache = frameCache ?? throw new ArgumentNullException(nameof(frameCache));
        _layoutProvider = layoutProvider ?? throw new ArgumentNullException(nameof(layoutProvider));
        _sourcesProvider = sourcesProvider ?? throw new ArgumentNullException(nameof(sourcesProvider));
        _videoPipeStream = videoPipeStream ?? throw new ArgumentNullException(nameof(videoPipeStream));
        _targetFps = targetFps > 0 ? targetFps : 60;
        _logger = logger;
        _cropsProvider = cropsProvider;
        _placementsProvider = placementsProvider;
        _stackOrderProvider = stackOrderProvider;
    }

    public long FramesRendered => Interlocked.Read(ref _framesRendered);

    public long FramesDropped => Interlocked.Read(ref _framesDropped);

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

                // If running behind by more than 2 frames, catch up to prevent runaway pipeline backlog
                if (currentTicks > nextFrameTicks + 2 * targetIntervalTicks)
                {
                    Interlocked.Increment(ref _framesDropped);
                    nextFrameTicks = currentTicks;
                }

                // Render current frame
                RenderSingleFrame();
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

                PumpBlackFrame();

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

                nextFrameTicks = Stopwatch.GetTimestamp();
            }
        }

        _isRunning = false;
        _renderLoopFinished.TrySetResult();
    }

    /// <summary>
    /// Renders a single composited frame according to the current layout and outputs it to the video stream.
    /// Supports full scene Z-stacking (captures, cameras, overlays, media backgrounds) and live placement updates.
    /// </summary>
    public void RenderSingleFrame()
    {
        lock (_compositor.RenderGate)
        {
            if (_compositor.DeviceManager.IsDeviceLost)
            {
                return;
            }

            var layout = _layoutProvider();
            var sources = _sourcesProvider();
            var placements = _placementsProvider?.Invoke();
            var crops = _cropsProvider?.Invoke();
            var stackOrder = _stackOrderProvider?.Invoke();

            // 1. Begin frame with clear color (navy or black)
            if (!_compositor.BeginFrame(new Color4(0.05f, 0.05f, 0.08f, 1.0f)))
            {
                return;
            }

            if (placements is not null)
            {
                var orderedKeys = GetOrderedKeys(sources, stackOrder);

                foreach (var key in orderedKeys)
                {
                    if (!sources.TryGetValue(key, out var source) || source.State == SceneSourceState.Lost)
                    {
                        continue;
                    }

                    if (!placements.TryGetValue(key, out var placement))
                    {
                        continue; // Layer is hidden
                    }

                    var crop = crops is not null && crops.TryGetValue(key, out var c) ? c : NormalizedRect.Full;
                    RenderSource(key, source, placement, crop);
                }
            }
            else
            {
                // Legacy / simple layout fallback when placements dictionary is not provided
                if (layout.ShowCapture)
                {
                    if (sources.TryGetValue("capture", out var defaultCapture))
                    {
                        RenderSource("capture", defaultCapture, layout.BasePlacement, layout.BaseCrop);
                    }
                    else
                    {
                        foreach (var (key, src) in sources)
                        {
                            if (!layout.CameraPlacements.ContainsKey(key) && src.State != SceneSourceState.Lost)
                            {
                                RenderSource(key, src, layout.BasePlacement, layout.BaseCrop);
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
                        RenderSource(cameraId, camSource, placement, crop);
                    }
                }
            }

            // 4. Convert composited frame to NV12 and write to video pipe stream
            _compositor.ConvertToNv12(_videoPipeStream);
        }
    }

    private IReadOnlyList<string> GetOrderedKeys(IReadOnlyDictionary<string, ID3D11SceneSource> sources, IReadOnlyList<string>? stackOrder)
    {
        bool needsUpdate = _cachedOrderedKeys is null
            || sources.Count != _lastSourcesCount
            || (stackOrder?.Count ?? 0) != _lastStackCount
            || !ReferenceEquals(stackOrder, _lastStackOrder);

        if (!needsUpdate)
        {
            foreach (var k in sources.Keys)
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
            foreach (var k in sources.Keys)
            {
                _cachedSourceKeys.Add(k);
                presentList.Add(k);
            }
            _lastSourcesCount = sources.Count;
            _lastStackOrder = stackOrder;
            _lastStackCount = stackOrder?.Count ?? 0;

            if (stackOrder is not null && stackOrder.Count > 0)
            {
                _cachedOrderedKeys = SceneStacking.Resolve(stackOrder, presentList);
            }
            else
            {
                _cachedOrderedKeys = presentList;
            }
        }

        return _cachedOrderedKeys!;
    }

    private void RenderSource(string key, ID3D11SceneSource source, NormalizedRect placement, NormalizedRect crop)
    {
        var task = source.AcquireFrameAsync();
        var acquired = task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
        if (acquired is not null)
        {
            _frameCache.Store(key, acquired, Stopwatch.GetTimestamp());
        }

        var (tex, srv, _, _) = _frameCache.AcquireWithSrv(key);
        if (source.State != SceneSourceState.Lost)
        {
            if (srv is not null)
            {
                _compositor.DrawLayer(srv, placement, crop);
            }
            else if (tex is not null)
            {
                _compositor.DrawLayer(tex, placement, crop);
            }
        }
    }

    private void PumpBlackFrame()
    {
        int requiredSize = (int)_compositor.Nv12ByteSize;
        if (_blackFrame is null || _blackFrame.Length != requiredSize)
        {
            _blackFrame = new byte[requiredSize];
            int ySize = _compositor.CanvasWidth * _compositor.CanvasHeight;
            _blackFrame.AsSpan(0, ySize).Fill(16); // BT.709 black Y
            _blackFrame.AsSpan(ySize).Fill(128);   // BT.709 neutral UV
        }

        try
        {
            _videoPipeStream.Write(_blackFrame);
        }
        catch
        {
            // If the pipe is broken or disconnected, the outer cancellation/stop flow handles it
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }

        if (_isRunning)
        {
            await _renderLoopFinished.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }

        _cts.Dispose();
    }

    [LoggerMessage(LogLevel.Error, "Render loop frame error occurred.")]
    private static partial void LogRenderFrameFailed(ILogger logger, Exception ex);

    [LoggerMessage(LogLevel.Information, "Direct3D 11 device lost or removed. Attempting device recreation.")]
    private static partial void LogAttemptingDeviceRestore(ILogger logger);

    [LoggerMessage(LogLevel.Information, "Direct3D 11 device successfully restored.")]
    private static partial void LogDeviceRestoredSuccess(ILogger logger);

    [LoggerMessage(LogLevel.Error, "Failed to restore Direct3D 11 device following device loss.")]
    private static partial void LogDeviceRestoreFailed(ILogger logger);
}

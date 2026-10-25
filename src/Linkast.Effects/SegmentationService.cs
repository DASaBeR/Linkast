// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Linkast.Core.Camera;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Linkast.Effects;

/// <summary>
/// Shares one segmentation worker per (camera, quality, GPU preference) between all consumers, so the
/// preview and the stream never run the model twice on the same frame. The worker holds its own
/// reference-counted camera feed (the device is still opened only once) and stops, releasing the
/// model and GPU memory, when the last consumer unsubscribes.
/// </summary>
public sealed partial class SegmentationService(ICameraService cameras, ILogger<SegmentationService>? logger = null) : ISegmentationService
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Device, SegmentationQuality Quality, bool Gpu), Worker> _workers = [];

    /// <summary>Workers currently running. Diagnostics and tests.</summary>
    public int ActiveWorkers
    {
        get
        {
            lock (_gate)
            {
                return _workers.Count;
            }
        }
    }

    public ISegmentationSubscription Subscribe(CameraDevice device, CameraFormatRequest format, SegmentationQuality quality, bool preferGpu)
    {
        ArgumentNullException.ThrowIfNull(device);
        var key = (device.Id, quality, preferGpu);
        Worker worker;
        lock (_gate)
        {
            if (!_workers.TryGetValue(key, out worker!))
            {
                worker = new Worker(this, key, cameras.Open(device, format ?? CameraFormatRequest.Auto), quality, preferGpu);
                _workers[key] = worker;
                worker.Start();
            }

            worker.References++;
        }

        return new Subscription(worker);
    }

    private async Task ReleaseAsync(Worker worker)
    {
        lock (_gate)
        {
            if (--worker.References > 0)
            {
                return;
            }

            _workers.Remove(worker.Key);
        }

        await worker.DisposeAsync().ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Segmentation for '{Device}' running on {Provider} ({Quality}){Note}")]
    private partial void LogStarted(string device, string provider, SegmentationQuality quality, string note);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Segmentation for '{Device}' unavailable: {Reason}")]
    private partial void LogUnavailable(string device, string reason);

    private sealed class Subscription(Worker worker) : ISegmentationSubscription
    {
        private int _disposed;

        public SegmentationMask? Latest => worker.Latest;

        public SegmentationStatus Status => worker.Status;

        public event EventHandler? StatusChanged
        {
            add => worker.StatusChanged += value;
            remove => worker.StatusChanged -= value;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await worker.Owner.ReleaseAsync(worker).ConfigureAwait(false);
            }
        }
    }

    private sealed class Worker(SegmentationService owner, (string, SegmentationQuality, bool) key, ICameraFeed feed, SegmentationQuality quality, bool preferGpu) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly SegmentationMask?[] _masks = new SegmentationMask?[3];
        private Thread? _thread;
        private SegmentationMask? _latest;

        public SegmentationService Owner { get; } = owner;

        public (string Device, SegmentationQuality Quality, bool Gpu) Key { get; } = key;

        public int References { get; set; }

        public SegmentationMask? Latest => Volatile.Read(ref _latest);

        public SegmentationStatus Status { get; private set; } = new(SegmentationState.Loading, null, 0, 0, "Loading background model…");

        public event EventHandler? StatusChanged;

        public void Start()
        {
            // A dedicated, below-normal thread: inference must never delay the UI, the encoder or the game.
            _thread = new Thread(Run) { IsBackground = true, Name = "Linkast segmentation", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            await Task.Run(() => _thread?.Join(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            await feed.DisposeAsync().ConfigureAwait(false);
            _cts.Dispose();
        }

        private void Run()
        {
            var ct = _cts.Token;
            RvmSegmenter segmenter;
            try
            {
                segmenter = RvmSegmenter.Create(quality, preferGpu);
            }
            catch (Exception ex) when (ex is ModelUnavailableException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException or DllNotFoundException or InvalidOperationException)
            {
                Owner.LogUnavailable(feed.Device.Name, ex.Message);
                SetStatus(new SegmentationStatus(SegmentationState.Unavailable, null, 0, 0,
                    ex is ModelUnavailableException ? ex.Message : $"Background effects could not start: {ex.Message}"));
                return;
            }

            using (segmenter)
            {
                var note = segmenter.GpuFailure is null ? string.Empty : $"; GPU unavailable: {segmenter.GpuFailure}";
                Owner.LogStarted(feed.Device.Name, segmenter.Provider, quality, note);
                var gpuMessage = segmenter.GpuFailure is null ? null : "The GPU could not be used, so the CPU is running background effects. This uses more CPU.";
                long last = 0;
                var slot = 0;
                double avgMs = 0;
                var windowStart = Stopwatch.GetTimestamp();
                var windowCount = 0;
                var lastFrameAt = Stopwatch.GetTimestamp();
                SetStatus(new SegmentationStatus(SegmentationState.Running, segmenter.Provider, 0, 0, gpuMessage));

                while (!ct.IsCancellationRequested)
                {
                    var frame = feed.State == CameraState.Running ? feed.Frames.TryBeginRead(last) : null;
                    if (frame is null)
                    {
                        // A long gap (camera off, unplugged) makes old temporal state misleading.
                        if (Stopwatch.GetElapsedTime(lastFrameAt) > TimeSpan.FromSeconds(1))
                        {
                            segmenter.Reset();
                            lastFrameAt = Stopwatch.GetTimestamp();
                        }

                        ct.WaitHandle.WaitOne(4);
                        continue;
                    }

                    try
                    {
                        // Rotate three buffers so a renderer reading the latest mask is never overwritten mid-read.
                        var mask = _masks[slot] ??= new SegmentationMask(segmenter.Width, segmenter.Height);
                        var elapsed = segmenter.Process(frame, mask);
                        mask.FrameSequence = frame.Sequence;
                        last = frame.Sequence;
                        lastFrameAt = Stopwatch.GetTimestamp();
                        Volatile.Write(ref _latest, mask);
                        slot = (slot + 1) % _masks.Length;

                        avgMs = avgMs == 0 ? elapsed.TotalMilliseconds : (avgMs * 0.9) + (elapsed.TotalMilliseconds * 0.1);
                        windowCount++;
                        var window = Stopwatch.GetElapsedTime(windowStart);
                        if (window >= TimeSpan.FromSeconds(1))
                        {
                            SetStatus(new SegmentationStatus(SegmentationState.Running, segmenter.Provider, avgMs, windowCount / window.TotalSeconds, gpuMessage));
                            windowCount = 0;
                            windowStart = Stopwatch.GetTimestamp();
                        }
                    }
                    catch (Microsoft.ML.OnnxRuntime.OnnxRuntimeException ex)
                    {
                        // A transient GPU failure (driver reset, device removed): report and stop rather than spin.
                        Owner.LogUnavailable(feed.Device.Name, ex.Message);
                        Volatile.Write(ref _latest, null);
                        SetStatus(new SegmentationStatus(SegmentationState.Unavailable, segmenter.Provider, 0, 0,
                            "Background effects stopped because of a processing error. Switch the effect off and on to retry."));
                        return;
                    }
                    finally
                    {
                        feed.Frames.EndRead(frame);
                    }
                }
            }
        }

        private void SetStatus(SegmentationStatus status)
        {
            Status = status;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

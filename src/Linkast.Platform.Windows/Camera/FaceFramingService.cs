// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Linkast.Core.Camera;
using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;

namespace Linkast.Platform.Windows.Camera;

/// <summary>
/// Auto-framing with Windows' built-in face detector (no extra model, runs locally). One worker per
/// camera layer, shared by its preview and its stream. Detection runs about 10 times per second on a
/// small greyscale copy of the newest frame; the crop is smoothed at 30 Hz by <see cref="AutoFramer"/>.
/// </summary>
public sealed class FaceFramingService(ICameraService cameras) : IFramingService
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Device, string Layer), Worker> _workers = [];

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

    public IFramingSubscription Subscribe(CameraDevice device, CameraFormatRequest format, string layerId, FramingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(device);
        var key = (device.Id, layerId);
        Worker worker;
        lock (_gate)
        {
            if (!_workers.TryGetValue(key, out worker!))
            {
                worker = new Worker(this, key, cameras.Open(device, format ?? CameraFormatRequest.Auto), settings);
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

    /// <summary>Detects faces in a BGRA frame. Exposed for tests. Returns normalized boxes.</summary>
    public static async Task<IReadOnlyList<FaceBox>> DetectAsync(FaceDetector detector, BgraFrame frame, int analysisWidth = 480)
    {
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(frame);
        var w = Math.Min(analysisWidth, frame.Width);
        var h = Math.Max(1, (int)Math.Round((double)frame.Height * w / frame.Width));
        var gray = new byte[w * h];
        var sx = (double)frame.Width / w;
        var sy = (double)frame.Height / h;
        var px = frame.Pixels;
        for (var y = 0; y < h; y++)
        {
            var srcRow = (int)(y * sy) * frame.Width;
            for (var x = 0; x < w; x++)
            {
                var o = (srcRow + (int)(x * sx)) * 4;
                gray[(y * w) + x] = (byte)(((77 * px[o + 2]) + (150 * px[o + 1]) + (29 * px[o])) >> 8);
            }
        }

        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Gray8, w, h);
        bitmap.CopyFromBuffer(gray.AsBuffer());
        var faces = await detector.DetectFacesAsync(bitmap);
        return [.. faces.Select(f => new FaceBox((double)f.FaceBox.X / w, (double)f.FaceBox.Y / h, (double)f.FaceBox.Width / w, (double)f.FaceBox.Height / h))];
    }

    private sealed class Subscription(Worker worker) : IFramingSubscription
    {
        private int _disposed;

        public FramingCrop? Latest => worker.Latest;

        public bool Tracking => worker.Tracking;

        public string? Problem => worker.Problem;

        public void Update(FramingSettings settings) => worker.Settings = settings;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await worker.Owner.ReleaseAsync(worker).ConfigureAwait(false);
            }
        }
    }

    private sealed class Worker(FaceFramingService owner, (string, string) key, ICameraFeed feed, FramingSettings settings) : IAsyncDisposable
    {
        private static readonly TimeSpan DetectEvery = TimeSpan.FromMilliseconds(100);
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;
        private FramingCrop? _latest;

        public FaceFramingService Owner { get; } = owner;

        public (string Device, string Layer) Key { get; } = key;

        public int References { get; set; }

        public FramingSettings Settings { get; set; } = settings;

        public FramingCrop? Latest
        {
            get
            {
                lock (this)
                {
                    return _latest;
                }
            }
        }

        public bool Tracking { get; private set; }

        public string? Problem { get; private set; }

        public void Start() => _loop = Task.Run(async () =>
        {
            try
            {
                await RunAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
            }
#pragma warning disable CA1031 // A framing failure must never affect the stream; report it instead.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Problem = "Auto-framing stopped: " + ex.Message;
                Tracking = false;
            }
        });

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            if (_loop is not null)
            {
                try
                {
                    await _loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            await feed.DisposeAsync().ConfigureAwait(false);
            _cts.Dispose();
        }

        private async Task RunAsync(CancellationToken ct)
        {
            if (!FaceDetector.IsSupported)
            {
                Problem = "Face detection is not available on this version of Windows, so auto-framing is off.";
                return;
            }

            var detector = await FaceDetector.CreateAsync();
            AutoFramer? framer = null;
            var clock = Stopwatch.StartNew();
            var lastDetect = -DetectEvery; // detect on the first tick
            long lastSequence = 0;
            Task<IReadOnlyList<FaceBox>>? detecting = null;

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(33));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = clock.Elapsed;

                if (detecting is { IsCompleted: true })
                {
                    if (detecting.IsCompletedSuccessfully && framer is not null)
                    {
                        framer.OnDetection(detecting.Result, now);
                    }

                    detecting = null;
                }

                if (detecting is null && now - lastDetect >= DetectEvery && feed.State == CameraState.Running)
                {
                    var frame = feed.Frames.TryBeginRead(lastSequence);
                    if (frame is not null)
                    {
                        try
                        {
                            framer ??= new AutoFramer(Settings, (double)frame.Width / frame.Height);
                            lastSequence = frame.Sequence;
                            lastDetect = now;

                            // Copy the small greyscale input now; the frame buffer is returned right after.
                            detecting = DetectAsync(detector, frame);
                        }
                        finally
                        {
                            feed.Frames.EndRead(frame);
                        }
                    }
                }

                if (framer is not null)
                {
                    framer.Settings = Settings.Clamped();
                    var crop = framer.Update(now);
                    lock (this)
                    {
                        _latest = crop;
                    }

                    Tracking = framer.Tracking;
                }
            }
        }
    }
}

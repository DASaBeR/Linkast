// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core.Camera;
using Linkast.Platform.Windows.Camera;
using Windows.Media.FaceAnalysis;
using Xunit.Abstractions;

namespace Linkast.IntegrationTests;

/// <summary>Auto-framing with Windows' real face detector. Needs LINKAST_TEST_PORTRAIT (1280x720 BGRA photo of a person).</summary>
public sealed class FramingIntegrationTests(ITestOutputHelper output)
{
    private static BgraFrame? Portrait()
    {
        var path = Environment.GetEnvironmentVariable("LINKAST_TEST_PORTRAIT");
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        var f = new BgraFrame(1280, 720);
        File.ReadAllBytes(path).CopyTo(f.Pixels, 0);
        return f;
    }

    [SkippableFact]
    public async Task Windows_face_detector_finds_the_face()
    {
        var frame = Portrait();
        Skip.If(frame is null, "Set LINKAST_TEST_PORTRAIT.");
        Skip.IfNot(FaceDetector.IsSupported, "FaceDetector not supported.");
        var detector = await FaceDetector.CreateAsync();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var faces = await FaceFramingService.DetectAsync(detector, frame!);
        output.WriteLine($"{faces.Count} face(s) in {sw.ElapsedMilliseconds} ms: {string.Join(", ", faces)}");

        var face = Assert.Single(faces);
        Assert.InRange(face.CenterX, 0.48, 0.62); // the astronaut's face is around x = 700 / 1280
        Assert.InRange(face.CenterY, 0.35, 0.60);

        // Timing of repeated detections (runs ~10 times per second in the app).
        sw.Restart();
        for (var i = 0; i < 20; i++)
        {
            await FaceFramingService.DetectAsync(detector, frame!);
        }

        output.WriteLine($"average detection: {sw.Elapsed.TotalMilliseconds / 20:0.0} ms");
    }

    [SkippableFact]
    public async Task Framing_worker_zooms_in_on_the_face_from_a_camera_feed()
    {
        var portrait = Portrait();
        Skip.If(portrait is null, "Set LINKAST_TEST_PORTRAIT.");

        // Someone sitting further back: the portrait at half size, off-centre, in a larger frame.
        var frame = new BgraFrame(1280, 720);
        for (var y = 0; y < 360; y++)
        {
            for (var x = 0; x < 640; x++)
            {
                Buffer.BlockCopy(portrait!.Pixels, (((y * 2) * 1280) + (x * 2)) * 4, frame.Pixels, (((y + 300) * 1280) + x + 500) * 4, 4);
            }
        }

        await using var camera = new StillCamera(frame);
        var service = new FaceFramingService(camera);
        await using var sub = service.Subscribe(camera.Device, CameraFormatRequest.Auto, "cam", new FramingSettings { Enabled = true, FaceSize = 0.35 });

        await Task.Delay(4000);
        var crop = sub.Latest;
        output.WriteLine($"crop after 4 s: {crop}, tracking {sub.Tracking}, problem {sub.Problem}");
        Assert.True(sub.Tracking);
        Assert.NotNull(crop);
        Assert.True(crop.Value.Height < 0.7, "Should have zoomed in on the face.");
        Assert.InRange(crop.Value.CenterX, 0.6, 0.72); // face at about x = (500 + 350) / 1280
        Assert.Equal(1, service.ActiveWorkers);
    }

    private sealed class StillCamera(BgraFrame frame) : ICameraService, IAsyncDisposable
    {
        private readonly LatestFrameExchange _frames = new();
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;

        public CameraDevice Device { get; } = new("still", "Still");

        public event EventHandler? DevicesChanged
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<CameraDevice>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CameraDevice>>([Device]);

        public Task<IReadOnlyList<CameraFormat>> GetFormatsAsync(string deviceId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CameraFormat>>([]);

        public ICameraFeed Open(CameraDevice device, CameraFormatRequest format)
        {
            _loop ??= Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(33));
                while (await timer.WaitForNextTickAsync(_cts.Token))
                {
                    var f = _frames.BeginWrite(frame.Width, frame.Height);
                    if (f is not null)
                    {
                        frame.Pixels.CopyTo(f.Pixels, 0);
                        _frames.Publish(TimeSpan.Zero);
                    }
                }
            });
            return new Feed(this);
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try
            {
                if (_loop is not null)
                {
                    await _loop;
                }
            }
            catch (OperationCanceledException)
            {
            }

            _cts.Dispose();
        }

        private sealed class Feed(StillCamera owner) : ICameraFeed
        {
            public CameraDevice Device => owner.Device;

            public CameraState State => CameraState.Running;

            public Linkast.Core.StreamError? LastError => null;

            public CameraFormat? ActiveFormat => null;

            public double MeasuredFps => 30;

            public LatestFrameExchange Frames => owner._frames;

            public IReadOnlyList<HardwareControlInfo> HardwareControls => [];

            public event EventHandler? StateChanged
            {
                add { }
                remove { }
            }

            public Task<bool> SetHardwareControlAsync(HardwareControl control, double? value) => Task.FromResult(false);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Net.Sockets;
using Linkast.Core;
using Linkast.Core.Camera;
using Linkast.Effects;
using Linkast.Engine.FFmpeg;
using Linkast.Networking;
using Linkast.Platform.Windows.Camera;
using Xunit.Abstractions;

namespace Linkast.IntegrationTests;

/// <summary>
/// Full stream path with a real person: a still portrait is played as a 30 fps "camera", and
/// everything after it is real (shared segmentation worker, layer pipe, FFmpeg, relay, MediaMTX).
/// Needs LINKAST_TEST_PORTRAIT (1280x720 BGRA), the model, ffmpeg and the test ingest.
/// This validates the pipeline, not camera hardware.
/// </summary>
[Collection("ingest")]
public sealed class BackgroundEffectsIntegrationTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task Image_background_replaces_the_room_in_the_real_stream()
    {
        var portrait = Environment.GetEnvironmentVariable("LINKAST_TEST_PORTRAIT");
        Skip.If(portrait is null || !File.Exists(portrait), "Set LINKAST_TEST_PORTRAIT.");
        Skip.IfNot(IngestReachable(), "MediaMTX not running.");
        FfmpegInstallation ffmpeg;
        try
        {
            ModelCatalog.Locate(ModelCatalog.Rvm);
            ffmpeg = await FfmpegLocator.LocateAsync();
        }
        catch (Exception ex) when (ex is ModelUnavailableException or StreamFailureException)
        {
            Skip.If(true, ex.Message);
            return;
        }

        // A solid green background image on disk, decoded through the real image cache.
        var greenPath = Path.Combine(Path.GetTempPath(), "linkast-green.bmp");
        await File.WriteAllBytesAsync(greenPath, SolidBmp(64, 36, 0, 200, 0));

        await using var camera = new StillCameraService(File.ReadAllBytes(portrait!), 1280, 720);
        var segmentation = new SegmentationService(camera);
        var images = new BackgroundImageCache();
        var layer = new LiveCameraLayer("cam")
        {
            Device = camera.Device,
            Settings = CameraLayerSettings.Default with
            {
                Shape = LayerShape.Rectangle,
                Background = new BackgroundSettings { Mode = BackgroundMode.Image, ImagePath = greenPath, EdgeSoftness = 0.2 },
            },
        };

        var key = "bg-" + Guid.NewGuid().ToString("N")[..8];
        await using var engine = new FfmpegStreamEngine(ffmpeg, new RelayOutputTransport(),
            layerProvider: new CameraLayerPipeInputProvider(camera, segmentation, images),
            options: new EngineOptions { ConnectTimeout = TimeSpan.FromSeconds(20) });
        engine.StateChanged += (_, e) => output.WriteLine($"state {e.Current} {e.Error}");
        await engine.StartAsync(new StreamSession(
            new BlankCanvasSource(),
            [new TestToneSource()],
            new EncoderSettings("libx264", 1280, 720, 30, 4000, Preset: "ultrafast"),
            new Destination("MediaMTX", new Uri("rtmp://127.0.0.1:1935/live"), new Secret(key)),
            NetworkSelection.SystemDefault,
            ReconnectPolicy.Default)
        {
            CameraLayers = [new CameraLayerBinding(layer, new NormalizedRect(0, 0, 1, 1))],
        });

        var sw = Stopwatch.StartNew();
        while (engine.State != StreamState.Live && sw.Elapsed < TimeSpan.FromSeconds(25))
        {
            await Task.Delay(200);
        }

        Assert.Equal(StreamState.Live, engine.State);
        await Task.Delay(3000); // model warm-up and first masks
        Assert.Equal(1, segmentation.ActiveWorkers); // pipe's subscription; one worker per camera

        var frame = await GrabAsync(ffmpeg, $"rtmp://127.0.0.1:1935/live/{key}");
        (int R, int G, int B) At(int x, int y)
        {
            var o = ((y * 320) + x) * 3;
            return (frame[o], frame[o + 1], frame[o + 2]);
        }

        var flag = At(20, 50);   // left side of the photo: flag / room
        var wall = At(125, 20);  // upper middle: wall
        var face = At(175, 80);  // the astronaut's face
        output.WriteLine($"stream pixels: flag {flag}, wall {wall}, face {face}");
        Assert.True(flag.G > 150 && flag.R < 60 && flag.B < 60, "Room behind the person should be replaced by the green image.");
        Assert.True(wall.G > 150 && wall.R < 60, "Wall should be replaced too.");
        Assert.True(face.R > 120 && face.G < 200 && face.R > face.B, "The face should stay as it is (skin tone, not green).");

        await File.WriteAllBytesAsync(Path.Combine(Path.GetTempPath(), "linkast-bg-stream.rgb"), frame);

        // Switching the effect off releases the model immediately, while live.
        layer.Settings = layer.Settings with { Background = BackgroundSettings.Off };
        await Task.Delay(1000);
        Assert.Equal(0, segmentation.ActiveWorkers);
        Assert.Equal(StreamState.Live, engine.State);
        await engine.StopAsync();
    }

    private static async Task<byte[]> GrabAsync(FfmpegInstallation ffmpeg, string url)
    {
        var psi = new ProcessStartInfo(ffmpeg.Path) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", "-rw_timeout", "5000000", "-i", url, "-frames:v", "1", "-vf", "scale=320:180", "-f", "rawvideo", "-pix_fmt", "rgb24", "-" })
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        using var ms = new MemoryStream();
        _ = p.StandardError.ReadToEndAsync();
        await p.StandardOutput.BaseStream.CopyToAsync(ms);
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        return ms.ToArray();
    }

    private static byte[] SolidBmp(int w, int h, byte r, byte g, byte b)
    {
        var rowBytes = w * 3;
        var pad = (4 - (rowBytes % 4)) % 4;
        var imageSize = (rowBytes + pad) * h;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((byte)'B');
        bw.Write((byte)'M');
        bw.Write(54 + imageSize);
        bw.Write(0);
        bw.Write(54);
        bw.Write(40);
        bw.Write(w);
        bw.Write(h);
        bw.Write((short)1);
        bw.Write((short)24);
        bw.Write(0);
        bw.Write(imageSize);
        bw.Write(2835);
        bw.Write(2835);
        bw.Write(0);
        bw.Write(0);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                bw.Write(b);
                bw.Write(g);
                bw.Write(r);
            }

            for (var i = 0; i < pad; i++)
            {
                bw.Write((byte)0);
            }
        }

        return ms.ToArray();
    }

    private static bool IngestReachable()
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync("127.0.0.1", 1935).Wait(TimeSpan.FromSeconds(1)) && c.Connected;
        }
        catch (AggregateException)
        {
            return false;
        }
    }

    /// <summary>Test camera: publishes one still image as a 30 fps feed.</summary>
    private sealed class StillCameraService : ICameraService, IAsyncDisposable
    {
        private readonly LatestFrameExchange _frames = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public StillCameraService(byte[] bgra, int width, int height)
        {
            _loop = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(33));
                while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
                {
                    var f = _frames.BeginWrite(width, height);
                    if (f is not null)
                    {
                        bgra.AsSpan(0, f.Pixels.Length).CopyTo(f.Pixels);
                        _frames.Publish(TimeSpan.Zero);
                    }
                }
            });
        }

        public CameraDevice Device { get; } = new("test-still", "Still portrait");

        public event EventHandler? DevicesChanged
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<CameraDevice>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CameraDevice>>([Device]);

        public Task<IReadOnlyList<CameraFormat>> GetFormatsAsync(string deviceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CameraFormat>>([new CameraFormat(1280, 720, 30, 1, "BGRA")]);

        public ICameraFeed Open(CameraDevice device, CameraFormatRequest format) => new Feed(this);

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }

            _cts.Dispose();
        }

        private sealed class Feed(StillCameraService owner) : ICameraFeed
        {
            public CameraDevice Device => owner.Device;

            public CameraState State => CameraState.Running;

            public StreamError? LastError => null;

            public CameraFormat? ActiveFormat => new(1280, 720, 30, 1, "BGRA");

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

// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Net.Sockets;
using Linkast.Core;
using Linkast.Core.Audio;
using Linkast.Core.Camera;
using Linkast.Engine.FFmpeg;
using Linkast.Networking;
using Linkast.Platform.Windows.Audio;
using Linkast.Platform.Windows.Camera;
using Xunit.Abstractions;

namespace Linkast.IntegrationTests;

/// <summary>
/// Phase 4: shared audio capture, several camera layers in one stream, and vertical output.
/// Camera tests are skipped without a camera; streaming tests need the MediaMTX ingest.
/// </summary>
[Collection("ingest")]
public sealed class MultiCameraIntegrationTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly MediaCaptureCameraService _cameras = new();
    private CameraDevice? _camera;
    private FfmpegInstallation? _ffmpeg;

    public async Task InitializeAsync()
    {
        _camera = (await _cameras.ListAsync()).FirstOrDefault();
        try
        {
            _ffmpeg = await FfmpegLocator.LocateAsync();
        }
        catch (StreamFailureException)
        {
            _ffmpeg = null;
        }
    }

    public Task DisposeAsync()
    {
        _cameras.Dispose();
        return Task.CompletedTask;
    }

    [SkippableFact]
    public async Task Audio_hub_opens_one_session_per_device_for_all_subscribers()
    {
        var outputs = AudioDeviceCatalog.List(AudioDeviceKind.Output);
        Skip.If(outputs.Count == 0, "No audio output device.");

        // Loopback only delivers buffers while something plays, so this part checks session sharing only.
        var hub = new AudioCaptureHub();
        var first = hub.Subscribe(null, loopback: true, (_, _) => { });
        var second = hub.Subscribe(null, loopback: true, (_, _) => { });
        Assert.Equal(1, hub.ActiveSessions);
        output.WriteLine($"Two loopback subscribers, one session ({first.DeviceName}, {first.Format})");

        first.Dispose();
        Assert.Equal(1, hub.ActiveSessions);
        second.Dispose();
        Assert.Equal(0, hub.ActiveSessions);

        // The voice meter and a stream input on the same microphone share one capture too.
        var inputs = AudioDeviceCatalog.List(AudioDeviceKind.Input);
        if (inputs.Count > 0)
        {
            var bus = new AudioLevelBus();
            using var monitor = new AudioLevelMonitor(hub, bus);
            monitor.Start(inputs[0].Id);
            output.WriteLine($"Meter on '{inputs[0].Name}': {monitor.Problem ?? "ok"}");
            if (monitor.Problem is null)
            {
                // A microphone delivers buffers continuously, so data flow to both subscribers is checked here.
                var received = 0;
                using var stream = hub.Subscribe(inputs[0].Id, loopback: false, (_, _) => Interlocked.Increment(ref received));
                Assert.Equal(1, hub.ActiveSessions);
                Assert.True(bus.Active);
                await WaitUntilAsync(() => Volatile.Read(ref received) > 5, TimeSpan.FromSeconds(5));
                output.WriteLine($"Second subscriber got {received} buffers; meter level {bus.Meter.LevelDb:0.0} dB");
            }

            monitor.Stop();
            Assert.Equal(0, hub.ActiveSessions);
        }
    }

    [SkippableFact]
    public async Task Two_layers_of_one_camera_share_the_device_and_hide_live()
    {
        SkipUnlessReady(needCamera: true);

        var settings = CameraLayerSettings.Default with { Shape = LayerShape.Rectangle, BorderWidth = 0.08, BorderColor = Argb.White };
        var close = new LiveCameraLayer("close") { Device = _camera, Settings = settings };
        var wide = new LiveCameraLayer("wide") { Device = _camera, Settings = settings with { Zoom = 1 } };
        var key = "multi-" + Guid.NewGuid().ToString("N")[..8];
        await using var engine = Engine();

        var session = Session(key, new BlankCanvasSource(), 640, 360) with
        {
            CameraLayers =
            [
                new CameraLayerBinding(wide, new NormalizedRect(0.05, 0.05, 0.35, 0.35)),
                new CameraLayerBinding(close, new NormalizedRect(0.6, 0.6, 0.35, 0.35)),
            ],
        };

        await engine.StartAsync(session);
        await WaitUntilAsync(() => engine.State == StreamState.Live, TimeSpan.FromSeconds(25));
        await Task.Delay(2000);
        output.WriteLine($"Camera sessions while streaming two layers of one camera: {_cameras.OpenSessionCount}");
        Assert.Equal(1, _cameras.OpenSessionCount);

        var url = $"rtmp://127.0.0.1:1935/live/{key}";
        var both = await GrabAsync(url, 320, 180);
        var (tl, br) = (Brightness(both, 320, 180, 0.05, 0.05, 0.35), Brightness(both, 320, 180, 0.6, 0.6, 0.35));
        output.WriteLine($"Both shown: top-left {tl:0.0}, bottom-right {br:0.0}");
        Assert.True(tl > 15 && br > 15, "Both camera layers should be visible.");

        Assert.True(await engine.HideLayerAsync("close"), "Hide command should succeed.");
        await Task.Delay(1500);
        var hidden = await GrabAsync(url, 320, 180);
        var (tl2, br2) = (Brightness(hidden, 320, 180, 0.05, 0.05, 0.35), Brightness(hidden, 320, 180, 0.6, 0.6, 0.35));
        output.WriteLine($"After hiding 'close': top-left {tl2:0.0}, bottom-right {br2:0.0}");
        Assert.True(tl2 > 15, "The other layer stays visible.");
        Assert.True(br2 < 10, "The hidden layer is gone.");

        Assert.True(await engine.MoveLayerAsync("close", new NormalizedRect(0.6, 0.6, 0.35, 0.35)), "Showing again should succeed.");
        await Task.Delay(1500);
        var back = await GrabAsync(url, 320, 180);
        Assert.True(Brightness(back, 320, 180, 0.6, 0.6, 0.35) > 15, "The layer reappears when moved back on the canvas.");
        Assert.Equal(StreamState.Live, engine.State);

        await engine.StopAsync();
        await WaitUntilAsync(() => _cameras.OpenSessionCount == 0, TimeSpan.FromSeconds(5));
    }

    [SkippableFact]
    public async Task Vertical_layout_streams_portrait_with_gameplay_on_top()
    {
        SkipUnlessReady(needCamera: false);

        var key = "vert-" + Guid.NewGuid().ToString("N")[..8];
        await using var engine = Engine();
        var layout = SceneLayout.DefaultVertical("cam");
        var (w, h) = layout.CanvasSize(640, 360);
        var session = Session(key, new TestPatternSource(), w, h) with
        {
            BasePlacement = layout.BasePlacement,
            BaseFit = layout.BaseFit,
            // A white border makes the layer measurable whatever the camera sees (a phone camera may send a dark picture).
            CameraLayers = _camera is null ? [] :
            [
                new CameraLayerBinding(
                    new LiveCameraLayer("cam") { Device = _camera, Settings = CameraLayerSettings.Default with { Shape = LayerShape.Rectangle, BorderWidth = 0.08, BorderColor = Argb.White } },
                    layout.CameraPlacements["cam"]),
            ],
        };

        await engine.StartAsync(session);
        await WaitUntilAsync(() => engine.State == StreamState.Live, TimeSpan.FromSeconds(25));
        await Task.Delay(2000);

        var url = $"rtmp://127.0.0.1:1935/live/{key}";
        var size = await ProbeSizeAsync(url);
        output.WriteLine($"Stream size: {size}");
        Assert.Equal("360x640", size);

        // Gameplay (the colourful test pattern) fills the top 56 % edge to edge; with Fill there are no bars.
        var frame = await GrabAsync(url, 180, 320);
        var top = Brightness(frame, 180, 320, 0.02, 0.05, 0.4);
        var topRight = Brightness(frame, 180, 320, 0.6, 0.05, 0.38);
        output.WriteLine($"Top-left {top:0.0}, top-right {topRight:0.0}");
        Assert.True(top > 15 && topRight > 15, "The gameplay should fill the top area.");
        if (_camera is not null)
        {
            // Whole camera area (0.56 to 1.0 of the height): the border alone lifts it well above black.
            var bottom = Brightness(frame, 180, 320, 0, 0.57, 0.53);
            var bottomRight = Brightness(frame, 180, 320, 0.55, 0.57, 0.53);
            output.WriteLine($"Camera area left {bottom:0.0}, right {bottomRight:0.0}");
            Assert.True(bottom > 15 && bottomRight > 15, "The camera should fill the bottom area.");
        }

        await engine.StopAsync();
        Assert.Equal(StreamState.Stopped, engine.State);
    }

    private FfmpegStreamEngine Engine()
    {
        var engine = new FfmpegStreamEngine(_ffmpeg!, new RelayOutputTransport(), layerProvider: new CameraLayerPipeInputProvider(_cameras),
            options: new EngineOptions { ConnectTimeout = TimeSpan.FromSeconds(20) });
        engine.StateChanged += (_, e) => output.WriteLine($"state {e.Previous} -> {e.Current} {e.Error}");
        engine.DiagnosticLine += (_, l) => output.WriteLine("ffmpeg: " + l);
        return engine;
    }

    private static StreamSession Session(string key, VideoSourceSpec video, int width, int height) => new(
        video,
        [new TestToneSource()],
        new EncoderSettings("libx264", width, height, 30, 1500, Preset: "ultrafast"),
        new Destination("MediaMTX", new Uri("rtmp://127.0.0.1:1935/live"), new Secret(key)),
        NetworkSelection.SystemDefault,
        ReconnectPolicy.Default);

    private void SkipUnlessReady(bool needCamera)
    {
        Skip.If(needCamera && _camera is null, "No camera connected.");
        Skip.If(_ffmpeg is null, "ffmpeg not found.");
        Skip.IfNot(IngestReachable(), "MediaMTX not running.");
    }

    private async Task<string> ProbeSizeAsync(string url)
    {
        var ffprobe = Path.Combine(Path.GetDirectoryName(_ffmpeg!.Path)!, "ffprobe.exe");
        var psi = new ProcessStartInfo(ffprobe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", "-rw_timeout", "5000000", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=s=x:p=0", url })
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        return stdout.Trim();
    }

    /// <summary>Grabs one RGB frame from the ingest at the given size.</summary>
    private async Task<byte[]> GrabAsync(string url, int width, int height)
    {
        var psi = new ProcessStartInfo(_ffmpeg!.Path) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", "-rw_timeout", "5000000", "-i", url, "-frames:v", "1", "-vf", $"scale={width}:{height}", "-f", "rawvideo", "-pix_fmt", "rgb24", "-" })
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        using var ms = new MemoryStream();
        _ = p.StandardError.ReadToEndAsync();
        await p.StandardOutput.BaseStream.CopyToAsync(ms);
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(width * height * 3, ms.Length);
        return ms.ToArray();
    }

    /// <summary>Mean brightness of a region (normalized x, y and size of the frame width/height).</summary>
    private static double Brightness(byte[] rgb, int width, int height, double nx, double ny, double size)
    {
        int x0 = (int)(nx * width), y0 = (int)(ny * height), w = (int)(size * width * 0.8), h = (int)(size * height * 0.8);
        double sum = 0;
        var n = 0;
        for (var y = y0; y < y0 + h; y++)
        {
            for (var x = x0; x < x0 + w; x++)
            {
                var o = ((y * width) + x) * 3;
                sum += (rgb[o] + rgb[o + 1] + rgb[o + 2]) / 3.0;
                n++;
            }
        }

        return sum / n;
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

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout)
            {
                throw new Xunit.Sdk.XunitException("Timed out waiting for condition.");
            }

            await Task.Delay(100);
        }
    }
}

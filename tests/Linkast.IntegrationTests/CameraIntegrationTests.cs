// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Net.Sockets;
using Linkast.Core;
using Linkast.Core.Camera;
using Linkast.Engine.FFmpeg;
using Linkast.Networking;
using Linkast.Platform.Windows.Camera;
using Xunit.Abstractions;

namespace Linkast.IntegrationTests;

/// <summary>
/// Real camera tests. Skipped when no camera is connected. Streaming tests also need the
/// MediaMTX ingest from tools/test-ingest.
/// </summary>
[Collection("ingest")]
public sealed class CameraIntegrationTests(ITestOutputHelper output) : IAsyncLifetime
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
    public async Task Camera_opens_delivers_frames_and_is_fully_released()
    {
        Skip.If(_camera is null, "No camera connected.");
        output.WriteLine($"Camera: {_camera}");

        var formats = await _cameras.GetFormatsAsync(_camera.Id);
        output.WriteLine($"{formats.Count} formats, auto picks {CameraFormatRequest.Auto.Choose(formats)}");
        Assert.NotEmpty(formats);

        var sw = Stopwatch.StartNew();
        var feed = _cameras.Open(_camera, CameraFormatRequest.Auto);
        await WaitUntilAsync(() => feed.Frames.PublishedCount > 10, TimeSpan.FromSeconds(10));
        output.WriteLine($"10 frames after {sw.ElapsedMilliseconds} ms; state {feed.State}; format {feed.ActiveFormat}; {feed.MeasuredFps:0.0} fps");
        Assert.Equal(CameraState.Running, feed.State);

        // A second consumer shares the same physical session.
        var second = _cameras.Open(_camera, CameraFormatRequest.Auto);
        Assert.Equal(1, _cameras.OpenSessionCount);

        await second.DisposeAsync();
        Assert.Equal(1, _cameras.OpenSessionCount);
        sw.Restart();
        await feed.DisposeAsync();
        output.WriteLine($"Released in {sw.ElapsedMilliseconds} ms");
        Assert.Equal(0, _cameras.OpenSessionCount);

        // Reopening right away works: nothing was leaked.
        var again = _cameras.Open(_camera, CameraFormatRequest.Auto);
        await WaitUntilAsync(() => again.Frames.PublishedCount > 3, TimeSpan.FromSeconds(10));
        await again.DisposeAsync();
        Assert.Equal(0, _cameras.OpenSessionCount);
    }

    [SkippableFact]
    public async Task Camera_layer_streams_moves_live_and_survives_camera_off()
    {
        Skip.If(_camera is null, "No camera connected.");
        Skip.If(_ffmpeg is null, "ffmpeg not found.");
        Skip.IfNot(IngestReachable(), "MediaMTX not running.");

        var layer = new LiveCameraLayer("cam")
        {
            Device = _camera,
            Settings = CameraLayerSettings.Default with { Shape = LayerShape.Rectangle, BorderWidth = 0.08, BorderColor = Argb.White },
        };
        var bottomRight = new NormalizedRect(0.6, 0.6, 0.35, 0.35);
        var key = "cam-" + Guid.NewGuid().ToString("N")[..8];
        var provider = new CameraLayerPipeInputProvider(_cameras);
        await using var engine = new FfmpegStreamEngine(_ffmpeg!, new RelayOutputTransport(), layerProvider: provider,
            options: new EngineOptions { ConnectTimeout = TimeSpan.FromSeconds(20) });
        engine.StateChanged += (_, e) => output.WriteLine($"state {e.Previous} -> {e.Current} {e.Error}");
        engine.DiagnosticLine += (_, l) => output.WriteLine("ffmpeg: " + l);

        var session = new StreamSession(
            new BlankCanvasSource(),
            [new TestToneSource()],
            new EncoderSettings("libx264", 640, 360, 30, 1500, Preset: "ultrafast"),
            new Destination("MediaMTX", new Uri("rtmp://127.0.0.1:1935/live"), new Secret(key)),
            NetworkSelection.SystemDefault,
            ReconnectPolicy.Default)
        {
            CameraLayers = [new CameraLayerBinding(layer, bottomRight)],
        };

        await engine.StartAsync(session);
        await WaitUntilAsync(() => engine.State == StreamState.Live, TimeSpan.FromSeconds(25));
        await Task.Delay(1500);

        var url = $"rtmp://127.0.0.1:1935/live/{key}";
        var before = await GrabAsync(url);
        var (brBefore, tlBefore) = (Brightness(before, 0.6, 0.6, 0.35), Brightness(before, 0.05, 0.05, 0.3));
        output.WriteLine($"Before move: bottom-right {brBefore:0.0}, top-left {tlBefore:0.0}");
        Assert.True(brBefore > 15, "Camera layer should be visible bottom-right.");
        Assert.True(tlBefore < 10, "Top-left should be the black canvas.");

        var moved = await engine.MoveLayerAsync("cam", new NormalizedRect(0.05, 0.05, 0.3, 0.3));
        Assert.True(moved, "Live move command should succeed.");
        await Task.Delay(1500);
        var after = await GrabAsync(url);
        var (brAfter, tlAfter) = (Brightness(after, 0.65, 0.65, 0.3), Brightness(after, 0.05, 0.05, 0.3));
        output.WriteLine($"After move: bottom-right {brAfter:0.0}, top-left {tlAfter:0.0}");
        Assert.True(tlAfter > 15, "Layer should now be top-left.");
        Assert.True(brAfter < 10, "Bottom-right should now be black.");
        Assert.Equal(StreamState.Live, engine.State);

        // Camera off while live: the layer turns transparent, the stream keeps running.
        layer.Enabled = false;
        await Task.Delay(2000);
        var off = await GrabAsync(url);
        var tlOff = Brightness(off, 0.05, 0.05, 0.3);
        output.WriteLine($"Camera off: top-left {tlOff:0.0}; sessions open {_cameras.OpenSessionCount}");
        Assert.True(tlOff < 10, "Layer should be transparent with the camera off.");
        Assert.Equal(0, _cameras.OpenSessionCount);
        Assert.Equal(StreamState.Live, engine.State);

        // And back on.
        layer.Enabled = true;
        await Task.Delay(4000);
        var on = await GrabAsync(url);
        Assert.True(Brightness(on, 0.05, 0.05, 0.3) > 15, "Layer should return when the camera is switched back on.");

        await engine.StopAsync();
        Assert.Equal(StreamState.Stopped, engine.State);
        await WaitUntilAsync(() => _cameras.OpenSessionCount == 0, TimeSpan.FromSeconds(5));
    }

    /// <summary>Grabs one 320x180 RGB frame from the ingest.</summary>
    private async Task<byte[]> GrabAsync(string url)
    {
        var psi = new ProcessStartInfo(_ffmpeg!.Path) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", "-rw_timeout", "5000000", "-i", url, "-frames:v", "1", "-vf", "scale=320:180", "-f", "rawvideo", "-pix_fmt", "rgb24", "-" })
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        using var ms = new MemoryStream();
        _ = p.StandardError.ReadToEndAsync();
        await p.StandardOutput.BaseStream.CopyToAsync(ms);
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(320 * 180 * 3, ms.Length);
        var bytes = ms.ToArray();
        var dump = Path.Combine(Path.GetTempPath(), $"linkast-cam-{DateTime.Now:HHmmssfff}.rgb");
        await File.WriteAllBytesAsync(dump, bytes);
        output.WriteLine("frame dump: " + dump);
        return bytes;
    }

    /// <summary>Mean brightness of a square region (normalized x, y, size) of a 320x180 RGB frame.</summary>
    private static double Brightness(byte[] rgb, double nx, double ny, double size)
    {
        int x0 = (int)(nx * 320), y0 = (int)(ny * 180), w = (int)(size * 320 * 0.8), h = (int)(size * 180 * 0.8);
        double sum = 0;
        var n = 0;
        for (var y = y0; y < y0 + h; y++)
        {
            for (var x = x0; x < x0 + w; x++)
            {
                var o = ((y * 320) + x) * 3;
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

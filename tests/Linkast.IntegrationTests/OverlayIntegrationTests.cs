// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Net.Sockets;
using Linkast.Core;
using Linkast.Core.Camera;
using Linkast.Engine.FFmpeg;
using Linkast.Networking;
using Linkast.Platform.Windows.Layers;
using Xunit.Abstractions;

namespace Linkast.IntegrationTests;

/// <summary>
/// Overlays and the away screen through the real FFmpeg pipeline to MediaMTX: a still overlay is
/// drawn where it was placed, and the away screen starts hidden, covers the stream live and hides again.
/// </summary>
[Collection("ingest")]
public sealed class OverlayIntegrationTests(ITestOutputHelper output) : IAsyncLifetime
{
    private FfmpegInstallation? _ffmpeg;

    public async Task InitializeAsync()
    {
        try
        {
            _ffmpeg = await FfmpegLocator.LocateAsync();
        }
        catch (StreamFailureException)
        {
            _ffmpeg = null;
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Overlay_is_drawn_and_away_screen_covers_the_stream_live()
    {
        Skip.If(_ffmpeg is null, "ffmpeg not found.");
        Skip.IfNot(IngestReachable(), "MediaMTX not running.");

        var key = "overlay-" + Guid.NewGuid().ToString("N")[..8];
        await using var engine = new FfmpegStreamEngine(_ffmpeg!, new RelayOutputTransport(),
            options: new EngineOptions { ConnectTimeout = TimeSpan.FromSeconds(20) },
            overlayProvider: new WhiteOverlayProvider());
        engine.StateChanged += (_, e) => output.WriteLine($"state {e.Previous} -> {e.Current} {e.Error}");
        engine.DiagnosticLine += (_, l) => output.WriteLine("ffmpeg: " + l);

        var full = new NormalizedRect(0, 0, 1, 1);
        var session = new StreamSession(
            new BlankCanvasSource(),
            [new TestToneSource()],
            new EncoderSettings("libx264", 640, 360, 30, 1500, Preset: "ultrafast"),
            new Destination("MediaMTX", new Uri("rtmp://127.0.0.1:1935/live"), new Secret(key)),
            NetworkSelection.SystemDefault,
            ReconnectPolicy.Default)
        {
            Overlays =
            [
                new OverlayLayerBinding("logo", new ImageOverlaySource("test"), new NormalizedRect(0.75, 0.05, 0.2, 0.2)),
                new OverlayLayerBinding("away", new AwayScreenSource("Be right back", null, null), full, StartHidden: true),
            ],
        };

        await engine.StartAsync(session);
        await WaitUntilAsync(() => engine.State == StreamState.Live, TimeSpan.FromSeconds(25));
        await Task.Delay(1500);
        var url = $"rtmp://127.0.0.1:1935/live/{key}";

        var start = await GrabAsync(url, 320, 180);
        var (logo, centre) = (Brightness(start, 320, 180, 0.76, 0.06, 0.18), Brightness(start, 320, 180, 0.4, 0.4, 0.2));
        output.WriteLine($"Start: logo {logo:0}, centre {centre:0}");
        Assert.True(logo > 200, "The overlay is drawn where it was placed.");
        Assert.True(centre < 40, "The away screen starts hidden.");

        Assert.True(await engine.MoveLayerAsync("away", full), "Showing the away screen live should succeed.");
        await Task.Delay(1500);
        var away = await GrabAsync(url, 320, 180);
        output.WriteLine($"Away: centre {Brightness(away, 320, 180, 0.4, 0.4, 0.2):0}");
        Assert.True(Brightness(away, 320, 180, 0.4, 0.4, 0.2) > 200, "The away screen covers the stream.");

        Assert.True(await engine.HideLayerAsync("away"), "Hiding the away screen live should succeed.");
        await Task.Delay(1500);
        var back = await GrabAsync(url, 320, 180);
        output.WriteLine($"Back: centre {Brightness(back, 320, 180, 0.4, 0.4, 0.2):0}");
        Assert.True(Brightness(back, 320, 180, 0.4, 0.4, 0.2) < 40, "The stream is visible again.");

        await engine.StopAsync();
    }

    /// <summary>Every overlay is solid white, so placement can be checked by brightness.</summary>
    private sealed class WhiteOverlayProvider : IOverlayInputProvider
    {
        public IVideoLayerInput Open(OverlayLayerBinding overlay, int width, int height, int maxFrameRate)
        {
            var frame = new byte[width * height * 4];
            Array.Fill(frame, (byte)255);
            return new StaticFramePipeInput(overlay.LayerId, frame, width, height);
        }
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

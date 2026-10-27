// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Net.Sockets;
using Linkast.Core;
using Linkast.Core.Camera;
using Linkast.Engine.FFmpeg;
using Linkast.Networking;
using Xunit.Abstractions;

namespace Linkast.IntegrationTests;

/// <summary>
/// What a scene switch does to a running stream, through the real FFmpeg pipeline to MediaMTX: the
/// captured picture is hidden, moved and trimmed live, and a scene's picture is shown and hidden live.
/// </summary>
[Collection("ingest")]
public sealed class SceneIntegrationTests(ITestOutputHelper output) : IAsyncLifetime
{
    private FfmpegInstallation? _ffmpeg;
    private readonly string _dir = Directory.CreateTempSubdirectory("linkast-scene").FullName;

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

    public Task DisposeAsync()
    {
        Directory.Delete(_dir, true);
        return Task.CompletedTask;
    }

    [SkippableFact]
    public async Task Scene_switch_hides_moves_the_game_and_shows_a_scene_picture_live()
    {
        Skip.If(_ffmpeg is null, "ffmpeg not found.");
        Skip.IfNot(IngestReachable(), "MediaMTX not running.");

        // A white picture stands in for a scene's card, so it can be found by brightness.
        var card = Path.Combine(_dir, "card.png");
        await RunFfmpegAsync("-f", "lavfi", "-i", "color=c=white:s=320x180", "-frames:v", "1", "-y", card);

        var key = "scene-" + Guid.NewGuid().ToString("N")[..8];
        await using var engine = new FfmpegStreamEngine(_ffmpeg!, new RelayOutputTransport(), options: new EngineOptions { ConnectTimeout = TimeSpan.FromSeconds(20) });
        engine.StateChanged += (_, e) => output.WriteLine($"state {e.Previous} -> {e.Current} {e.Error}");
        engine.DiagnosticLine += (_, l) => output.WriteLine("ffmpeg: " + l);

        var full = new NormalizedRect(0, 0, 1, 1);
        var session = new StreamSession(
            new TestPatternSource(),
            [new TestToneSource()],
            new EncoderSettings("libx264", 640, 360, 30, 1500, Preset: "ultrafast"),
            new Destination("MediaMTX", new Uri("rtmp://127.0.0.1:1935/live"), new Secret(key)),
            NetworkSelection.SystemDefault,
            ReconnectPolicy.Default)
        {
            Media = [new MediaLayerBinding("scene-card", card, IsVideo: false, StartHidden: true)],
        };

        await engine.StartAsync(session);
        await WaitUntilAsync(() => engine.State == StreamState.Live, TimeSpan.FromSeconds(25));
        await Task.Delay(1500);
        var url = $"rtmp://127.0.0.1:1935/live/{key}";

        var start = await GrabAsync(url, 320, 180);
        output.WriteLine($"Start: centre {Brightness(start, 0.4, 0.4, 0.2):0}");
        Assert.True(Brightness(start, 0.4, 0.4, 0.2) > 40, "The test pattern fills the canvas at the start.");

        // A scene without the game: the capture goes off the canvas.
        Assert.True(await engine.SetBaseAsync(null, full, BaseFit.Fit), "Hiding the game live should succeed.");
        await Task.Delay(1500);
        var hidden = await GrabAsync(url, 320, 180);
        output.WriteLine($"Hidden: centre {Brightness(hidden, 0.4, 0.4, 0.2):0}");
        Assert.True(Brightness(hidden, 0.4, 0.4, 0.2) < 25, "Only the black canvas is left.");

        // The game back, small in the bottom-right quarter, with its left half trimmed away.
        Assert.True(await engine.SetBaseAsync(new NormalizedRect(0.5, 0.5, 0.5, 0.5), new NormalizedRect(0.5, 0, 0.5, 1), BaseFit.Fill));
        await Task.Delay(1500);
        var moved = await GrabAsync(url, 320, 180);
        output.WriteLine($"Moved: top-left {Brightness(moved, 0.05, 0.05, 0.35):0}, bottom-right {Brightness(moved, 0.6, 0.6, 0.35):0}");
        Assert.True(Brightness(moved, 0.05, 0.05, 0.35) < 25, "The top-left quarter is empty now.");
        Assert.True(Brightness(moved, 0.6, 0.6, 0.35) > 40, "The game is in the bottom-right quarter.");

        // A scene with a card: the still picture covers the canvas, then goes again.
        Assert.True(await engine.MoveLayerAsync("scene-card", full), "Showing the scene picture live should succeed.");
        await Task.Delay(1500);
        var shown = await GrabAsync(url, 320, 180);
        output.WriteLine($"Card: centre {Brightness(shown, 0.4, 0.4, 0.2):0}");
        Assert.True(Brightness(shown, 0.4, 0.4, 0.2) > 200, "The scene picture covers the stream.");

        Assert.True(await engine.HideLayerAsync("scene-card"));
        await Task.Delay(1500);
        var back = await GrabAsync(url, 320, 180);
        Assert.True(Brightness(back, 0.6, 0.6, 0.35) > 40 && Brightness(back, 0.05, 0.05, 0.35) < 25, "The game is back where it was.");

        await engine.StopAsync();
    }

    private async Task RunFfmpegAsync(params string[] args)
    {
        var psi = new ProcessStartInfo(_ffmpeg!.Path) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        _ = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, p.ExitCode);
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

    /// <summary>Mean brightness of a region of a 320 × 180 frame (normalized x, y and size).</summary>
    private static double Brightness(byte[] rgb, double nx, double ny, double size)
    {
        const int width = 320, height = 180;
        int x0 = (int)(nx * width), y0 = (int)(ny * height), w = (int)(size * width), h = (int)(size * height);
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

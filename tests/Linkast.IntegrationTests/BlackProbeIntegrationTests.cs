// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Linkast.Core;
using Linkast.Engine.FFmpeg;
using Linkast.Networking;
using Xunit.Abstractions;

namespace Linkast.IntegrationTests;

/// <summary>The black probe's graph on the real FFmpeg: the escaping holds and both marks reach stderr.</summary>
public sealed class BlackProbeIntegrationTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task Reports_black_start_and_end_on_stderr()
    {
        FfmpegInstallation ffmpeg;
        try
        {
            ffmpeg = await FfmpegLocator.LocateAsync();
        }
        catch (StreamFailureException)
        {
            throw new SkipException("ffmpeg not found.");
        }

        // 3 s of black, then 3 s of picture, through the same probe the window capture uses.
        string[] args =
        [
            "-hide_banner", "-nostats", "-loglevel", "level+warning",
            "-f", "lavfi", "-i", "color=c=black:s=1280x720:r=30:d=3[a];testsrc2=s=1280x720:r=30:d=3[b];[a][b]concat",
            "-filter_complex", "[0:v]format=bgra,fps=30," + BlackProbe.Graph + ";[cap]format=nv12[vout]",
            "-map", "[vout]", "-f", "null", "-",
        ];
        var marks = new ConcurrentQueue<bool>();
        var other = new ConcurrentQueue<string>();
        await using var process = FfmpegProcess.Start(ffmpeg.Path, args, _ => { }, line =>
        {
            if (BlackProbe.Parse(line) is { } black)
            {
                marks.Enqueue(black);
            }
            else if (!BlackProbe.IsProbeLine(line) && line.Length > 0)
            {
                other.Enqueue(line);
            }
        });

        var exit = await process.Exited.WaitAsync(TimeSpan.FromSeconds(60));
        foreach (var line in other)
        {
            output.WriteLine(line);
        }

        Assert.Equal(0, exit);
        Assert.Equal([true, false], marks.ToArray());
    }

    /// <summary>
    /// End to end: Windows Graphics Capture of a borderless window (ffplay showing a picture),
    /// recorded through the engine. Telemetry says the capture looks black only for a black window.
    /// </summary>
    [SkippableTheory]
    [InlineData("color=c=black:s=640x360:r=30", true)]
    [InlineData("testsrc2=s=640x360:r=30", false)]
    public async Task Window_capture_telemetry_says_black_only_for_a_black_window(string picture, bool expected)
    {
        FfmpegInstallation ffmpeg;
        try
        {
            ffmpeg = await FfmpegLocator.LocateAsync();
        }
        catch (StreamFailureException)
        {
            throw new SkipException("ffmpeg not found.");
        }

        var ffplay = Path.Combine(Path.GetDirectoryName(ffmpeg.Path)!, "ffplay.exe");
        Skip.IfNot(File.Exists(ffplay), "ffplay not found next to ffmpeg.");

        var folder = Path.Combine(Path.GetTempPath(), "linkast-black-" + Guid.NewGuid().ToString("N")[..6]);
        using var player = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ffplay)
        {
            ArgumentList = { "-hide_banner", "-loglevel", "quiet", "-noborder", "-an", "-x", "640", "-y", "360", "-f", "lavfi", picture },
            UseShellExecute = false,
        })!;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (player.MainWindowHandle == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
                player.Refresh();
            }

            Skip.If(player.MainWindowHandle == 0, "ffplay showed no window.");

            await using var engine = new FfmpegStreamEngine(ffmpeg, new RelayOutputTransport(),
                options: new EngineOptions { ConnectTimeout = TimeSpan.FromSeconds(20) });
            var black = false;
            engine.TelemetryUpdated += (_, t) => black |= t.CaptureLooksBlack;
            engine.StateChanged += (_, e) => output.WriteLine($"state {e.Previous} -> {e.Current} {e.Error}");
            engine.DiagnosticLine += (_, l) => output.WriteLine("ffmpeg: " + l);

            await engine.StartAsync(new StreamSession(
                new WindowSource(player.MainWindowHandle, "black", "ffplay.exe", CaptureCursor: false),
                [new TestToneSource()],
                new EncoderSettings("libx264", 640, 360, 30, 1000, Preset: "ultrafast"),
                new Destination("None", new Uri("rtmp://invalid/"), new Secret("")),
                NetworkSelection.SystemDefault,
                ReconnectPolicy.Disabled)
            {
                Broadcast = false,
                Recording = new RecordingSpec(folder),
            });

            // Live, then well past the 3 s a picture must stay black before it counts.
            deadline = DateTime.UtcNow.AddSeconds(25);
            while (engine.State != StreamState.Live && DateTime.UtcNow < deadline)
            {
                await Task.Delay(250);
            }

            Assert.Equal(StreamState.Live, engine.State);
            deadline = DateTime.UtcNow.AddSeconds(8);
            while (!black && DateTime.UtcNow < deadline)
            {
                await Task.Delay(250);
            }

            await engine.StopAsync();
            Assert.Equal(expected, black);
        }
        finally
        {
            player.Kill();
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
    }
}

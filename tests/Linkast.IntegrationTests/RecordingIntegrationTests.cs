// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Linkast.Core;
using Linkast.Engine.FFmpeg;
using Linkast.Networking;
using Xunit.Abstractions;

namespace Linkast.IntegrationTests;

/// <summary>
/// Recording through the real FFmpeg pipeline: while streaming to MediaMTX (one encode, two outputs),
/// and on its own without any network. The files must play back and remux to MP4.
/// </summary>
[Collection("ingest")]
public sealed partial class RecordingIntegrationTests(ITestOutputHelper output) : IAsyncLifetime
{
    private FfmpegInstallation? _ffmpeg;
    private string _folder = string.Empty;

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

        // A space and brackets in the folder name check the tee escaping on a real path.
        _folder = Path.Combine(Path.GetTempPath(), "Linkast rec [" + Guid.NewGuid().ToString("N")[..6] + "]");
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }

        return Task.CompletedTask;
    }

    [SkippableFact]
    public async Task Records_while_streaming_and_on_its_own()
    {
        Skip.If(_ffmpeg is null, "ffmpeg not found.");
        Skip.IfNot(IngestReachable(), "MediaMTX not running.");

        var key = "rec-" + Guid.NewGuid().ToString("N")[..8];
        await using var engine = new FfmpegStreamEngine(_ffmpeg!, new RelayOutputTransport(),
            options: new EngineOptions { ConnectTimeout = TimeSpan.FromSeconds(20) });
        var saved = new List<string>();
        engine.RecordingSaved += (_, path) => saved.Add(path);
        engine.StateChanged += (_, e) => output.WriteLine($"state {e.Previous} -> {e.Current} {e.Error}");
        engine.DiagnosticLine += (_, l) => output.WriteLine("ffmpeg: " + l);

        var session = new StreamSession(
            new TestPatternSource(),
            [new TestToneSource()],
            new EncoderSettings("libx264", 640, 360, 30, 1500, Preset: "ultrafast"),
            new Destination("MediaMTX", new Uri("rtmp://127.0.0.1:1935/live"), new Secret(key)),
            NetworkSelection.SystemDefault,
            ReconnectPolicy.Default)
        {
            Recording = new RecordingSpec(_folder),
        };

        // 1. Streaming and recording.
        await engine.StartAsync(session);
        await WaitUntilAsync(() => engine.State == StreamState.Live, TimeSpan.FromSeconds(25));
        Assert.NotNull(engine.CurrentRecording);
        await Task.Delay(3000);
        Assert.True(await PlaysAsync($"rtmp://127.0.0.1:1935/live/{key}", 1), "The stream reaches the server while recording.");
        await engine.StopAsync();

        Assert.Single(saved);
        var both = await DurationAsync(saved[0]);
        output.WriteLine($"Stream + record: {saved[0]} {both.TotalSeconds:0.0} s");
        Assert.True(both.TotalSeconds > 3, "The recording covers the stream.");

        // 2. Recording only: no destination needed, nothing sent anywhere.
        await engine.StartAsync(session with
        {
            Broadcast = false,
            Destination = new Destination("None", new Uri("rtmp://invalid/"), new Secret("")),
        });
        await WaitUntilAsync(() => engine.State == StreamState.Live, TimeSpan.FromSeconds(25));
        await Task.Delay(2500);
        await engine.StopAsync();

        Assert.Equal(2, saved.Count);
        var only = await DurationAsync(saved[1]);
        output.WriteLine($"Record only: {saved[1]} {only.TotalSeconds:0.0} s");
        Assert.True(only.TotalSeconds > 2);

        // 3. Remux to MP4 without re-encoding; the MKV is replaced.
        var mp4 = await FfmpegRemuxer.RemuxToMp4Async(_ffmpeg!.Path, saved[1]);
        Assert.NotNull(mp4);
        Assert.False(File.Exists(saved[1]));
        var remuxed = await DurationAsync(mp4!);
        Assert.InRange(remuxed.TotalSeconds, only.TotalSeconds - 0.5, only.TotalSeconds + 0.5);
    }

    private async Task<TimeSpan> DurationAsync(string file)
    {
        // Decoding the whole file proves it plays; the header gives its length.
        var (exit, err) = await RunAsync("-hide_banner", "-v", "info", "-i", file, "-f", "null", "-");
        Assert.True(exit == 0, err);
        var m = DurationPattern().Match(err);
        Assert.True(m.Success, err);
        return TimeSpan.ParseExact(m.Groups[1].Value, @"hh\:mm\:ss\.ff", CultureInfo.InvariantCulture);
    }

    private async Task<bool> PlaysAsync(string url, int seconds)
    {
        var (exit, _) = await RunAsync("-hide_banner", "-v", "error", "-rw_timeout", "8000000", "-i", url, "-t", seconds.ToString(CultureInfo.InvariantCulture), "-f", "null", "-");
        return exit == 0;
    }

    private async Task<(int Exit, string Error)> RunAsync(params string[] args)
    {
        var psi = new ProcessStartInfo(_ffmpeg!.Path) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        _ = p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await err);
    }

    [GeneratedRegex(@"Duration: (\d\d:\d\d:\d\d\.\d\d)")]
    private static partial Regex DurationPattern();

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

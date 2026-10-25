// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using Linkast.Core;
using Linkast.Engine.FFmpeg;
using Linkast.Networking;
using Linkast.Platform.Windows.Audio;
using Linkast.Platform.Windows.Capture;
using Xunit.Abstractions;

namespace Linkast.IntegrationTests;

/// <summary>
/// Long-running stability test for the Milestone 1 exit criterion. Opt-in only:
/// set LINKAST_SOAK_MINUTES (for example 30) and run with --filter "FullyQualifiedName~Soak".
/// Optional: LINKAST_SOAK_URL (default rtmp://127.0.0.1:1935/live) and LINKAST_SOAK_KEY.
/// </summary>
[Collection("ingest")]
public sealed class SoakTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task Soak_display_capture_with_system_audio()
    {
        var minutesText = Environment.GetEnvironmentVariable("LINKAST_SOAK_MINUTES");
        Skip.If(string.IsNullOrEmpty(minutesText), "Set LINKAST_SOAK_MINUTES to run the soak test.");
        var minutes = double.Parse(minutesText, CultureInfo.InvariantCulture);
        var url = Environment.GetEnvironmentVariable("LINKAST_SOAK_URL") ?? "rtmp://127.0.0.1:1935/live";
        var key = Environment.GetEnvironmentVariable("LINKAST_SOAK_KEY") ?? "soak-" + Guid.NewGuid().ToString("N")[..8];

        var ffmpeg = await FfmpegLocator.LocateAsync();
        var probes = await new EncoderCatalog(ffmpeg).ProbeAsync();
        var encoder = probes.FirstOrDefault(p => p.Works && p.Encoder.Kind == EncoderKind.Hardware)?.Encoder.Id ?? "libx264";
        var monitor = VideoSourceCatalog.ListMonitors().First(m => m.IsPrimary);

        var session = new StreamSession(
            monitor.ToSource(CaptureApi.WindowsGraphicsCapture),
            [new SystemAudioSource(null, "Default output")],
            EncoderSettings.Default1080p60(encoder),
            new Destination("soak", new Uri(url), new Secret(key)),
            NetworkSelection.SystemDefault,
            ReconnectPolicy.Default);

        await using var engine = new FfmpegStreamEngine(ffmpeg, new RelayOutputTransport(), new WasapiAudioInputProvider());
        var states = new List<string>();
        StreamTelemetry? latest = null;
        var fpsSamples = new List<double>();
        var kbpsSamples = new List<double>();
        engine.StateChanged += (_, e) => { lock (states) { states.Add($"{DateTime.Now:HH:mm:ss} {e.Current} {e.Error}"); } };
        engine.TelemetryUpdated += (_, t) =>
        {
            latest = t;
            if (t.State == StreamState.Live && t.Encoder is { } enc && t.Network is { } net)
            {
                lock (fpsSamples)
                {
                    fpsSamples.Add(enc.Fps);
                    kbpsSamples.Add(net.OutgoingKbps);
                }
            }
        };

        output.WriteLine($"Soak: {minutes} min, {encoder}, {monitor}, {ffmpeg.Describe()}");
        var cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
        await engine.StartAsync(session);

        var end = DateTime.UtcNow.AddMinutes(minutes);
        while (DateTime.UtcNow < end)
        {
            await Task.Delay(TimeSpan.FromSeconds(60));
            var t = latest;
            output.WriteLine($"{DateTime.Now:HH:mm:ss} state={t?.State} up={t?.Uptime:hh\\:mm\\:ss} fps={t?.Encoder?.Fps:0.0} " +
                $"speed={t?.Encoder?.Speed:0.00} out={t?.Encoder?.OutTime:hh\\:mm\\:ss} kbps={t?.Network?.OutgoingKbps:0} " +
                $"sent={t?.Network?.BytesSent / 1_000_000.0:0.0}MB drop={t?.Encoder?.DroppedFrames} dup={t?.Encoder?.DuplicatedFrames} reconnects={t?.ReconnectCount}");
        }

        var final = latest!;
        await engine.StopAsync();

        lock (fpsSamples)
        {
            output.WriteLine($"FPS avg {fpsSamples.Average():0.00} min {fpsSamples.Min():0.00}; upload avg {kbpsSamples.Average():0} kbps; samples {fpsSamples.Count}");
        }

        output.WriteLine($"Final: uptime {final.Uptime}, out_time {final.Encoder?.OutTime}, reconnects {final.ReconnectCount}, last error {final.LastError}");
        output.WriteLine($"Test-process CPU: {(Process.GetCurrentProcess().TotalProcessorTime - cpuStart).TotalSeconds:0}s");
        lock (states)
        {
            foreach (var s in states)
            {
                output.WriteLine("state: " + s);
            }
        }

        Assert.Equal(StreamState.Live, final.State);
        Assert.Equal(0, final.ReconnectCount);
        Assert.True((final.Encoder!.OutTime - final.Uptime).Duration() < TimeSpan.FromSeconds(5), "Output time drifted from wall-clock time.");
        Assert.Equal(StreamState.Stopped, engine.State);
    }
}

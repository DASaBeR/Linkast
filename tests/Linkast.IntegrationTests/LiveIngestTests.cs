// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Linkast.Core;
using Linkast.Engine.FFmpeg;
using Linkast.Networking;
using Xunit.Abstractions;

namespace Linkast.IntegrationTests;

/// <summary>
/// Streams a 1080p60 test pattern and tone to a real ingest (for example Restream or Kick) on this PC's
/// best encoder. Opt-in only: set LINKAST_LIVE_URL and LINKAST_LIVE_KEY (never commit the key);
/// LINKAST_LIVE_SECONDS sets the length (default 120). The stream is public on that channel.
/// </summary>
public sealed class LiveIngestTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task Streams_1080p60_to_a_real_ingest()
    {
        var url = Environment.GetEnvironmentVariable("LINKAST_LIVE_URL");
        var key = Environment.GetEnvironmentVariable("LINKAST_LIVE_KEY");
        Skip.If(string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key), "Set LINKAST_LIVE_URL and LINKAST_LIVE_KEY to run the live ingest test.");
        var seconds = int.Parse(Environment.GetEnvironmentVariable("LINKAST_LIVE_SECONDS") ?? "120", CultureInfo.InvariantCulture);

        var ffmpeg = await FfmpegLocator.LocateAsync();
        var probes = await new EncoderCatalog(ffmpeg).ProbeAsync();
        var encoder = probes.FirstOrDefault(p => p.Works && p.Encoder.Kind == EncoderKind.Hardware)?.Encoder.Id ?? "libx264";
        var settings = EncoderSettings.Default1080p60(encoder);

        await using var engine = new FfmpegStreamEngine(ffmpeg, new RelayOutputTransport(), audioProvider: null,
            options: new EngineOptions { ConnectTimeout = TimeSpan.FromSeconds(20) });
        StreamTelemetry? latest = null;
        engine.StateChanged += (_, e) => output.WriteLine($"{DateTime.Now:HH:mm:ss} state {e.Previous} -> {e.Current} {e.Error}");
        engine.TelemetryUpdated += (_, t) => latest = t;

        var connectStart = DateTime.UtcNow;
        var connected = DateTime.MinValue;
        engine.StateChanged += (_, e) =>
        {
            if (e.Current == StreamState.Live && connected == DateTime.MinValue)
            {
                connected = DateTime.UtcNow;
            }
        };

        await engine.StartAsync(new StreamSession(
            new TestPatternSource(),
            [new TestToneSource()],
            settings,
            new Destination("Live ingest", new Uri(url!), new Secret(key!)),
            NetworkSelection.SystemDefault,
            ReconnectPolicy.Default));

        var kbps = new List<double>();
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            if (latest is { State: StreamState.Live, Network: { } net })
            {
                kbps.Add(net.OutgoingKbps);
            }
        }

        var final = latest!;
        Assert.Equal(StreamState.Live, engine.State);
        await engine.StopAsync();
        Assert.Equal(StreamState.Stopped, engine.State);

        output.WriteLine($"Live {settings.Width}x{settings.Height}@{settings.Fps} {encoder}: connect {(connected - connectStart).TotalSeconds:0.0} s, " +
            $"average {final.Encoder?.Fps:0.0} fps, speed {final.Encoder?.Speed:0.00}x, upload avg {(kbps.Count > 0 ? kbps.Average() : 0):0} kbps, " +
            $"dropped {final.Encoder?.DroppedFrames}, reconnects {final.ReconnectCount}");
        Assert.Equal(0, final.ReconnectCount);
        Assert.True(final.Encoder?.Speed >= 0.98, $"Encoder fell behind real time: {final.Encoder?.Speed}x.");
    }
}

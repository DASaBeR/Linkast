// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Sockets;
using Linkast.Core;
using Linkast.Engine.FFmpeg;
using Linkast.Networking;
using Xunit.Abstractions;

namespace Linkast.IntegrationTests;

/// <summary>
/// The auto quality upload test appends "?bandwidthtest=true" to the stream key (Twitch's test mode).
/// The key must pass validation and the relay unchanged, and the relay must measure what is sent.
/// </summary>
[Collection("ingest")]
public sealed class UploadTestIntegrationTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task A_bandwidth_test_key_streams_and_is_measured()
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

        Skip.IfNot(IngestReachable(), "MediaMTX not running.");
        await using var engine = new FfmpegStreamEngine(ffmpeg, new RelayOutputTransport());
        var kbps = new List<double>();
        engine.TelemetryUpdated += (_, t) =>
        {
            if (t.State == StreamState.Live && t.Network is { OutgoingKbps: > 0 } n)
            {
                lock (kbps)
                {
                    kbps.Add(n.OutgoingKbps);
                }
            }
        };
        engine.StateChanged += (_, e) => output.WriteLine($"state {e.Previous} -> {e.Current} {e.Error}");

        var key = "bw-" + Guid.NewGuid().ToString("N")[..8] + "?bandwidthtest=true";
        await engine.StartAsync(new StreamSession(
            new TestPatternSource(), [new TestToneSource()],
            new EncoderSettings("libx264", 1280, 720, 30, 3000, Preset: "ultrafast"),
            new Destination("MediaMTX", new Uri("rtmp://127.0.0.1:1935/live"), new Secret(key)),
            NetworkSelection.SystemDefault, ReconnectPolicy.Disabled));

        for (var i = 0; i < 100 && kbps.Count < 5; i++)
        {
            await Task.Delay(200);
        }

        await engine.StopAsync();
        output.WriteLine("kbps: " + string.Join(", ", kbps.Select(k => k.ToString("0", System.Globalization.CultureInfo.InvariantCulture))));
        Assert.True(kbps.Count >= 5, "The test stream went live and was measured.");
        Assert.InRange(kbps.Skip(2).Average(), 1000, 6000);
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
}

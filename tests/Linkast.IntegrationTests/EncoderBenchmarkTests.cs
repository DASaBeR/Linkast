// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core;
using Linkast.Engine.FFmpeg;
using Xunit.Abstractions;

namespace Linkast.IntegrationTests;

/// <summary>The encoder speed test of the auto quality setup, on this PC's real encoders.</summary>
public sealed class EncoderBenchmarkTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task Measures_every_working_encoder()
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

        var working = (await new EncoderCatalog(ffmpeg).ProbeAsync()).Where(r => r.Works).Select(r => r.Encoder).ToList();
        Skip.If(working.Count == 0, "No working encoder.");
        foreach (var encoder in working)
        {
            var speed = await EncoderBenchmark.RunAsync(ffmpeg, encoder);
            output.WriteLine($"{encoder.Id}: {speed?.FramesPerSecond:0} fps at 1080p");
            Assert.NotNull(speed);
            Assert.InRange(speed.FramesPerSecond, 5, 5000);
        }
    }
}

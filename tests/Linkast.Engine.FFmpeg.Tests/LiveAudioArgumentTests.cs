// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core;
using Linkast.Core.Audio;

namespace Linkast.Engine.FFmpeg.Tests;

public class LiveAudioArgumentTests
{
    private static string Graph(params AudioJobInput[] audio)
    {
        var job = new FfmpegJob(new DisplaySource(1), audio, EncoderSettings.Default1080p60("h264_nvenc"), "rtmp://127.0.0.1:50000/live/k", null)
        {
            CommandPort = 5555,
        };
        var args = FfmpegArgumentBuilder.Build(job);
        return args[args.ToList().IndexOf("-filter_complex") + 1];
    }

    [Fact]
    public void Named_inputs_get_named_volume_filters_that_can_change_live()
    {
        var fmt = new PcmFormat(48000, 2, PcmSampleFormat.F32);
        var graph = Graph(
            new PipeAudioJobInput(@"\\.\pipe\linkast-sys", fmt, 1) { Name = "sys" },
            new PipeAudioJobInput(@"\\.\pipe\linkast-mic", fmt, 0) { Name = "mic", NoiseSuppression = false });

        Assert.Contains("volume@sys=1[a0]", graph, StringComparison.Ordinal);
        Assert.Contains("afftdn@mic=nr=0.01:nf=-40,volume@mic=0[a1]", graph, StringComparison.Ordinal);
        Assert.DoesNotContain("afftdn@sys", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void Noise_removal_on_uses_a_real_reduction()
    {
        var fmt = new PcmFormat(48000, 2, PcmSampleFormat.F32);
        var graph = Graph(new PipeAudioJobInput(@"\\.\pipe\linkast-mic", fmt, 1) { Name = "mic", NoiseSuppression = true });
        Assert.Contains("afftdn@mic=nr=12:nf=-40,volume@mic=1[a0]", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void Command_channel_is_bound_even_without_layers()
    {
        var fmt = new PcmFormat(48000, 2, PcmSampleFormat.F32);
        var graph = Graph(new PipeAudioJobInput(@"\\.\pipe\linkast-mic", fmt, 1) { Name = "mic" });
        Assert.Contains(@"zmq=bind_address=tcp\\://127.0.0.1\\:5555", graph, StringComparison.Ordinal);
    }
}

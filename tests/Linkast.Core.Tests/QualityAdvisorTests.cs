// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core.Tests;

public class QualityAdvisorTests
{
    private static readonly EncoderSpeed FastNvenc = new("h264_nvenc", true, 400);
    private static readonly EncoderSpeed X264 = new("libx264", false, 90);

    [Fact]
    public void Fast_upload_on_twitch_gives_1080p60_at_the_twitch_limit()
    {
        var r = QualityAdvisor.Recommend(StreamPlatform.Twitch, 40_000, [X264, FastNvenc])!;
        Assert.Equal(("h264_nvenc", 1920, 1080, 60, 6000), (r.EncoderId, r.Width, r.Height, r.Fps, r.VideoBitrateKbps));
    }

    [Fact]
    public void YouTube_allows_more_bitrate()
    {
        var r = QualityAdvisor.Recommend(StreamPlatform.YouTube, 40_000, [FastNvenc])!;
        Assert.Equal(12000, r.VideoBitrateKbps);
    }

    [Theory]
    [InlineData(6500, 1280, 720, 60, 4500)]   // 4875 kbps budget
    [InlineData(4000, 1280, 720, 30, 3000)]   // 3000 budget
    [InlineData(2000, 854, 480, 30, 1500)]    // 1500 budget
    [InlineData(1200, 854, 480, 30, 900)]     // the stream must fit the upload, even below the ideal
    public void Slower_uploads_step_down(int upload, int w, int h, int fps, int kbps)
    {
        var r = QualityAdvisor.Recommend(StreamPlatform.Twitch, upload, [FastNvenc])!;
        Assert.Equal((w, h, fps, kbps), (r.Width, r.Height, r.Fps, r.VideoBitrateKbps));
    }

    [Fact]
    public void A_slow_processor_lowers_the_frame_rate_and_says_why()
    {
        // x264 shares the processor with the game: 100 fps of 1080p is only enough for 1080p30.
        var r = QualityAdvisor.Recommend(StreamPlatform.Twitch, 40_000, [new EncoderSpeed("libx264", false, 100)])!;
        Assert.Equal((1920, 1080, 30), (r.Width, r.Height, r.Fps));
        Assert.Contains("can't keep up", r.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_much_faster_software_encoder_still_loses_to_a_capable_hardware_one()
    {
        var r = QualityAdvisor.Recommend(StreamPlatform.Twitch, 40_000, [new EncoderSpeed("libx264", false, 900), new EncoderSpeed("h264_amf", true, 80)])!;
        Assert.Equal("h264_amf", r.EncoderId);
    }

    [Fact]
    public void A_hardware_encoder_too_slow_for_real_time_is_not_preferred()
    {
        var r = QualityAdvisor.Recommend(StreamPlatform.Twitch, 40_000, [new EncoderSpeed("h264_mf", true, 40), X264])!;
        Assert.Equal("libx264", r.EncoderId);
    }

    [Fact]
    public void Nvenc_beats_a_faster_media_foundation_encoder_for_picture_quality()
    {
        var r = QualityAdvisor.Recommend(StreamPlatform.Twitch, 40_000, [new EncoderSpeed("h264_mf", true, 577), new EncoderSpeed("h264_nvenc", true, 235)])!;
        Assert.Equal("h264_nvenc", r.EncoderId);
    }

    [Fact]
    public void Nothing_to_go_on_gives_no_recommendation()
    {
        Assert.Null(QualityAdvisor.Recommend(StreamPlatform.Twitch, 10_000, []));
        Assert.Null(QualityAdvisor.Recommend(StreamPlatform.Twitch, 0, [FastNvenc]));
    }
}

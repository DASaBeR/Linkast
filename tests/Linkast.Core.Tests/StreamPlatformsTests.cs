// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core;

namespace Linkast.Core.Tests;

public class StreamPlatformsTests
{
    [Theory]
    [InlineData("rtmps://fa723fc1b171.global-contribute.live-video.net", "rtmps://fa723fc1b171.global-contribute.live-video.net/app")]
    [InlineData("  rtmps://fa723fc1b171.global-contribute.live-video.net/  ", "rtmps://fa723fc1b171.global-contribute.live-video.net/app")]
    [InlineData("rtmps://fa723fc1b171.global-contribute.live-video.net:443", "rtmps://fa723fc1b171.global-contribute.live-video.net:443/app")]
    [InlineData("rtmps://fa723fc1b171.global-contribute.live-video.net/app", "rtmps://fa723fc1b171.global-contribute.live-video.net/app")]
    [InlineData("rtmp://live.twitch.tv", "rtmp://live.twitch.tv")]
    [InlineData("not a url", "not a url")]
    public void Normalize_adds_the_app_path_only_where_the_service_leaves_it_out(string input, string expected) =>
        Assert.Equal(expected, StreamPlatforms.Normalize(input));

    [Fact]
    public void Kick_is_detected_from_the_server_without_the_app_path() =>
        Assert.Equal(StreamPlatform.Kick, StreamPlatforms.Detect("rtmps://fa723fc1b171.global-contribute.live-video.net"));

    [Theory]
    [InlineData("rtmps://abc123def456.global-contribute.live-video.net/app")]
    [InlineData("rtmps://abc123def456.global-contribute.live-video.net")]
    public void Any_kick_ingest_host_is_detected_as_Kick(string url) =>
        Assert.Equal(StreamPlatform.Kick, StreamPlatforms.Detect(url));

    [Fact]
    public void Only_custom_and_kick_have_an_editable_server()
    {
        Assert.Equal(
            [StreamPlatform.Custom, StreamPlatform.Kick],
            StreamPlatforms.All.Where(p => p.ServerEditable).Select(p => p.Platform).Order());
    }
}

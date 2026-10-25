// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core.Settings;

namespace Linkast.Core.Tests;

public class StreamEssentialsTests
{
    [Theory]
    [InlineData("rtmp://live.twitch.tv/app", StreamPlatform.Twitch)]
    [InlineData("rtmp://live.twitch.tv/app/", StreamPlatform.Twitch)]
    [InlineData("RTMPS://A.RTMPS.YOUTUBE.COM/LIVE2", StreamPlatform.YouTube)]
    [InlineData("rtmps://my-own-server.example/live", StreamPlatform.Custom)]
    [InlineData("", StreamPlatform.Custom)]
    public void Platform_is_detected_from_the_server(string url, StreamPlatform expected) =>
        Assert.Equal(expected, StreamPlatforms.Detect(url));

    [Fact]
    public void Every_preset_has_a_secure_or_known_server_and_a_key_page()
    {
        foreach (var p in StreamPlatforms.All.Where(p => p.Platform != StreamPlatform.Custom))
        {
            Assert.True(Uri.TryCreate(p.Server, UriKind.Absolute, out var server) && server.Scheme is "rtmp" or "rtmps", p.Name);
            Assert.StartsWith("https://", p.KeyPage, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Platform_and_noise_removal_round_trip_and_default_off()
    {
        var s = new LinkastSettings { Stream = new StreamSettings { Platform = StreamPlatform.Kick, MicrophoneNoiseSuppression = true } };
        var (back, _) = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(s));
        Assert.Equal(StreamPlatform.Kick, back.Stream.Platform);
        Assert.True(back.Stream.MicrophoneNoiseSuppression);

        var (old, _) = SettingsSerializer.Deserialize("""{"schemaVersion":2,"stream":{"serverUrl":"rtmp://live.twitch.tv/app"}}""");
        Assert.Equal(StreamPlatform.Custom, old.Stream.Platform);
        Assert.False(old.Stream.MicrophoneNoiseSuppression);
    }
}

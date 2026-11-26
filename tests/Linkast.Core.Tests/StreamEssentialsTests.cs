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
        Assert.Null(old.Stream.MicrophoneNoiseRemoval);
    }

    [Fact]
    public void Noise_removal_level_round_trips_and_older_settings_keep_their_on_off_choice()
    {
        var s = new LinkastSettings { Stream = new StreamSettings { MicrophoneNoiseSuppression = true, MicrophoneNoiseRemoval = Audio.NoiseRemoval.Strong } };
        var (back, _) = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(s));
        Assert.Equal(Audio.NoiseRemoval.Strong, back.Stream.MicrophoneNoiseRemoval);

        // Saved by 1.3.2: only the on/off switch. The app reads it as Standard.
        var (old, _) = SettingsSerializer.Deserialize("""{"schemaVersion":2,"stream":{"microphoneNoiseSuppression":true}}""");
        Assert.True(old.Stream.MicrophoneNoiseSuppression);
        Assert.Null(old.Stream.MicrophoneNoiseRemoval);
    }

    [Theory]
    [InlineData(false, Audio.NoiseRemoval.Off, Audio.NoiseRemoval.Off)]
    [InlineData(true, Audio.NoiseRemoval.Off, Audio.NoiseRemoval.Standard)]
    [InlineData(true, Audio.NoiseRemoval.Strong, Audio.NoiseRemoval.Strong)]
    [InlineData(false, Audio.NoiseRemoval.Strong, Audio.NoiseRemoval.Strong)]
    public void A_microphone_asks_for_the_level_or_else_the_switch(bool suppression, Audio.NoiseRemoval level, Audio.NoiseRemoval expected) =>
        Assert.Equal(expected, new MicrophoneSource(null, "Mic") { NoiseSuppression = suppression, NoiseRemoval = level }.EffectiveNoiseRemoval);
}

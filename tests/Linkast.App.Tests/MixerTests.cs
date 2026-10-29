// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.App.ViewModels;

namespace Linkast.App.Tests;

/// <summary>The mixer's meters show what the stream gets, after the fader and mutes.</summary>
public class MixerTests
{
    [Fact]
    public void A_muted_channel_meters_silence() =>
        Assert.Equal(0, MixerChannelViewModel.StreamLevel(-6, 0));

    [Fact]
    public void Full_volume_meters_the_sound_as_captured() =>
        Assert.Equal(0.9, MixerChannelViewModel.StreamLevel(-6, 1), 3);

    [Fact]
    public void Half_volume_lowers_the_meter_by_6_dB() =>
        Assert.Equal(0.8, MixerChannelViewModel.StreamLevel(-6, 0.5), 2);

    [Fact]
    public void The_meter_stays_between_0_and_1()
    {
        Assert.Equal(1, MixerChannelViewModel.StreamLevel(0, 2));
        Assert.Equal(0, MixerChannelViewModel.StreamLevel(-60, 0.5));
    }

    [Fact]
    public void Muting_or_the_scene_muting_makes_the_stream_gain_0()
    {
        var channel = new MixerChannelViewModel("sys", MixerChannelKind.Desktop, "Desktop audio") { Volume = 150 };
        Assert.Equal(1.5, channel.Gain);
        channel.Muted = true;
        Assert.Equal(0, channel.Gain);
        channel.Muted = false;
        channel.SceneMuted = true;
        Assert.Equal(0, channel.Gain);
    }

    [Fact]
    public void A_test_sound_is_reported_at_its_level_in_the_stream() =>
        Assert.StartsWith("-12 dB in the stream.", MainViewModel.DescribeTestLevel(-12, 0.5), StringComparison.Ordinal);

    [Fact]
    public void A_quiet_fader_is_not_mistaken_for_a_missing_sound() =>
        Assert.Contains("Quiet", MainViewModel.DescribeTestLevel(-50, 0.01), StringComparison.Ordinal);

    [Fact]
    public void A_sound_muted_in_windows_is_reported_as_not_heard() =>
        Assert.StartsWith("Not heard", MainViewModel.DescribeTestLevel(-60, 1), StringComparison.Ordinal);
}

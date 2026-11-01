// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Linkast.Platform.Windows.Audio;

namespace Linkast.App.Tests;

public class UnifiedAudioMixerTests
{
    [Fact]
    public async Task UnifiedAudioMixer_pads_silence_on_empty_timeline_and_stops_cleanly()
    {
        var hub = new AudioCaptureHub();
        using var memoryStream = new MemoryStream();

        var mixer = new UnifiedAudioMixer(hub, memoryStream);

        mixer.Start();
        Assert.True(mixer.IsRunning);

        // Wait ~60 ms for timer ticks
        await Task.Delay(60);

        await mixer.DisposeAsync();
        Assert.False(mixer.IsRunning);

        Assert.True(mixer.TotalBytesWritten > 0);
        Assert.Equal(0, mixer.TotalBytesWritten % 4); // Block aligned
    }

    [Fact]
    public async Task UnifiedAudioMixer_controls_channel_gain()
    {
        var hub = new AudioCaptureHub();
        using var memoryStream = new MemoryStream();

        var mixer = new UnifiedAudioMixer(hub, memoryStream);

        // Unknown channel returns false
        bool changedUnknown = await mixer.SetGainAsync("unknown", 0.5);
        Assert.False(changedUnknown);

        await mixer.DisposeAsync();
    }
}

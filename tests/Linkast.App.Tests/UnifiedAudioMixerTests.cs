// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Linkast.Core;
using Linkast.Core.Audio;
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

        // Wait for worker loop ticks with deterministic polling (avoids thread pool delay flakiness on CI)
        var deadline = Stopwatch.StartNew();
        while (mixer.TotalBytesWritten == 0 && deadline.Elapsed < TimeSpan.FromSeconds(3))
        {
            await Task.Delay(10);
        }

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

        mixer.AddTestChannel("mic", 1.0);
        bool changedKnown = await mixer.SetGainAsync("mic", 8.0);
        Assert.True(changedKnown);

        await mixer.DisposeAsync();
    }

    [Fact]
    public async Task UnifiedAudioMixer_toggles_noise_suppression()
    {
        var hub = new AudioCaptureHub();
        using var memoryStream = new MemoryStream();

        var mixer = new UnifiedAudioMixer(hub, memoryStream);
        Assert.False(mixer.NoiseSuppressionEnabled);

        bool enabled = await mixer.SetNoiseSuppressionAsync(true);
        Assert.True(enabled);
        Assert.True(mixer.NoiseSuppressionEnabled);

        bool disabled = await mixer.SetNoiseSuppressionAsync(false);
        Assert.True(disabled);
        Assert.False(mixer.NoiseSuppressionEnabled);

        await mixer.DisposeAsync();
    }

    [Fact]
    public async Task UnifiedAudioMixer_normalizes_mono_and_s16_formats_and_resamples()
    {
        var hub = new AudioCaptureHub();
        using var memoryStream = new MemoryStream();

        var mixer = new UnifiedAudioMixer(hub, memoryStream);

        // Add 44.1 kHz Mono s16 channel
        var mono44k = new PcmFormat(44100, 1, PcmSampleFormat.S16);
        mixer.AddTestChannel("mono44k", initialGain: 1.0, format: mono44k);

        // Feed 441 samples of sine wave (10ms of 44.1kHz audio)
        short[] shortBuffer = new short[441];
        for (int i = 0; i < shortBuffer.Length; i++)
        {
            shortBuffer[i] = (short)(Math.Sin(2 * Math.PI * 440 * i / 44100.0) * 16000);
        }
        byte[] bytes = new byte[shortBuffer.Length * sizeof(short)];
        Buffer.BlockCopy(shortBuffer, 0, bytes, 0, bytes.Length);

        mixer.EnqueueTestChannelData("mono44k", bytes);

        // Process a slice directly to verify format normalization & resampling deterministically
        var written = await mixer.MixOneSliceAsync();
        Assert.True(written > 0);

        await mixer.DisposeAsync();

        // Total bytes written must be 48 kHz stereo 16-bit PCM (multiple of block align 4 bytes)
        Assert.True(mixer.TotalBytesWritten > 0);
        Assert.Equal(0, mixer.TotalBytesWritten % 4);
    }
}

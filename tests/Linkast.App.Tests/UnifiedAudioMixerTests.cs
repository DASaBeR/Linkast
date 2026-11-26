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
    /// <summary>Float32 stereo 48 kHz of a constant, non-silent level.</summary>
    private static byte[] Sound(int milliseconds, float level = 0.25f)
    {
        var samples = new float[48000 * milliseconds / 1000 * 2];
        Array.Fill(samples, level);
        return MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
    }

    [Fact]
    public async Task UnifiedAudioMixer_catches_up_with_waiting_sound_when_slightly_late()
    {
        var hub = new AudioCaptureHub();
        using var memoryStream = new MemoryStream();

        // 200 ms late (a late timer tick), with 150 ms of sound waiting: no gap to fill, the sound catches up.
        var mixer = new UnifiedAudioMixer(hub, memoryStream, Stopwatch.GetTimestamp() - Stopwatch.Frequency / 5);
        mixer.AddTestChannel("game", 1.0);
        mixer.EnqueueTestChannelData("game", Sound(150));

        var before = mixer.ClockLag;
        await mixer.CatchUpAsync(new float[960], new byte[1920], new byte[192000], CancellationToken.None);

        Assert.True(mixer.TotalBytesWritten >= 1920 * 10);
        Assert.True(mixer.TotalBytesWritten <= 150 * 192, "below the resync threshold only real sound is written");
        Assert.True(mixer.ClockLag < before);
        Assert.Equal(0, mixer.Resyncs);
        Assert.Equal(0, mixer.TotalBytesWritten % 4);

        await mixer.DisposeAsync();
    }

    [Fact]
    public async Task UnifiedAudioMixer_fills_a_blocked_output_gap_with_silence_and_then_plays_the_waiting_sound()
    {
        var hub = new AudioCaptureHub();
        using var memoryStream = new MemoryStream();

        // The output was blocked for a second: 1 s late, with the newest 150 ms of sound waiting in the channel.
        // Before, the gap was never filled and the sound stayed a second late for the rest of the stream.
        var mixer = new UnifiedAudioMixer(hub, memoryStream, Stopwatch.GetTimestamp() - Stopwatch.Frequency);
        mixer.AddTestChannel("game", 1.0);
        mixer.EnqueueTestChannelData("game", Sound(150));

        await mixer.CatchUpAsync(new float[960], new byte[1920], new byte[192000], CancellationToken.None);

        Assert.Equal(1, mixer.Resyncs);
        Assert.InRange(mixer.ClockLag.TotalMilliseconds, -30, 30);

        // Silence first, then the waiting sound at the end, where it belongs on the timeline.
        var written = memoryStream.ToArray();
        Assert.Equal(0, written.Length % 4);
        var samples = MemoryMarshal.Cast<byte, short>(written);
        Assert.Equal(0, samples[0]);
        Assert.True(samples[^2] > 8000, "the last thing written is the waiting sound");
        Assert.Contains("silence after 1 stall(s)", mixer.Describe(), StringComparison.Ordinal);

        await mixer.DisposeAsync();
    }

    [Fact]
    public async Task UnifiedAudioMixer_keeps_to_the_picture_when_the_video_lost_time()
    {
        var hub = new AudioCaptureHub();
        using var memoryStream = new MemoryStream();

        // A second of wall time has passed, but the picture lost all of it: the sound is on time, not late.
        var mixer = new UnifiedAudioMixer(hub, memoryStream, Stopwatch.GetTimestamp() - Stopwatch.Frequency,
            videoLostTime: () => TimeSpan.FromSeconds(1));
        mixer.AddTestChannel("game", 1.0);
        mixer.EnqueueTestChannelData("game", Sound(200));

        Assert.InRange(mixer.ClockLag.TotalMilliseconds, -20, 50);
        await mixer.CatchUpAsync(new float[960], new byte[1920], new byte[192000], CancellationToken.None);
        Assert.Equal(0, mixer.Resyncs);
        Assert.True(mixer.TotalBytesWritten <= 1920 * 5, "nothing to catch up: the picture is not ahead");

        await mixer.DisposeAsync();
    }

    [Fact]
    public async Task UnifiedAudioMixer_drops_sound_that_is_ahead_of_the_picture()
    {
        var hub = new AudioCaptureHub();
        using var memoryStream = new MemoryStream();

        // The picture lost more than the wall time since the start: the sound would play early.
        var mixer = new UnifiedAudioMixer(hub, memoryStream, Stopwatch.GetTimestamp() - Stopwatch.Frequency / 10,
            videoLostTime: () => TimeSpan.FromMilliseconds(400));
        mixer.AddTestChannel("game", 1.0);
        mixer.EnqueueTestChannelData("game", Sound(100));

        Assert.Equal(0, await mixer.MixOneSliceAsync());
        Assert.Equal(0, mixer.TotalBytesWritten);
        Assert.Contains("dropped while ahead", mixer.Describe(), StringComparison.Ordinal);

        await mixer.DisposeAsync();
    }

    [Fact]
    public async Task UnifiedAudioMixer_cleans_the_microphone_before_its_fader()
    {
        var hub = new AudioCaptureHub();
        using var memoryStream = new MemoryStream();
        // Started 10 s ago, so mixing two seconds at once is never "ahead of the picture".
        var mixer = new UnifiedAudioMixer(hub, memoryStream, Stopwatch.GetTimestamp() - 10 * Stopwatch.Frequency);
        mixer.AddTestChannel(AudioChannels.Microphone, 5.6);
        Assert.True(await mixer.SetNoiseRemovalAsync(NoiseRemoval.Strong));
        Assert.Equal(NoiseRemoval.Strong, mixer.NoiseRemoval);

        // Two seconds of steady hiss, mixed slice by slice.
        var random = new Random(2);
        var hiss = new float[48000 * 2 * 2];
        for (var i = 0; i < hiss.Length; i += 2)
        {
            hiss[i] = hiss[i + 1] = (float)(random.NextDouble() * 2 - 1) * 0.01f;
        }

        var slices = 0;
        for (var at = 0; at < hiss.Length; at += 960)
        {
            mixer.EnqueueTestChannelData(AudioChannels.Microphone, MemoryMarshal.AsBytes(hiss.AsSpan(at, 960)));
            slices += await mixer.MixOneSliceAsync() > 0 ? 1 : 0;
        }

        Assert.True(slices > 150);
        var last = MemoryMarshal.Cast<byte, short>(memoryStream.ToArray())[^1920..];
        var rms = Math.Sqrt(last.ToArray().Average(s => (double)s * s)) / 32768;
        Assert.True(rms < 0.01 * 5.6 / 10, $"the hiss at 560 % should be at least 20 dB quieter; it is {20 * Math.Log10(rms / (0.01 * 5.6 / Math.Sqrt(3))):0.0} dB");
        Assert.Contains("noise removal Strong", mixer.Describe(), StringComparison.Ordinal);

        await mixer.DisposeAsync();
    }

    [Fact]
    public async Task UnifiedAudioMixer_does_not_catch_up_when_on_time()
    {
        var hub = new AudioCaptureHub();
        using var memoryStream = new MemoryStream();

        var mixer = new UnifiedAudioMixer(hub, memoryStream, Stopwatch.GetTimestamp());
        mixer.AddTestChannel("game", 1.0);
        mixer.EnqueueTestChannelData("game", new byte[48000 * 2 * sizeof(float)]);

        await mixer.CatchUpAsync(new float[960], new byte[1920], new byte[192000], CancellationToken.None);

        Assert.Equal(0, mixer.TotalBytesWritten);
        await mixer.DisposeAsync();
    }
}

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

    [Fact]
    public async Task UnifiedAudioMixer_turns_a_too_loud_mix_down_instead_of_clipping_it()
    {
        var hub = new AudioCaptureHub();
        using var memoryStream = new MemoryStream();
        var mixer = new UnifiedAudioMixer(hub, memoryStream, Stopwatch.GetTimestamp() - 10 * Stopwatch.Frequency);

        // A loud game and loud desktop audio: 0.7 + 0.7 is 1.4 times full scale.
        mixer.AddTestChannel("game", 1.0);
        mixer.AddTestChannel("sys", 1.0);
        for (var slice = 0; slice < 20; slice++)
        {
            mixer.EnqueueTestChannelData("game", Sound(10, 0.7f));
            mixer.EnqueueTestChannelData("sys", Sound(10, 0.7f));
            Assert.True(await mixer.MixOneSliceAsync() > 0);
        }

        var samples = MemoryMarshal.Cast<byte, short>(memoryStream.ToArray()).ToArray();
        Assert.True(samples.Max() <= (short)(32767 * 0.892), $"peak {samples.Max()}; before, 32767 (clipped)");
        Assert.True(samples[^1] > 32767 * 0.88, "held just under the ceiling");
        Assert.Contains("instead of clipping", mixer.Describe(), StringComparison.Ordinal);

        await mixer.DisposeAsync();
    }

    [Fact]
    public async Task UnifiedAudioMixer_boosts_a_quiet_microphone_before_its_fader()
    {
        var hub = new AudioCaptureHub();
        using var memoryStream = new MemoryStream();
        var mixer = new UnifiedAudioMixer(hub, memoryStream, Stopwatch.GetTimestamp() - 10 * Stopwatch.Frequency);
        mixer.AddTestChannel(AudioChannels.Microphone, 0.5);
        Assert.True(await mixer.SetMicrophoneBoostAsync(20));
        Assert.Equal(20, mixer.MicrophoneBoostDb);

        mixer.EnqueueTestChannelData(AudioChannels.Microphone, Sound(10, 0.01f));
        Assert.True(await mixer.MixOneSliceAsync() > 0);

        // 0.01 boosted 20 dB is 0.1; the fader at 50 % halves it: 0.05.
        var last = MemoryMarshal.Cast<byte, short>(memoryStream.ToArray())[^1];
        Assert.InRange(last / 32768.0, 0.049, 0.051);

        // Out-of-range asks are held to 0..30 dB.
        await mixer.SetMicrophoneBoostAsync(99);
        Assert.Equal(UnifiedAudioMixer.MaxMicrophoneBoostDb, mixer.MicrophoneBoostDb);
        await mixer.SetMicrophoneBoostAsync(double.NaN);
        Assert.Equal(0, mixer.MicrophoneBoostDb);

        await mixer.DisposeAsync();
    }

    [Fact]
    public async Task UnifiedAudioMixer_boost_reaches_only_the_microphone()
    {
        var hub = new AudioCaptureHub();
        using var memoryStream = new MemoryStream();
        var mixer = new UnifiedAudioMixer(hub, memoryStream, Stopwatch.GetTimestamp() - 10 * Stopwatch.Frequency);
        mixer.AddTestChannel("sys", 1.0);
        await mixer.SetMicrophoneBoostAsync(30);

        mixer.EnqueueTestChannelData("sys", Sound(10, 0.1f));
        Assert.True(await mixer.MixOneSliceAsync() > 0);

        var last = MemoryMarshal.Cast<byte, short>(memoryStream.ToArray())[^1];
        Assert.InRange(last / 32768.0, 0.099, 0.101);
        await mixer.DisposeAsync();
    }

    [Fact]
    public async Task UnifiedAudioMixer_keeps_a_steady_source_close_to_the_picture_after_a_burst()
    {
        var hub = new AudioCaptureHub();
        using var memoryStream = new MemoryStream();
        var mixer = new UnifiedAudioMixer(hub, memoryStream, Stopwatch.GetTimestamp() - 100 * Stopwatch.Frequency);
        mixer.AddTestChannel("mic", 1.0);

        // A capture hiccup hands over 150 ms at once; then the microphone keeps pace for a minute.
        mixer.EnqueueTestChannelData("mic", Sound(150));
        for (var slice = 0; slice < 6000; slice++)
        {
            mixer.EnqueueTestChannelData("mic", Sound(10));
            await mixer.MixOneSliceAsync();
        }

        // Before, the microphone stayed 150 ms behind the picture for the rest of the stream.
        Assert.Contains("played away a little faster", mixer.Describe(), StringComparison.Ordinal);
        Assert.DoesNotContain("dropped (not mixed in time)", mixer.Describe(), StringComparison.Ordinal);
        await mixer.DisposeAsync();
    }

    [Fact]
    public async Task UnifiedAudioMixer_follows_a_program_that_closes_and_starts_again()
    {
        if (Environment.OSVersion.Version.Build < 19041)
        {
            return; // process loopback needs Windows 10 2004
        }

        static Process StartProgram() =>
            Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;

        var hub = new AudioCaptureHub();
        using var memoryStream = new MemoryStream();
        var mixer = new UnifiedAudioMixer(hub, memoryStream);
        var program = $"linkast-test-{Guid.NewGuid():N}.exe";

        // Not running when the stream starts: the channel is there, silent, and doesn't fail the stream.
        mixer.AddSource("app_test", new ProcessAudioSource(0, "Test") { ProgramName = program, Channel = "app_test" });
        Assert.True(mixer.HasChannel("app_test"));
        Assert.Equal(0u, mixer.FollowedProcessOf("app_test"));
        mixer.FollowPrograms();
        Assert.Equal(0u, mixer.FollowedProcessOf("app_test"));
        Assert.Equal(0, hub.ActiveSessions);
        await mixer.DisposeAsync();

        // A real program (ping): followed while it runs, let go when it closes, taken again when it restarts.
        mixer = new UnifiedAudioMixer(hub, memoryStream);
        var first = StartProgram();
        Process? second = null;
        try
        {
            mixer.AddSource("app_ping", new ProcessAudioSource((uint)first.Id, "Ping") { ProgramName = "PING.EXE", Channel = "app_ping" });
            Assert.Equal((uint)first.Id, mixer.FollowedProcessOf("app_ping"));
            Assert.Equal(1, hub.ActiveSessions);

            first.Kill();
            await first.WaitForExitAsync();
            mixer.FollowPrograms();
            if (mixer.FollowedProcessOf("app_ping") == 0)
            {
                Assert.Equal(0, hub.ActiveSessions);
            }

            second = StartProgram();
            mixer.FollowPrograms();
            var followed = mixer.FollowedProcessOf("app_ping");
            Assert.NotEqual((uint)first.Id, followed);
            Assert.NotEqual(0u, followed); // this run's second ping, or another ping that runs on this PC
            Assert.Equal(1, hub.ActiveSessions);
        }
        finally
        {
            foreach (var p in new[] { first, second })
            {
                if (p is not null)
                {
                    if (!p.HasExited)
                    {
                        p.Kill();
                    }

                    p.Dispose();
                }
            }

            await mixer.DisposeAsync();
        }

        Assert.Equal(0, hub.ActiveSessions);
    }

    [Fact]
    public void ProgramProcesses_picks_the_process_that_started_the_others()
    {
        // Chrome: the browser process started its renderers and its audio service; Explorer started it.
        ProcessEntry[] processes =
        [
            new(4, 0, "System"),
            new(100, 4, "explorer.exe"),
            new(200, 100, "chrome.exe"),
            new(201, 200, "chrome.exe"),
            new(202, 200, "chrome.exe"),
            new(203, 201, "chrome.exe"),
            new(300, 100, "Spotify.exe"),
            new(301, 300, "Spotify.exe"),
            // A second, idle copy of Chrome started from elsewhere has fewer processes under it.
            new(400, 4, "chrome.exe"),
        ];

        Assert.Equal(200u, ProgramProcesses.PickRoot(processes, "chrome.exe"));
        Assert.Equal(300u, ProgramProcesses.PickRoot(processes, "SPOTIFY.EXE"));
        Assert.Null(ProgramProcesses.PickRoot(processes, "discord.exe"));
        Assert.Null(ProgramProcesses.PickRoot(processes, ""));
    }

    [Fact]
    public void ProgramProcesses_snapshot_sees_this_process()
    {
        var self = (uint)Environment.ProcessId;
        Assert.Contains(ProgramProcesses.Snapshot(), p => p.ProcessId == self && p.ExeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UnifiedAudioMixer_fans_out_identical_audio_and_resync_silence_to_secondary_sinks()
    {
        var hub = new AudioCaptureHub();
        using var primary = new MemoryStream();
        using var secondary = new MemoryStream();

        var mixer = new UnifiedAudioMixer(hub, primary, Stopwatch.GetTimestamp() - Stopwatch.Frequency);
        mixer.AddSink("recording", secondary);
        mixer.AddTestChannel("game", 1.0);
        mixer.EnqueueTestChannelData("game", Sound(150));

        await mixer.CatchUpAsync(new float[960], new byte[1920], new byte[192000], CancellationToken.None);

        Assert.True(primary.Length > 0);
        Assert.Equal(primary.ToArray(), secondary.ToArray());

        await mixer.DisposeAsync();
    }

    [Fact]
    public async Task UnifiedAudioMixer_detaches_failing_secondary_sink_without_stopping_primary()
    {
        var hub = new AudioCaptureHub();
        using var primary = new MemoryStream();
        using var failing = new ThrowingAudioSinkStream();

        var mixer = new UnifiedAudioMixer(hub, primary, Stopwatch.GetTimestamp() - 10 * Stopwatch.Frequency);
        mixer.AddSink("recording", failing);
        var detached = new List<string>();
        mixer.SinkDetached += (_, name) => detached.Add(name);

        mixer.AddTestChannel("game", 1.0);
        mixer.EnqueueTestChannelData("game", Sound(20));

        Assert.True(await mixer.MixOneSliceAsync() > 0);
        Assert.True(await mixer.MixOneSliceAsync() > 0);

        Assert.Equal(["recording"], detached);
        Assert.Equal(2 * 1920, primary.Length);

        await mixer.DisposeAsync();
    }

    private sealed class ThrowingAudioSinkStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Broken audio pipe");
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException(new IOException("Broken audio pipe"));
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Broken audio pipe"));
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core.Audio;

namespace Linkast.Core.Tests;

public class PeakLimiterTests
{
    private static float[] Sine(double amplitude, double seconds, double hz = 440, double rightShare = 1)
    {
        var frames = (int)(48000 * seconds);
        var s = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var v = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / 48000));
            s[2 * i] = v;
            s[(2 * i) + 1] = (float)(v * rightShare);
        }

        return s;
    }

    [Fact]
    public void Sound_below_the_ceiling_passes_unchanged()
    {
        var limiter = new PeakLimiter();
        var sound = Sine(0.8, 0.5);
        var copy = (float[])sound.Clone();

        limiter.Process(sound);

        Assert.Equal(copy, sound);
        Assert.Equal(0, limiter.LimitedFrames);
    }

    [Fact]
    public void A_mix_past_full_scale_is_turned_down_instead_of_clipped()
    {
        // A game at full scale with a loud microphone on top: 1.6 times full scale.
        var limiter = new PeakLimiter();
        var sound = Sine(1.6, 0.5, rightShare: 0.5);

        limiter.Process(sound);

        Assert.True(sound.Max(MathF.Abs) <= PeakLimiter.Ceiling + 1e-6f, $"peak {sound.Max(MathF.Abs)}");
        Assert.True(limiter.LimitedFrames > 0);
        Assert.InRange(limiter.DeepestReductionDb, -6, -4); // 0.891 / 1.6 is −5.1 dB

        // Both sides share one gain: the right stays half the left (no clipped, flattened tops either).
        for (var i = 12000; i < 12100; i++)
        {
            Assert.Equal(sound[2 * i] * 0.5f, sound[(2 * i) + 1], 4);
        }
    }

    [Fact]
    public void After_a_loud_moment_the_mix_comes_back_to_full_level()
    {
        var limiter = new PeakLimiter();
        limiter.Process(Sine(2.0, 0.2));

        var after = Sine(0.5, 1.0);
        limiter.Process(after);

        // Half a second later (over three release times) the quiet sound is back at its own level.
        var tail = after.AsSpan(48000, 4800).ToArray().Max(MathF.Abs);
        Assert.InRange(tail, 0.49f, 0.5f);
    }
}

public class JitterBufferTests
{
    private const int Slice = 480; // 10 ms

    private static void WriteFrames(JitterBuffer b, int frames, ref int counter)
    {
        for (var i = 0; i < frames; i++, counter++)
        {
            var v = (float)Math.Sin(counter * 0.01);
            b.Write(v, -v);
        }
    }

    [Fact]
    public void Plays_sound_as_it_came_in_steady_state()
    {
        var b = new JitterBuffer();
        for (var i = 0; i < Slice; i++)
        {
            b.Write(i, -i);
        }

        var out_ = new float[Slice * 2];
        Assert.True(b.TryRead(out_));
        Assert.Equal(0, b.Frames);
        Assert.Equal(123f, out_[246]);
        Assert.Equal(-123f, out_[247]);
        Assert.False(b.TryRead(out_));
        Assert.Equal(1, b.Underruns);
    }

    [Fact]
    public void A_burst_that_left_sound_waiting_is_played_away_instead_of_staying_late()
    {
        var b = new JitterBuffer();
        var counter = 0;
        var out_ = new float[Slice * 2];

        // A capture hiccup: 150 ms arrive at once, then the source keeps pace with the mixer for a minute.
        WriteFrames(b, 48000 * 150 / 1000, ref counter);
        for (var read = 0; read < 6000; read++)
        {
            WriteFrames(b, Slice, ref counter);
            Assert.True(b.TryRead(out_));
        }

        // Before, the 150 ms stayed for the rest of the stream: that source was heard 150 ms late.
        Assert.InRange(b.Buffered.TotalMilliseconds, 10, 30);
        Assert.InRange(b.FramesCaughtUp, 48 * 120, 48 * 150);
        Assert.Equal(0, b.Rate);
        Assert.Equal(0, b.OverflowFrames);
        Assert.Equal(0, b.Underruns);
    }

    [Fact]
    public void A_source_whose_clock_runs_fast_does_not_drift_late_over_a_long_stream()
    {
        var b = new JitterBuffer();
        var counter = 0;
        var out_ = new float[Slice * 2];
        var most = TimeSpan.Zero;

        // 0.2 % fast (a cheap USB microphone): one frame too many every 10 ms, for 20 minutes. Before, the
        // waiting sound grew to the 200 ms cap within 2 minutes and stayed there, dropping sound.
        WriteFrames(b, Slice, ref counter);
        for (var read = 0; read < 120_000; read++)
        {
            WriteFrames(b, Slice + 1, ref counter);
            b.TryRead(out_);
            most = b.Buffered > most ? b.Buffered : most;
        }

        Assert.True(most < JitterBuffer.HighWater + TimeSpan.FromMilliseconds(25), $"most waiting {most.TotalMilliseconds:0} ms");
        Assert.Equal(0, b.OverflowFrames);
        Assert.Equal(0, b.Underruns);
    }

    [Fact]
    public void A_source_whose_clock_runs_slow_is_stretched_before_it_runs_dry()
    {
        var b = new JitterBuffer();
        var counter = 0;
        var out_ = new float[Slice * 2];

        // 0.2 % slow: one frame short every 10 ms, for 20 minutes. Without stretching that is a 10 ms gap
        // every 5 s (240 in all).
        WriteFrames(b, 48000 * 20 / 1000, ref counter);
        for (var read = 0; read < 120_000; read++)
        {
            WriteFrames(b, Slice - 1, ref counter);
            b.TryRead(out_);
        }

        Assert.True(b.Underruns <= 2, $"{b.Underruns} gaps");
        Assert.True(b.FramesStretched > 0);
        Assert.InRange(b.Buffered.TotalMilliseconds, 1, 40);
    }

    [Fact]
    public void A_quiet_source_that_hands_over_nothing_keeps_its_rate()
    {
        var b = new JitterBuffer();
        var out_ = new float[Slice * 2];
        for (var read = 0; read < 500; read++)
        {
            Assert.False(b.TryRead(out_));
        }

        Assert.Equal(0, b.Rate);
    }

    [Fact]
    public void Changing_rate_keeps_the_sound_continuous()
    {
        // A slow ramp through a burst catch-up: no sample may jump further than the ramp itself moves.
        var b = new JitterBuffer();
        var value = 0f;
        var out_ = new float[Slice * 2];
        void Feed(int frames)
        {
            for (var i = 0; i < frames; i++)
            {
                value += 0.0001f;
                b.Write(value, value);
            }
        }

        Feed(48000 * 100 / 1000);
        var last = float.NaN;
        for (var read = 0; read < 3000; read++)
        {
            Feed(Slice);
            Assert.True(b.TryRead(out_));
            for (var i = 0; i < out_.Length; i += 2)
            {
                if (!float.IsNaN(last))
                {
                    var jump = out_[i] - last;
                    Assert.InRange(jump, -1e-6f, 0.0001f * 1.1f); // the ramp, played up to 0.5 % faster, plus float rounding
                }

                last = out_[i];
            }
        }

        Assert.True(b.FramesCaughtUp > 0);
    }
}

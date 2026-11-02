// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core.Audio;

namespace Linkast.Core.Tests;

public class SpeechGateTests
{
    private const int Rate = SpeechGate.SampleRate;

    private static float[] Silence(double seconds) => new float[(int)(seconds * Rate) * 2];

    /// <summary>Room noise: white noise at the given RMS (full scale = 1), the same in both channels.</summary>
    private static void AddRoomNoise(float[] stereo, double rms, int seed)
    {
        var rng = new Random(seed);
        for (int i = 0; i < stereo.Length; i += 2)
        {
            float v = (float)((rng.NextDouble() * 2 - 1) * rms * Math.Sqrt(3));
            stereo[i] += v;
            stereo[i + 1] += v;
        }
    }

    /// <summary>Voiced speech: 120 Hz pitch with 20 harmonics, a slow pitch wobble and a soft attack and release.</summary>
    private static void AddVoice(float[] stereo, double startSeconds, double seconds, double rms)
    {
        int start = (int)(startSeconds * Rate);
        int count = (int)(seconds * Rate);
        double phase = 0;
        var samples = new double[count];
        for (int n = 0; n < count; n++)
        {
            double f0 = 120 * (1 + 0.03 * Math.Sin(2 * Math.PI * 5 * n / Rate));
            phase += 2 * Math.PI * f0 / Rate;
            double s = 0;
            for (int h = 1; h <= 20; h++)
            {
                s += Math.Sin(h * phase) / h;
            }

            double edge = Math.Min(1, Math.Min(n, count - 1 - n) / (0.02 * Rate));
            samples[n] = s * edge;
        }

        double power = samples.Sum(v => v * v) / count;
        double scale = rms / Math.Sqrt(power);
        for (int n = 0; n < count; n++)
        {
            float v = (float)(samples[n] * scale);
            stereo[(start + n) * 2] += v;
            stereo[(start + n) * 2 + 1] += v;
        }
    }

    /// <summary>A breath: low-passed noise (energy mostly under 3 kHz) with a smooth envelope.</summary>
    private static void AddBreath(float[] stereo, double startSeconds, double seconds, double rms, int seed)
    {
        int start = (int)(startSeconds * Rate);
        int count = (int)(seconds * Rate);
        var rng = new Random(seed);
        var samples = new double[count];
        double low = 0;
        for (int n = 0; n < count; n++)
        {
            low += 0.25 * ((rng.NextDouble() * 2 - 1) - low);
            samples[n] = low * Math.Sin(Math.PI * n / count);
        }

        double power = samples.Sum(v => v * v) / count;
        double scale = rms / Math.Sqrt(power);
        for (int n = 0; n < count; n++)
        {
            float v = (float)(samples[n] * scale);
            stereo[(start + n) * 2] += v;
            stereo[(start + n) * 2 + 1] += v;
        }
    }

    private static double RmsDb(float[] stereo, double fromSeconds, double toSeconds)
    {
        int from = (int)(fromSeconds * Rate) * 2;
        int to = (int)(toSeconds * Rate) * 2;
        double sum = 0;
        for (int i = from; i < to; i++)
        {
            sum += (double)stereo[i] * stereo[i];
        }

        return 10 * Math.Log10(sum / (to - from) + 1e-15);
    }

    private static float[] Run(float[] input, int chunk = 960)
    {
        var gate = new SpeechGate();
        var output = (float[])input.Clone();
        for (int i = 0; i < output.Length; i += chunk)
        {
            gate.Process(output.AsSpan(i, Math.Min(chunk, output.Length - i)));
        }

        return output;
    }

    [Theory]
    [InlineData(0.003)]  // quiet room
    [InlineData(0.012)]  // the level at which the old fixed -38 dB gate stayed open (a mic at 200%)
    [InlineData(0.03)]   // a noisy room
    public void Steady_room_noise_is_removed_at_any_level(double noiseRms)
    {
        var input = Silence(3);
        AddRoomNoise(input, noiseRms, 1);

        var output = Run(input);

        var reduction = RmsDb(input, 1, 3) - RmsDb(output, 1, 3);
        Assert.True(reduction >= 20, $"Noise reduced by only {reduction:0.0} dB");
    }

    [Fact]
    public void Speech_passes_unchanged_after_the_gate_opens()
    {
        var input = Silence(3);
        AddRoomNoise(input, 0.012, 2);
        AddVoice(input, 1.0, 1.0, 0.1);

        var output = Run(input);

        // The output is the input delayed by the lookahead.
        double delay = (double)SpeechGate.LookaheadFrames / Rate;
        // The 85 Hz high-pass takes about 1 dB off this test voice, whose strongest part is at 120 Hz.
        var difference = Math.Abs(RmsDb(input, 1.1, 1.9) - RmsDb(output, 1.1 + delay, 1.9 + delay));
        Assert.True(difference < 2.0, $"Speech level changed by {difference:0.0} dB");
    }

    [Fact]
    public void The_start_of_a_word_is_not_clipped()
    {
        var input = Silence(3);
        AddRoomNoise(input, 0.012, 3);
        AddVoice(input, 1.0, 1.0, 0.1);

        var output = Run(input);

        // The first 20 ms of the word, which reaches the output one lookahead later.
        double delay = (double)SpeechGate.LookaheadFrames / Rate;
        var loss = RmsDb(input, 1.0, 1.02) - RmsDb(output, 1.0 + delay, 1.02 + delay);
        Assert.True(loss < 3.0, $"The first 20 ms of the word lost {loss:0.0} dB");
    }

    [Fact]
    public void A_breath_between_words_is_attenuated()
    {
        var input = Silence(5);
        AddRoomNoise(input, 0.012, 4);
        AddVoice(input, 0.5, 0.8, 0.1);
        AddBreath(input, 2.5, 0.5, 0.04, 5);   // louder than the room, quieter than speech
        AddVoice(input, 3.6, 0.8, 0.1);

        var output = Run(input);

        double delay = (double)SpeechGate.LookaheadFrames / Rate;
        var reduction = RmsDb(input, 2.65, 2.85) - RmsDb(output, 2.65 + delay, 2.85 + delay);
        Assert.True(reduction >= 12, $"The breath was reduced by only {reduction:0.0} dB");
    }

    [Fact]
    public void Short_pauses_inside_a_sentence_do_not_chop_the_speech()
    {
        var input = Silence(4);
        AddRoomNoise(input, 0.012, 6);
        AddVoice(input, 1.0, 0.4, 0.1);
        AddVoice(input, 1.55, 0.4, 0.1);   // a 150 ms pause between two words
        AddVoice(input, 2.1, 0.4, 0.1);

        var output = Run(input);

        double delay = (double)SpeechGate.LookaheadFrames / Rate;
        var second = RmsDb(input, 1.6, 1.9) - RmsDb(output, 1.6 + delay, 1.9 + delay);
        var third = RmsDb(input, 2.15, 2.45) - RmsDb(output, 2.15 + delay, 2.45 + delay);
        Assert.True(Math.Abs(second) < 1.5 && Math.Abs(third) < 1.5, $"Words changed by {second:0.0} and {third:0.0} dB");
    }

    [Fact]
    public void Continuous_speech_for_a_long_time_keeps_the_gate_open()
    {
        var input = Silence(12);
        AddRoomNoise(input, 0.012, 7);
        AddVoice(input, 0.5, 11, 0.1);

        var output = Run(input);

        double delay = (double)SpeechGate.LookaheadFrames / Rate;
        var difference = Math.Abs(RmsDb(input, 9, 11) - RmsDb(output, 9 + delay, 11 + delay));
        Assert.True(difference < 2.0, $"After 9 s of speech the level was off by {difference:0.0} dB");
    }

    [Fact]
    public void The_result_does_not_depend_on_how_the_audio_is_cut_into_chunks()
    {
        var input = Silence(3);
        AddRoomNoise(input, 0.012, 8);
        AddVoice(input, 1.0, 0.8, 0.1);

        var a = Run(input, 960);
        var b = Run(input, 14);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Reset_forgets_old_audio()
    {
        var gate = new SpeechGate();
        var loud = Silence(1);
        AddVoice(loud, 0, 1, 0.3);
        gate.Process(loud);

        gate.Reset();
        var quiet = Silence(0.05);
        gate.Process(quiet);

        Assert.All(quiet, v => Assert.Equal(0f, v));
    }
}

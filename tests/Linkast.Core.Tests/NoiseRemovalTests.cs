// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Linkast.Core.Audio;
using Linkast.Core.Audio.Noise;

namespace Linkast.Core.Tests;

public class ComplexFftTests
{
    [Theory]
    [InlineData(960)]
    [InlineData(480)]
    [InlineData(60)]
    [InlineData(8)]
    public void Forward_matches_a_plain_DFT(int n)
    {
        var random = new Random(n);
        var input = new Complex32[n];
        for (var i = 0; i < n; i++)
        {
            input[i] = new Complex32((float)(random.NextDouble() * 2 - 1), (float)(random.NextDouble() * 2 - 1));
        }

        var output = new Complex32[n];
        new ComplexFft(n).Forward(input, output);

        for (var k = 0; k < n; k++)
        {
            double re = 0, im = 0;
            for (var t = 0; t < n; t++)
            {
                var phase = -2 * Math.PI * k * t / n;
                re += input[t].Re * Math.Cos(phase) - input[t].Im * Math.Sin(phase);
                im += input[t].Re * Math.Sin(phase) + input[t].Im * Math.Cos(phase);
            }

            Assert.True(Math.Abs(output[k].Re - re) < 1e-3 * Math.Sqrt(n), $"bin {k} real {output[k].Re} vs {re}");
            Assert.True(Math.Abs(output[k].Im - im) < 1e-3 * Math.Sqrt(n), $"bin {k} imaginary {output[k].Im} vs {im}");
        }
    }

    [Fact]
    public void Inverse_of_forward_gives_the_input_times_n()
    {
        const int n = 960;
        var random = new Random(1);
        var input = new Complex32[n];
        for (var i = 0; i < n; i++)
        {
            input[i] = new Complex32((float)random.NextDouble(), (float)random.NextDouble());
        }

        var fft = new ComplexFft(n);
        var spectrum = new Complex32[n];
        var back = new Complex32[n];
        fft.Forward(input, spectrum);
        fft.Inverse(spectrum, back);

        for (var i = 0; i < n; i++)
        {
            Assert.Equal(input[i].Re, back[i].Re / n, 4);
            Assert.Equal(input[i].Im, back[i].Im / n, 4);
        }
    }

    [Fact]
    public void Rejects_sizes_with_other_prime_factors() =>
        Assert.Throws<ArgumentException>(() => new ComplexFft(7 * 64));
}

public class NoiseRemovalTests
{
    private const int Frame = MicrophoneNoiseRemover.FrameSize;

    [Fact]
    public void The_embedded_model_loads_with_RNNoise_layer_sizes()
    {
        var model = RnNoiseModel.Default;
        Assert.Equal((42, 24), (model.InputDense.Inputs, model.InputDense.Neurons));
        Assert.Equal((24, 24), (model.VadGru.Inputs, model.VadGru.Neurons));
        Assert.Equal((90, 48), (model.NoiseGru.Inputs, model.NoiseGru.Neurons));
        Assert.Equal((114, 96), (model.DenoiseGru.Inputs, model.DenoiseGru.Neurons));
        Assert.Equal((96, 22), (model.DenoiseOutput.Inputs, model.DenoiseOutput.Neurons));
        Assert.Equal((24, 1), (model.VadOutput.Inputs, model.VadOutput.Neurons));
    }

    [Fact]
    public void A_model_of_the_wrong_size_is_refused() =>
        Assert.Throws<InvalidDataException>(() => RnNoiseModel.FromBytes(new byte[1000]));

    [Fact]
    public void Off_leaves_the_sound_untouched()
    {
        var remover = new MicrophoneNoiseRemover { Level = NoiseRemoval.Off };
        var block = Noise(2 * Frame, seed: 3, amplitude: 0.1f);
        var copy = (float[])block.Clone();

        remover.Process(block);

        Assert.Equal(copy, block);
        Assert.Equal(0, remover.BlocksProcessed);
    }

    [Theory]
    [InlineData(NoiseRemoval.Standard, 20)]
    [InlineData(NoiseRemoval.Strong, 30)]
    public void Steady_fan_noise_is_removed(NoiseRemoval level, double atLeastDb)
    {
        var remover = new MicrophoneNoiseRemover { Level = level };
        var noise = PinkNoise(48000 * 4, seed: 11, amplitude: 0.03f);

        var before = 0.0;
        var after = 0.0;
        var block = new float[2 * Frame];
        for (var f = 0; f < noise.Length / Frame; f++)
        {
            for (var i = 0; i < Frame; i++)
            {
                block[2 * i] = block[2 * i + 1] = noise[f * Frame + i];
            }

            remover.Process(block);
            if (f >= 200) // after the first two seconds, once the network has heard the noise
            {
                for (var i = 0; i < Frame; i++)
                {
                    before += noise[f * Frame + i] * noise[f * Frame + i];
                    after += block[2 * i] * block[2 * i];
                }
            }
        }

        var reductionDb = 10 * Math.Log10(before / Math.Max(after, 1e-20));
        Assert.True(reductionDb >= atLeastDb, $"{level} removed {reductionDb:0.0} dB of fan noise; expected at least {atLeastDb} dB.");
    }

    [Fact]
    public void Silence_stays_silent_and_finite()
    {
        var remover = new MicrophoneNoiseRemover { Level = NoiseRemoval.Strong };
        var block = new float[2 * Frame];
        for (var f = 0; f < 50; f++)
        {
            remover.Process(block);
            Assert.All(block, s => Assert.True(float.IsFinite(s) && Math.Abs(s) < 1e-6f));
        }
    }

    [Fact]
    public void Loud_clipped_input_never_produces_NaN()
    {
        var remover = new MicrophoneNoiseRemover { Level = NoiseRemoval.Standard };
        var block = new float[2 * Frame];
        for (var f = 0; f < 100; f++)
        {
            for (var i = 0; i < block.Length; i++)
            {
                block[i] = (i / 7 + f) % 2 == 0 ? 5f : -5f; // a fader at 500 % into a square wave
            }

            remover.Process(block);
            Assert.All(block, s => Assert.True(float.IsFinite(s)));
        }
    }

    [Fact]
    public void Both_channels_get_the_cleaned_mix()
    {
        var remover = new MicrophoneNoiseRemover { Level = NoiseRemoval.Standard };
        var block = Noise(2 * Frame, seed: 5, amplitude: 0.2f);
        remover.Process(block);
        for (var i = 0; i < Frame; i++)
        {
            Assert.Equal(block[2 * i], block[2 * i + 1]);
        }
    }

    [Fact]
    public void Blocks_must_be_whole_frames() =>
        Assert.Throws<ArgumentException>(() => new MicrophoneNoiseRemover { Level = NoiseRemoval.Standard }.Process(new float[100]));

    [Fact]
    public void Cleaning_costs_a_small_share_of_one_core()
    {
        var remover = new MicrophoneNoiseRemover { Level = NoiseRemoval.Strong };
        var block = new float[2 * Frame];
        var noise = Noise(2 * Frame * 1000, seed: 9, amplitude: 0.05f);
        var watch = Stopwatch.StartNew();
        for (var f = 0; f < 1000; f++) // 10 s of sound
        {
            noise.AsSpan(f * 2 * Frame, 2 * Frame).CopyTo(block);
            remover.Process(block);
        }

        watch.Stop();

        // About 2 to 3 % of a core on a desktop CPU; 25 % leaves room for slow CI machines.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2.5), $"10 s of sound took {watch.Elapsed.TotalMilliseconds:0} ms to clean.");
        Assert.InRange(remover.CoreShare, 0.0001, 0.25);
        Assert.Equal(1000, remover.BlocksProcessed);
    }

    private static float[] Noise(int count, int seed, float amplitude)
    {
        var random = new Random(seed);
        var data = new float[count];
        for (var i = 0; i < count; i++)
        {
            data[i] = (float)(random.NextDouble() * 2 - 1) * amplitude;
        }

        return data;
    }

    /// <summary>Pink-ish noise (white noise through a gentle low-pass), like a fan.</summary>
    private static float[] PinkNoise(int count, int seed, float amplitude)
    {
        var white = Noise(count, seed, 1f);
        var data = new float[count];
        double b0 = 0, b1 = 0, b2 = 0;
        for (var i = 0; i < count; i++)
        {
            b0 = 0.99765 * b0 + white[i] * 0.0990460;
            b1 = 0.96300 * b1 + white[i] * 0.2965164;
            b2 = 0.57000 * b2 + white[i] * 1.0526913;
            data[i] = (float)((b0 + b1 + b2 + white[i] * 0.1848) * amplitude * 0.25);
        }

        return data;
    }
}

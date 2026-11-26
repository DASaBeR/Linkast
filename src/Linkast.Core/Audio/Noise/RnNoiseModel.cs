// SPDX-License-Identifier: GPL-3.0-or-later
// The network layout and the weights are RNNoise v0.1 by Jean-Marc Valin, Mozilla and Xiph.Org
// (BSD-3-Clause, see THIRD-PARTY-NOTICES.md).
namespace Linkast.Core.Audio.Noise;

internal enum RnnActivation
{
    Tanh = 0,
    Sigmoid = 1,
    Relu = 2,
}

/// <summary>A fully connected layer. Weights are stored per neuron (row i holds the inputs of neuron i).</summary>
internal sealed class DenseLayer(int inputs, int neurons, RnnActivation activation, float[] weights, float[] bias)
{
    public int Inputs { get; } = inputs;
    public int Neurons { get; } = neurons;
    public RnnActivation Activation { get; } = activation;
    public float[] Weights { get; } = weights;
    public float[] Bias { get; } = bias;
}

/// <summary>
/// A GRU layer. <see cref="InputWeights"/> holds 3 gates × neurons rows of <see cref="Inputs"/> values (update,
/// reset, candidate); <see cref="RecurrentWeights"/> the same with <see cref="Neurons"/> values per row.
/// </summary>
internal sealed class GruLayer(int inputs, int neurons, RnnActivation activation, float[] inputWeights, float[] recurrentWeights, float[] bias)
{
    public int Inputs { get; } = inputs;
    public int Neurons { get; } = neurons;
    public RnnActivation Activation { get; } = activation;
    public float[] InputWeights { get; } = inputWeights;
    public float[] RecurrentWeights { get; } = recurrentWeights;
    public float[] Bias { get; } = bias;
}

/// <summary>
/// The RNNoise network (Xiph's v0.1 model, trained on speech mixed with many kinds of noise). The weights are
/// signed bytes in the order of RNNoise's rnn_data.c (also the order of FFmpeg's .rnnn files), embedded in
/// this assembly. Loaded once and shared, read-only, by every denoiser.
/// </summary>
internal sealed class RnNoiseModel
{
    public const string ResourceName = "Linkast.Core.Audio.Noise.rnnoise-xiph-v0.1.bin";

    private static readonly Lazy<RnNoiseModel> s_default = new(LoadEmbedded);

    private RnNoiseModel(DenseLayer inputDense, GruLayer vadGru, GruLayer noiseGru, GruLayer denoiseGru, DenseLayer denoiseOutput, DenseLayer vadOutput)
    {
        InputDense = inputDense;
        VadGru = vadGru;
        NoiseGru = noiseGru;
        DenoiseGru = denoiseGru;
        DenoiseOutput = denoiseOutput;
        VadOutput = vadOutput;
    }

    public static RnNoiseModel Default => s_default.Value;

    public DenseLayer InputDense { get; }
    public GruLayer VadGru { get; }
    public GruLayer NoiseGru { get; }
    public GruLayer DenoiseGru { get; }
    public DenseLayer DenoiseOutput { get; }
    public DenseLayer VadOutput { get; }

    private static RnNoiseModel LoadEmbedded()
    {
        using var stream = typeof(RnNoiseModel).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The noise removal model ({ResourceName}) is missing from the build.");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return FromBytes(bytes);
    }

    /// <summary>Reads the layers in the order input dense, VAD GRU, noise GRU, denoise GRU, denoise output, VAD output.</summary>
    internal static RnNoiseModel FromBytes(ReadOnlySpan<byte> data)
    {
        var at = 0;
        var inputDense = Dense(data, ref at, 42, 24, RnnActivation.Tanh);
        var vadGru = Gru(data, ref at, 24, 24, RnnActivation.Relu);
        var noiseGru = Gru(data, ref at, 90, 48, RnnActivation.Relu);
        var denoiseGru = Gru(data, ref at, 114, 96, RnnActivation.Relu);
        var denoiseOutput = Dense(data, ref at, 96, 22, RnnActivation.Sigmoid);
        var vadOutput = Dense(data, ref at, 24, 1, RnnActivation.Sigmoid);
        if (at != data.Length)
        {
            throw new InvalidDataException($"The noise removal model has {data.Length} values; {at} were expected.");
        }

        return new RnNoiseModel(inputDense, vadGru, noiseGru, denoiseGru, denoiseOutput, vadOutput);
    }

    private static float[] Take(ReadOnlySpan<byte> data, ref int at, int count)
    {
        if (at + count > data.Length)
        {
            throw new InvalidDataException("The noise removal model is shorter than expected.");
        }

        var values = new float[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = (sbyte)data[at + i];
        }

        at += count;
        return values;
    }

    // rnn_data.c: input_weights[j * N + i] for input j, neuron i.
    private static DenseLayer Dense(ReadOnlySpan<byte> data, ref int at, int inputs, int neurons, RnnActivation activation)
    {
        var raw = Take(data, ref at, inputs * neurons);
        var bias = Take(data, ref at, neurons);
        var rows = new float[inputs * neurons];
        for (var j = 0; j < inputs; j++)
        {
            for (var i = 0; i < neurons; i++)
            {
                rows[i * inputs + j] = raw[j * neurons + i];
            }
        }

        return new DenseLayer(inputs, neurons, activation, rows, bias);
    }

    // rnn_data.c: weights[g * N + j * 3N + i] for gate g, input j, neuron i.
    private static GruLayer Gru(ReadOnlySpan<byte> data, ref int at, int inputs, int neurons, RnnActivation activation)
    {
        var rawInput = Take(data, ref at, inputs * neurons * 3);
        var rawRecurrent = Take(data, ref at, neurons * neurons * 3);
        var bias = Take(data, ref at, neurons * 3);
        return new GruLayer(inputs, neurons, activation, Rows(rawInput, inputs, neurons), Rows(rawRecurrent, neurons, neurons), bias);
    }

    private static float[] Rows(float[] raw, int inputs, int neurons)
    {
        var rows = new float[3 * neurons * inputs];
        for (var g = 0; g < 3; g++)
        {
            for (var j = 0; j < inputs; j++)
            {
                for (var i = 0; i < neurons; i++)
                {
                    rows[(g * neurons + i) * inputs + j] = raw[g * neurons + j * 3 * neurons + i];
                }
            }
        }

        return rows;
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core.Audio;

/// <summary>How much noise is removed from the microphone (see <c>MicrophoneNoiseRemover</c>).</summary>
public enum NoiseRemoval
{
    /// <summary>The microphone as it is.</summary>
    Off = 0,

    /// <summary>RNNoise: removes fans, hum, hiss, keyboards and street sound, under and between words.</summary>
    Standard = 1,

    /// <summary>RNNoise, and what remains between words (breaths, clicks) is lowered by 20 dB.</summary>
    Strong = 2,
}

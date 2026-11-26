// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;

namespace Linkast.Core.Audio.Noise;

/// <summary>
/// Removes noise from a microphone on this PC, in real time, with RNNoise (a recurrent neural network trained
/// on speech mixed with many kinds of noise). It cleans fans, hum, hiss, keyboards and street sound under and
/// between words, not only in the pauses as a gate does.
/// <list type="bullet">
/// <item><see cref="NoiseRemoval.Standard"/>: the network alone.</item>
/// <item><see cref="NoiseRemoval.Strong"/>: also lowers what is left between words (breaths, clicks, the
/// faint rest of the noise) by 20 dB while the network hears no speech.</item>
/// </list>
/// Takes 48 kHz interleaved stereo float audio in blocks of 10 ms (<see cref="FrameSize"/> frames). A
/// microphone is one voice, so both channels are mixed, cleaned once and written to both: half the CPU of two
/// channels. The sound comes out 10 ms later. Not thread-safe: one caller (the mixer) processes; only
/// <see cref="Level"/> may be set from another thread.
/// </summary>
public sealed class MicrophoneNoiseRemover
{
    /// <summary>Frames in one block: 10 ms at 48 kHz.</summary>
    public const int FrameSize = RnNoiseDenoiser.FrameSize;

    private const float Scale = 32768f;

    // Strong: the gate between words.
    private const float SpeechProbability = 0.6f;
    private const int HoldFrames = 25;                // 250 ms after the last frame that held speech
    private const float ClosedGain = 0.1f;            // -20 dB
    private static readonly float OpenStep = 1f / (0.005f * 48000);   // fully open in 5 ms
    private static readonly float CloseStep = 1f / (0.080f * 48000);  // fully closed in 80 ms

    private readonly RnNoiseDenoiser _denoiser = new();
    private readonly float[] _mono = new float[FrameSize];
    private volatile NoiseRemoval _level;
    private NoiseRemoval _appliedLevel;
    private int _hold;
    private float _gateGain = 1f;
    private long _processingTicks;
    private long _frames;
    private long _speechFrames;

    /// <summary>How much noise is removed. Can be changed while audio flows.</summary>
    public NoiseRemoval Level
    {
        get => _level;
        set => _level = Enum.IsDefined(value) ? value : NoiseRemoval.Off;
    }

    /// <summary>How likely the last block held speech, 0 to 1 (0 while off).</summary>
    public float SpeechProbability01 { get; private set; }

    /// <summary>Blocks cleaned so far (each 10 ms).</summary>
    public long BlocksProcessed => Interlocked.Read(ref _frames);

    /// <summary>The share of cleaned blocks that held speech.</summary>
    public double SpeechShare => BlocksProcessed == 0 ? 0 : (double)Interlocked.Read(ref _speechFrames) / BlocksProcessed;

    /// <summary>
    /// CPU time spent cleaning, as a share of one core: 0.02 means 2 % of one core while the microphone is on.
    /// </summary>
    public double CoreShare
    {
        get
        {
            var blocks = BlocksProcessed;
            return blocks == 0 ? 0 : Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _processingTicks)).TotalSeconds / (blocks * 0.01);
        }
    }

    /// <summary>
    /// Cleans <paramref name="stereo"/> in place: interleaved left/right samples (±1), a multiple of
    /// <see cref="FrameSize"/> frames. Does nothing while <see cref="Level"/> is Off.
    /// </summary>
    public void Process(Span<float> stereo)
    {
        var level = _level;
        if (level != _appliedLevel)
        {
            if (_appliedLevel == NoiseRemoval.Off)
            {
                // Switched on: what the network heard before must not shape the first words.
                _denoiser.Reset();
                _hold = 0;
                _gateGain = 1f;
            }

            _appliedLevel = level;
        }

        if (level == NoiseRemoval.Off)
        {
            SpeechProbability01 = 0;
            return;
        }

        if (stereo.Length % (2 * FrameSize) != 0)
        {
            throw new ArgumentException($"Audio is cleaned in blocks of {FrameSize} stereo frames.", nameof(stereo));
        }

        var began = Stopwatch.GetTimestamp();
        for (var block = 0; block < stereo.Length; block += 2 * FrameSize)
        {
            var slice = stereo.Slice(block, 2 * FrameSize);
            for (var i = 0; i < FrameSize; i++)
            {
                _mono[i] = (slice[2 * i] + slice[2 * i + 1]) * (0.5f * Scale);
            }

            var speech = _denoiser.ProcessFrame(_mono, _mono);
            SpeechProbability01 = speech;
            Interlocked.Increment(ref _frames);
            if (speech >= SpeechProbability)
            {
                Interlocked.Increment(ref _speechFrames);
            }

            var strong = level == NoiseRemoval.Strong;
            if (strong)
            {
                _hold = speech >= SpeechProbability ? HoldFrames : Math.Max(0, _hold - 1);
            }

            var target = !strong || _hold > 0 ? 1f : ClosedGain;
            for (var i = 0; i < FrameSize; i++)
            {
                if (_gateGain < target)
                {
                    _gateGain = MathF.Min(target, _gateGain + OpenStep);
                }
                else if (_gateGain > target)
                {
                    _gateGain = MathF.Max(target, _gateGain - CloseStep);
                }

                var v = _mono[i] * (_gateGain / Scale);
                slice[2 * i] = v;
                slice[2 * i + 1] = v;
            }
        }

        Interlocked.Add(ref _processingTicks, Stopwatch.GetTimestamp() - began);
    }
}

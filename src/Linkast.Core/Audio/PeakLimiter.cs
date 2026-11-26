// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core.Audio;

/// <summary>
/// Keeps the stream's mix under <see cref="Ceiling"/> (−1 dBFS) instead of clipping it. Several loud sounds
/// together (a game, the microphone turned up, an alert) add up past full scale; before, the excess was cut
/// off sample by sample, which sounds harsh and crackly on stream. Here the whole mix is turned down at
/// once when a peak would pass the ceiling, and comes back up smoothly over <see cref="ReleaseSeconds"/>.
/// Both channels share one gain, so the stereo picture doesn't move. Below the ceiling nothing changes.
/// Takes 48 kHz interleaved stereo float samples. Not thread-safe: one caller (the mixer).
/// </summary>
public sealed class PeakLimiter
{
    /// <summary>The highest a sample may be: −1 dBFS, room for the encoder's own overshoot.</summary>
    public const float Ceiling = 0.891f;

    /// <summary>How long the gain takes to come most of the way back after a peak.</summary>
    public const double ReleaseSeconds = 0.15;

    private readonly float _release;
    private float _gain = 1f;
    private long _limitedFrames;
    private float _lowestGain = 1f;

    public PeakLimiter(int sampleRate = 48000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        _release = (float)(1 - Math.Exp(-1 / (ReleaseSeconds * sampleRate)));
    }

    /// <summary>Frames (1/48000 s) that were turned down so far.</summary>
    public long LimitedFrames => Interlocked.Read(ref _limitedFrames);

    /// <summary>The most the mix was turned down so far, in dB (0 when never).</summary>
    public double DeepestReductionDb => 20 * Math.Log10(Volatile.Read(ref _lowestGain));

    /// <summary>Limits <paramref name="stereo"/> (interleaved left/right) in place.</summary>
    public void Process(Span<float> stereo)
    {
        var limited = 0;
        for (var i = 0; i + 1 < stereo.Length; i += 2)
        {
            var peak = MathF.Max(MathF.Abs(stereo[i]), MathF.Abs(stereo[i + 1]));

            // Back up towards unchanged, then down at once if this frame would still pass the ceiling.
            _gain += (1f - _gain) * _release;
            if (peak * _gain > Ceiling)
            {
                _gain = Ceiling / peak;
            }

            if (_gain < 0.9999f)
            {
                limited++;
                if (_gain < _lowestGain)
                {
                    Volatile.Write(ref _lowestGain, _gain);
                }
            }

            stereo[i] *= _gain;
            stereo[i + 1] *= _gain;
        }

        if (limited > 0)
        {
            Interlocked.Add(ref _limitedFrames, limited);
        }
    }
}

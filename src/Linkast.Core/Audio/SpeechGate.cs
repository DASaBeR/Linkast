// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core.Audio;

/// <summary>
/// Microphone noise suppressor for 48 kHz interleaved stereo float audio. It removes what is not speech
/// between words: room noise, fan hiss, breaths, mouth clicks.
/// <para>
/// A plain level gate cannot do this. A fixed threshold is wrong for every microphone and volume (at 200% the
/// room noise alone is above it), and a breath is louder than the room, so a level gate lets it through.
/// This gate decides on two things instead:
/// </para>
/// <list type="bullet">
/// <item>the <b>noise floor</b>, tracked as the lowest recent level, so it works at any gain;</item>
/// <item><b>voicing</b>: speech vowels are periodic (a pitch between 70 and 400 Hz), while breath and room
/// noise are not. A short hold keeps the consonants around a vowel, and a very loud sound opens the gate
/// too, so a hard "s" or "t" is not cut.</item>
/// </list>
/// The audio is delayed by <see cref="LookaheadFrames"/> so the gate has already opened when a word begins.
/// </summary>
public sealed class SpeechGate
{
    public const int SampleRate = 48000;

    /// <summary>The analysis hop: 10 ms.</summary>
    public const int FrameSize = 480;

    /// <summary>Delay of the audio against the gate's decision: 40 ms.</summary>
    public const int LookaheadFrames = 1920;

    private const int Decimation = 4;
    private const int HistorySize = 480;      // 40 ms at 12 kHz
    private const int MinLag = 30;            // 400 Hz at 12 kHz
    private const int MaxLag = 171;           // 70 Hz at 12 kHz

    private const float HighPassAlpha = 0.989f;       // 85 Hz one-pole high-pass
    private const float VoicingThreshold = 0.5f;
    private const float VoicedAboveFloorDb = 6f;
    private const float LoudAboveFloorDb = 26f;
    private const int VoicedHoldFrames = 22;          // 220 ms after the last voiced frame
    private const int LoudHoldFrames = 12;            // 120 ms after a loud unvoiced sound
    private const float ClosedGain = 0.05f;           // -26 dB
    private const float FloorCeilingDb = -20f;
    private const float FloorRiseDbPerFrame = 0.01f;  // 1 dB per second, only while nothing is voiced

    // exp(-1/(t*fs)): 3 ms to open, 40 ms to close
    private static readonly float AttackCoefficient = 1f - MathF.Exp(-1f / (0.003f * SampleRate));
    private static readonly float ReleaseCoefficient = 1f - MathF.Exp(-1f / (0.040f * SampleRate));

    private readonly float[] _delayL = new float[LookaheadFrames];
    private readonly float[] _delayR = new float[LookaheadFrames];
    private readonly float[] _history = new float[HistorySize];

    private int _delayIndex;
    private float _prevInL, _prevInR, _prevOutL, _prevOutR;
    private int _frameFill;
    private double _frameEnergy;
    private float _decimationSum;
    private int _decimationCount;
    private int _historyIndex;
    private int _historyFilled;
    private float _floorDb;
    private bool _floorKnown;
    private int _holdFrames;
    private float _targetGain = 1f;
    private float _gain = 1f;

    /// <summary>Gain the gate is moving to: 1 while speech is detected, else the closed gain.</summary>
    public float TargetGain => _targetGain;

    /// <summary>The tracked noise floor in dB (RMS, full scale = 0), or -120 before the first frame.</summary>
    public float NoiseFloorDb => _floorKnown ? _floorDb : -120f;

    /// <summary>Forgets all state. Call it when the suppressor is switched on, so old audio is not replayed.</summary>
    public void Reset()
    {
        Array.Clear(_delayL);
        Array.Clear(_delayR);
        Array.Clear(_history);
        _delayIndex = 0;
        _prevInL = _prevInR = _prevOutL = _prevOutR = 0;
        _frameFill = 0;
        _frameEnergy = 0;
        _decimationSum = 0;
        _decimationCount = 0;
        _historyIndex = 0;
        _historyFilled = 0;
        _floorKnown = false;
        _holdFrames = 0;
        _targetGain = 1f;
        _gain = 1f;
    }

    /// <summary>Processes interleaved stereo samples in place. Any length that is a multiple of two works.</summary>
    public void Process(Span<float> stereo)
    {
        for (int i = 0; i + 1 < stereo.Length; i += 2)
        {
            float inL = stereo[i];
            float inR = stereo[i + 1];

            float hpL = HighPassAlpha * (_prevOutL + inL - _prevInL);
            float hpR = HighPassAlpha * (_prevOutR + inR - _prevInR);
            _prevInL = inL;
            _prevInR = inR;
            _prevOutL = hpL;
            _prevOutR = hpR;

            Analyse((hpL + hpR) * 0.5f);

            float delayedL = _delayL[_delayIndex];
            float delayedR = _delayR[_delayIndex];
            _delayL[_delayIndex] = hpL;
            _delayR[_delayIndex] = hpR;
            if (++_delayIndex == LookaheadFrames)
            {
                _delayIndex = 0;
            }

            _gain += (_targetGain - _gain) * (_targetGain > _gain ? AttackCoefficient : ReleaseCoefficient);
            stereo[i] = delayedL * _gain;
            stereo[i + 1] = delayedR * _gain;
        }
    }

    private void Analyse(float mono)
    {
        _frameEnergy += (double)mono * mono;

        _decimationSum += mono;
        if (++_decimationCount == Decimation)
        {
            _history[_historyIndex] = _decimationSum / Decimation;
            _historyIndex = (_historyIndex + 1) % HistorySize;
            if (_historyFilled < HistorySize)
            {
                _historyFilled++;
            }

            _decimationSum = 0;
            _decimationCount = 0;
        }

        if (++_frameFill == FrameSize)
        {
            DecideFrame();
            _frameFill = 0;
            _frameEnergy = 0;
        }
    }

    private void DecideFrame()
    {
        float levelDb = 10f * MathF.Log10((float)(_frameEnergy / FrameSize) + 1e-12f);
        if (!_floorKnown)
        {
            _floorDb = levelDb;
            _floorKnown = true;
        }

        bool voiced = _historyFilled == HistorySize
            && levelDb > _floorDb + VoicedAboveFloorDb
            && Periodicity() > VoicingThreshold;

        if (levelDb < _floorDb)
        {
            _floorDb += (levelDb - _floorDb) * 0.3f;
        }
        else if (!voiced)
        {
            _floorDb = MathF.Min(_floorDb + FloorRiseDbPerFrame, FloorCeilingDb);
        }

        bool loud = levelDb > _floorDb + LoudAboveFloorDb;

        if (voiced)
        {
            _holdFrames = VoicedHoldFrames;
        }
        else if (loud)
        {
            _holdFrames = Math.Max(_holdFrames, LoudHoldFrames);
        }
        else if (_holdFrames > 0)
        {
            _holdFrames--;
        }

        // Until there is enough audio to judge, stay open: nothing is cut at the start.
        _targetGain = _holdFrames > 0 || _historyFilled < HistorySize ? 1f : ClosedGain;
    }

    /// <summary>The strongest normalised autocorrelation over the speech pitch range, 0 to 1.</summary>
    private float Periodicity()
    {
        Span<float> x = stackalloc float[HistorySize];
        for (int n = 0; n < HistorySize; n++)
        {
            x[n] = _history[(_historyIndex + n) % HistorySize];
        }

        float best = 0;
        for (int lag = MinLag; lag <= MaxLag; lag++)
        {
            double cross = 0, energyA = 0, energyB = 0;
            for (int n = lag; n < HistorySize; n++)
            {
                float a = x[n];
                float b = x[n - lag];
                cross += a * b;
                energyA += a * a;
                energyB += b * b;
            }

            double denominator = Math.Sqrt(energyA * energyB);
            if (denominator > 1e-12)
            {
                best = Math.Max(best, (float)(cross / denominator));
            }
        }

        return best;
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
// A port of RNNoise (denoise.c, pitch.c, celt_lpc.c, rnn.c) as found in FFmpeg's af_arnndn.c.
// Copyright (c) 2018 Gregor Richards, (c) 2017 Mozilla, (c) 2005-2009 Xiph.Org Foundation,
// (c) 2007-2008 CSIRO, (c) 2008-2011 Octasic Inc., (c) Jean-Marc Valin, (c) 2019 Paul B Mahol.
// Originally under the BSD-3-Clause license (see THIRD-PARTY-NOTICES.md).
namespace Linkast.Core.Audio.Noise;

/// <summary>
/// RNNoise for one channel of 48 kHz audio: a small recurrent network that estimates, a hundred times a
/// second, how much of each of 22 frequency bands is speech, and keeps only that. Unlike a gate it
/// also cleans the noise under the voice (fans, hum, keyboards, traffic), not only between words.
/// <para>
/// Works on frames of <see cref="FrameSize"/> samples (10 ms) at the scale of 16-bit audio (±32768), as the
/// model was trained. The output is delayed by one frame.
/// </para>
/// </summary>
internal sealed class RnNoiseDenoiser
{
    public const int FrameSize = 480;
    private const int WindowSize = 2 * FrameSize;
    private const int FreqSize = FrameSize + 1;
    private const int PitchMinPeriod = 60;
    private const int PitchMaxPeriod = 768;
    private const int PitchFrameSize = 960;
    private const int PitchBufSize = PitchMaxPeriod + PitchFrameSize;
    private const int NbBands = 22;
    private const int CepsMem = 8;
    private const int NbDeltaCeps = 6;
    private const int NbFeatures = NbBands + 3 * NbDeltaCeps + 2;
    private const float WeightsScale = 1f / 256;
    private const int FrameSizeShift = 2;

    private static readonly byte[] EBand5ms = [0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 34, 40, 48, 60, 78, 100];
    private static readonly int[] SecondCheck = [0, 0, 3, 2, 3, 2, 5, 2, 3, 2, 3, 2, 5, 2, 3, 2];
    private static readonly float[] Window = MakeWindow();
    private static readonly float[] DctTable = MakeDct();
    private static readonly float[] TansigTable = MakeTansigTable();

    private readonly RnNoiseModel _model;
    private readonly ComplexFft _fft = new(WindowSize);

    // Denoise state.
    private readonly float[] _analysisMem = new float[FrameSize];
    private readonly float[][] _cepstralMem = new float[CepsMem][];
    private int _memId;
    private readonly float[] _synthesisMem = new float[FrameSize];
    private readonly float[] _pitchBuf = new float[PitchBufSize];
    private float _lastGain;
    private int _lastPeriod;
    private readonly float[] _memHpX = new float[2];
    private readonly float[] _lastG = new float[NbBands];

    // Network state.
    private readonly float[] _vadGruState;
    private readonly float[] _noiseGruState;
    private readonly float[] _denoiseGruState;

    // Work buffers, kept so a frame allocates nothing.
    private readonly Complex32[] _fftIn = new Complex32[WindowSize];
    private readonly Complex32[] _fftOut = new Complex32[WindowSize];
    private readonly Complex32[] _x = new Complex32[WindowSize];
    private readonly Complex32[] _p = new Complex32[WindowSize];
    private readonly float[] _ex = new float[NbBands];
    private readonly float[] _ep = new float[NbBands];
    private readonly float[] _exp = new float[NbBands];
    private readonly float[] _features = new float[NbFeatures];
    private readonly float[] _gains = new float[NbBands];
    private readonly float[] _gf = new float[FreqSize];
    private readonly float[] _hp = new float[FrameSize];
    private readonly float[] _window = new float[WindowSize];
    private readonly float[] _pitchLp = new float[PitchBufSize >> 1];
    private readonly float[] _denseOut = new float[128];
    private readonly float[] _noiseInput = new float[128 * 3];
    private readonly float[] _denoiseInput = new float[128 * 3];
    private readonly float[] _z = new float[128];
    private readonly float[] _r = new float[128];
    private readonly float[] _h = new float[128];
    private readonly float[] _vad = new float[1];

    public RnNoiseDenoiser(RnNoiseModel? model = null)
    {
        _model = model ?? RnNoiseModel.Default;
        for (var i = 0; i < CepsMem; i++)
        {
            _cepstralMem[i] = new float[NbBands];
        }

        _vadGruState = new float[_model.VadGru.Neurons];
        _noiseGruState = new float[_model.NoiseGru.Neurons];
        _denoiseGruState = new float[_model.DenoiseGru.Neurons];
    }

    /// <summary>Forgets everything heard so far.</summary>
    public void Reset()
    {
        Array.Clear(_analysisMem);
        foreach (var c in _cepstralMem)
        {
            Array.Clear(c);
        }

        _memId = 0;
        Array.Clear(_synthesisMem);
        Array.Clear(_pitchBuf);
        _lastGain = 0;
        _lastPeriod = 0;
        Array.Clear(_memHpX);
        Array.Clear(_lastG);
        Array.Clear(_vadGruState);
        Array.Clear(_noiseGruState);
        Array.Clear(_denoiseGruState);
    }

    /// <summary>
    /// Denoises one frame of <see cref="FrameSize"/> samples (16-bit scale) into <paramref name="output"/>,
    /// which may be the same buffer. Returns how likely the frame is to hold speech, 0 to 1.
    /// </summary>
    public float ProcessFrame(ReadOnlySpan<float> input, Span<float> output)
    {
        if (input.Length < FrameSize || output.Length < FrameSize)
        {
            throw new ArgumentException($"A frame is {FrameSize} samples.");
        }

        Biquad(_hp, _memHpX, input);
        var silence = ComputeFrameFeatures(_hp);
        var vad = 0f;

        if (!silence)
        {
            ComputeRnn(_gains, out vad, _features);
            PitchFilter(_x, _p, _ex, _ep, _exp, _gains);
            for (var i = 0; i < NbBands; i++)
            {
                _gains[i] = MathF.Max(_gains[i], 0.6f * _lastG[i]);
                _lastG[i] = _gains[i];
            }

            InterpBandGain(_gf, _gains);
            for (var i = 0; i < FreqSize; i++)
            {
                _x[i].Re *= _gf[i];
                _x[i].Im *= _gf[i];
            }
        }

        FrameSynthesis(output, _x);
        return vad;
    }

    // ---- Signal processing ------------------------------------------------------------------

    private static void Biquad(Span<float> y, float[] mem, ReadOnlySpan<float> x)
    {
        // b = {-2, 1}, a = {-1.99599, 0.99600}: a high-pass that removes DC and rumble.
        const float b0 = -2f, b1 = 1f, a0 = -1.99599f, a1 = 0.99600f;
        for (var i = 0; i < FrameSize; i++)
        {
            var xi = x[i];
            var yi = x[i] + mem[0];
            mem[0] = mem[1] + (b0 * xi - a0 * yi);
            mem[1] = b1 * xi - a1 * yi;
            y[i] = yi;
        }
    }

    /// <summary>Forward transform scaled by 1/N, as RNNoise's (KISS FFT with scaling).</summary>
    private void ForwardTransform(Complex32[] output, ReadOnlySpan<float> input)
    {
        for (var i = 0; i < WindowSize; i++)
        {
            _fftIn[i] = new Complex32(input[i], 0);
        }

        _fft.Forward(_fftIn, _fftOut);
        const float scale = 1f / WindowSize;
        for (var i = 0; i < FreqSize; i++)
        {
            output[i] = new Complex32(_fftOut[i].Re * scale, _fftOut[i].Im * scale);
        }
    }

    private void InverseTransform(Span<float> output, Complex32[] input)
    {
        for (var i = 0; i < FreqSize; i++)
        {
            _fftIn[i] = input[i];
        }

        for (var i = FreqSize; i < WindowSize; i++)
        {
            _fftIn[i] = new Complex32(_fftIn[WindowSize - i].Re, -_fftIn[WindowSize - i].Im);
        }

        _fft.Inverse(_fftIn, _fftOut);
        for (var i = 0; i < WindowSize; i++)
        {
            output[i] = _fftOut[i].Re;
        }
    }

    private static void ComputeBandEnergy(Span<float> bandE, Complex32[] x)
    {
        Span<float> sum = stackalloc float[NbBands];
        sum.Clear();
        for (var i = 0; i < NbBands - 1; i++)
        {
            var bandSize = (EBand5ms[i + 1] - EBand5ms[i]) << FrameSizeShift;
            var start = EBand5ms[i] << FrameSizeShift;
            for (var j = 0; j < bandSize; j++)
            {
                var frac = (float)j / bandSize;
                var c = x[start + j];
                var tmp = c.Re * c.Re + c.Im * c.Im;
                sum[i] += (1f - frac) * tmp;
                sum[i + 1] += frac * tmp;
            }
        }

        sum[0] *= 2;
        sum[NbBands - 1] *= 2;
        sum.CopyTo(bandE);
    }

    private static void ComputeBandCorr(float[] bandE, Complex32[] x, Complex32[] p)
    {
        Span<float> sum = stackalloc float[NbBands];
        sum.Clear();
        for (var i = 0; i < NbBands - 1; i++)
        {
            var bandSize = (EBand5ms[i + 1] - EBand5ms[i]) << FrameSizeShift;
            var start = EBand5ms[i] << FrameSizeShift;
            for (var j = 0; j < bandSize; j++)
            {
                var frac = (float)j / bandSize;
                var tmp = x[start + j].Re * p[start + j].Re + x[start + j].Im * p[start + j].Im;
                sum[i] += (1 - frac) * tmp;
                sum[i + 1] += frac * tmp;
            }
        }

        sum[0] *= 2;
        sum[NbBands - 1] *= 2;
        sum.CopyTo(bandE);
    }

    private void FrameAnalysis(ReadOnlySpan<float> input)
    {
        _analysisMem.CopyTo(_window, 0);
        input[..FrameSize].CopyTo(_window.AsSpan(FrameSize));
        input[..FrameSize].CopyTo(_analysisMem);
        for (var i = 0; i < WindowSize; i++)
        {
            _window[i] *= Window[i];
        }

        ForwardTransform(_x, _window);
        ComputeBandEnergy(_ex, _x);
    }

    private void FrameSynthesis(Span<float> output, Complex32[] y)
    {
        Span<float> x = stackalloc float[WindowSize];
        InverseTransform(x, y);
        for (var i = 0; i < WindowSize; i++)
        {
            x[i] *= Window[i];
        }

        for (var i = 0; i < FrameSize; i++)
        {
            output[i] = x[i] + _synthesisMem[i];
        }

        x[FrameSize..].CopyTo(_synthesisMem);
    }

    private static float InnerProd(ReadOnlySpan<float> x, ReadOnlySpan<float> y, int n)
    {
        var sum = 0f;
        for (var i = 0; i < n; i++)
        {
            sum += x[i] * y[i];
        }

        return sum;
    }

    private static void PitchXcorr(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> xcorr, int len, int maxPitch)
    {
        for (var i = 0; i < maxPitch; i++)
        {
            xcorr[i] = InnerProd(x, y[i..], len);
        }
    }

    private static void CeltLpc(Span<float> lpc, ReadOnlySpan<float> ac, int p)
    {
        var error = ac[0];
        lpc[..p].Clear();
        if (ac[0] == 0)
        {
            return;
        }

        for (var i = 0; i < p; i++)
        {
            var rr = 0f;
            for (var j = 0; j < i; j++)
            {
                rr += lpc[j] * ac[i - j];
            }

            rr += ac[i + 1];
            var r = -rr / error;
            lpc[i] = r;
            for (var j = 0; j < (i + 1) >> 1; j++)
            {
                var tmp1 = lpc[j];
                var tmp2 = lpc[i - 1 - j];
                lpc[j] = tmp1 + r * tmp2;
                lpc[i - 1 - j] = tmp2 + r * tmp1;
            }

            error -= r * r * error;
            if (error < .001f * ac[0])
            {
                break;
            }
        }
    }

    private static void PitchDownsample(ReadOnlySpan<float> x, Span<float> xLp, int len)
    {
        Span<float> ac = stackalloc float[5];
        Span<float> lpc = stackalloc float[4];
        Span<float> lpc2 = stackalloc float[5];
        var half = len >> 1;

        for (var i = 1; i < half; i++)
        {
            xLp[i] = .5f * (.5f * (x[2 * i - 1] + x[2 * i + 1]) + x[2 * i]);
        }

        xLp[0] = .5f * (.5f * x[1] + x[0]);

        // Autocorrelation, lags 0 to 4.
        for (var k = 0; k <= 4; k++)
        {
            var d = 0f;
            for (var i = k; i < half; i++)
            {
                d += xLp[i] * xLp[i - k];
            }

            ac[k] = d;
        }

        ac[0] *= 1.0001f; // noise floor -40 dB
        for (var i = 1; i <= 4; i++)
        {
            ac[i] -= ac[i] * (.008f * i) * (.008f * i); // lag windowing
        }

        CeltLpc(lpc, ac, 4);
        var tmp = 1f;
        for (var i = 0; i < 4; i++)
        {
            tmp *= .9f;
            lpc[i] *= tmp;
        }

        const float c1 = .8f;
        lpc2[0] = lpc[0] + .8f;
        lpc2[1] = lpc[1] + c1 * lpc[0];
        lpc2[2] = lpc[2] + c1 * lpc[1];
        lpc2[3] = lpc[3] + c1 * lpc[2];
        lpc2[4] = c1 * lpc[3];

        // celt_fir5 in place.
        float mem0 = 0, mem1 = 0, mem2 = 0, mem3 = 0, mem4 = 0;
        for (var i = 0; i < half; i++)
        {
            var sum = xLp[i] + lpc2[0] * mem0 + lpc2[1] * mem1 + lpc2[2] * mem2 + lpc2[3] * mem3 + lpc2[4] * mem4;
            mem4 = mem3;
            mem3 = mem2;
            mem2 = mem1;
            mem1 = mem0;
            mem0 = xLp[i];
            xLp[i] = sum;
        }
    }

    private static float PitchGain(float xy, float xx, float yy) => xy / MathF.Sqrt(1f + xx * yy);

    private static float RemoveDoubling(float[] buf, int maxPeriod, int minPeriod, int n, ref int t0Ref, int prevPeriod, float prevGain)
    {
        var minPeriod0 = minPeriod;
        maxPeriod /= 2;
        minPeriod /= 2;
        t0Ref /= 2;
        prevPeriod /= 2;
        n /= 2;
        var xo = maxPeriod; // x = buf + maxPeriod
        if (t0Ref >= maxPeriod)
        {
            t0Ref = maxPeriod - 1;
        }

        var t = t0Ref;
        var t0 = t0Ref;
        var xx = 0f;
        var xy = 0f;
        for (var i = 0; i < n; i++)
        {
            xx += buf[xo + i] * buf[xo + i];
            xy += buf[xo + i] * buf[xo + i - t0];
        }

        Span<float> yyLookup = stackalloc float[maxPeriod + 1];
        yyLookup[0] = xx;
        var yy = xx;
        for (var i = 1; i <= maxPeriod; i++)
        {
            yy = yy + buf[xo - i] * buf[xo - i] - buf[xo + n - i] * buf[xo + n - i];
            yyLookup[i] = MathF.Max(0, yy);
        }

        yy = yyLookup[t0];
        var bestXy = xy;
        var bestYy = yy;
        var g0 = PitchGain(xy, xx, yy);
        var g = g0;

        for (var k = 2; k <= 15; k++)
        {
            var t1 = (2 * t0 + k) / (2 * k);
            if (t1 < minPeriod)
            {
                break;
            }

            int t1b;
            if (k == 2)
            {
                t1b = t1 + t0 > maxPeriod ? t0 : t0 + t1;
            }
            else
            {
                t1b = (2 * SecondCheck[k] * t0 + k) / (2 * k);
            }

            var xy1 = 0f;
            var xy2 = 0f;
            for (var i = 0; i < n; i++)
            {
                xy1 += buf[xo + i] * buf[xo + i - t1];
                xy2 += buf[xo + i] * buf[xo + i - t1b];
            }

            xy = .5f * (xy1 + xy2);
            yy = .5f * (yyLookup[t1] + yyLookup[t1b]);
            var g1 = PitchGain(xy, xx, yy);
            float cont;
            if (Math.Abs(t1 - prevPeriod) <= 1)
            {
                cont = prevGain;
            }
            else if (Math.Abs(t1 - prevPeriod) <= 2 && 5 * k * k < t0)
            {
                cont = prevGain * .5f;
            }
            else
            {
                cont = 0;
            }

            var thresh = MathF.Max(.3f, .7f * g0 - cont);
            if (t1 < 3 * minPeriod)
            {
                thresh = MathF.Max(.4f, .85f * g0 - cont);
            }
            else if (t1 < 2 * minPeriod)
            {
                thresh = MathF.Max(.5f, .9f * g0 - cont);
            }

            if (g1 > thresh)
            {
                bestXy = xy;
                bestYy = yy;
                t = t1;
                g = g1;
            }
        }

        bestXy = MathF.Max(0, bestXy);
        var pg = bestYy <= bestXy ? 1f : bestXy / (bestYy + 1);

        Span<float> xcorr = stackalloc float[3];
        for (var k = 0; k < 3; k++)
        {
            var s = 0f;
            for (var i = 0; i < n; i++)
            {
                s += buf[xo + i] * buf[xo + i - (t + k - 1)];
            }

            xcorr[k] = s;
        }

        int offset;
        if (xcorr[2] - xcorr[0] > .7f * (xcorr[1] - xcorr[0]))
        {
            offset = 1;
        }
        else if (xcorr[0] - xcorr[2] > .7f * (xcorr[1] - xcorr[2]))
        {
            offset = -1;
        }
        else
        {
            offset = 0;
        }

        if (pg > g)
        {
            pg = g;
        }

        t0Ref = 2 * t + offset;
        if (t0Ref < minPeriod0)
        {
            t0Ref = minPeriod0;
        }

        return pg;
    }

    private static void FindBestPitch(ReadOnlySpan<float> xcorr, ReadOnlySpan<float> y, int len, int maxPitch, Span<int> bestPitch)
    {
        Span<float> bestNum = [-1, -1];
        Span<float> bestDen = [0, 0];
        var syy = 1f;
        bestPitch[0] = 0;
        bestPitch[1] = 1;
        for (var j = 0; j < len; j++)
        {
            syy += y[j] * y[j];
        }

        for (var i = 0; i < maxPitch; i++)
        {
            if (xcorr[i] > 0)
            {
                var xcorr16 = xcorr[i] * 1e-12f;
                var num = xcorr16 * xcorr16;
                if (num * bestDen[1] > bestNum[1] * syy)
                {
                    if (num * bestDen[0] > bestNum[0] * syy)
                    {
                        bestNum[1] = bestNum[0];
                        bestDen[1] = bestDen[0];
                        bestPitch[1] = bestPitch[0];
                        bestNum[0] = num;
                        bestDen[0] = syy;
                        bestPitch[0] = i;
                    }
                    else
                    {
                        bestNum[1] = num;
                        bestDen[1] = syy;
                        bestPitch[1] = i;
                    }
                }
            }

            syy += y[i + len] * y[i + len] - y[i] * y[i];
            syy = MathF.Max(1, syy);
        }
    }

    private static int PitchSearch(ReadOnlySpan<float> xLp, ReadOnlySpan<float> y, int len, int maxPitch)
    {
        var lag = len + maxPitch;
        Span<int> bestPitch = [0, 0];
        Span<float> xLp4 = stackalloc float[len >> 2];
        Span<float> yLp4 = stackalloc float[lag >> 2];
        Span<float> xcorr = stackalloc float[maxPitch >> 1];

        for (var j = 0; j < len >> 2; j++)
        {
            xLp4[j] = xLp[2 * j];
        }

        for (var j = 0; j < lag >> 2; j++)
        {
            yLp4[j] = y[2 * j];
        }

        // Coarse search with 4x decimation.
        PitchXcorr(xLp4, yLp4, xcorr, len >> 2, maxPitch >> 2);
        FindBestPitch(xcorr, yLp4, len >> 2, maxPitch >> 2, bestPitch);

        // Finer search with 2x decimation.
        for (var i = 0; i < maxPitch >> 1; i++)
        {
            xcorr[i] = 0;
            if (Math.Abs(i - 2 * bestPitch[0]) > 2 && Math.Abs(i - 2 * bestPitch[1]) > 2)
            {
                continue;
            }

            xcorr[i] = MathF.Max(-1, InnerProd(xLp, y[i..], len >> 1));
        }

        FindBestPitch(xcorr, y, len >> 1, maxPitch >> 1, bestPitch);

        // Refine by pseudo-interpolation.
        var offset = 0;
        if (bestPitch[0] > 0 && bestPitch[0] < (maxPitch >> 1) - 1)
        {
            var a = xcorr[bestPitch[0] - 1];
            var b = xcorr[bestPitch[0]];
            var c = xcorr[bestPitch[0] + 1];
            if (c - a > .7f * (b - a))
            {
                offset = 1;
            }
            else if (a - c > .7f * (b - c))
            {
                offset = -1;
            }
        }

        return 2 * bestPitch[0] - offset;
    }

    private static void Dct(Span<float> output, ReadOnlySpan<float> input)
    {
        var scale = MathF.Sqrt(2f / 22);
        for (var i = 0; i < NbBands; i++)
        {
            var sum = 0f;
            for (var k = 0; k < NbBands; k++)
            {
                sum += input[k] * DctTable[i * NbBands + k];
            }

            output[i] = sum * scale;
        }
    }

    /// <summary>Fills the features, X, P and the band energies. True when the frame is silent.</summary>
    private bool ComputeFrameFeatures(ReadOnlySpan<float> input)
    {
        var features = _features;
        FrameAnalysis(input);

        Array.Copy(_pitchBuf, FrameSize, _pitchBuf, 0, PitchBufSize - FrameSize);
        input[..FrameSize].CopyTo(_pitchBuf.AsSpan(PitchBufSize - FrameSize));
        PitchDownsample(_pitchBuf, _pitchLp, PitchBufSize);
        var pitchIndex = PitchSearch(_pitchLp.AsSpan(PitchMaxPeriod >> 1), _pitchLp, PitchFrameSize, PitchMaxPeriod - 3 * PitchMinPeriod);
        pitchIndex = PitchMaxPeriod - pitchIndex;

        var gain = RemoveDoubling(_pitchLp, PitchMaxPeriod, PitchMinPeriod, PitchFrameSize, ref pitchIndex, _lastPeriod, _lastGain);
        _lastPeriod = pitchIndex;
        _lastGain = gain;

        for (var i = 0; i < WindowSize; i++)
        {
            _window[i] = _pitchBuf[PitchBufSize - WindowSize - pitchIndex + i] * Window[i];
        }

        ForwardTransform(_p, _window);
        ComputeBandEnergy(_ep, _p);
        ComputeBandCorr(_exp, _x, _p);

        for (var i = 0; i < NbBands; i++)
        {
            _exp[i] /= MathF.Sqrt(.001f + _ex[i] * _ep[i]);
        }

        Span<float> tmp = stackalloc float[NbBands];
        Dct(tmp, _exp);
        for (var i = 0; i < NbDeltaCeps; i++)
        {
            features[NbBands + 2 * NbDeltaCeps + i] = tmp[i];
        }

        features[NbBands + 2 * NbDeltaCeps] -= 1.3f;
        features[NbBands + 2 * NbDeltaCeps + 1] -= 0.9f;
        features[NbBands + 3 * NbDeltaCeps] = .01f * (pitchIndex - 300);

        Span<float> ly = stackalloc float[NbBands];
        var logMax = -2f;
        var follow = -2f;
        var e = 0f;
        for (var i = 0; i < NbBands; i++)
        {
            ly[i] = MathF.Log10(1e-2f + _ex[i]);
            ly[i] = MathF.Max(logMax - 7, MathF.Max(follow - 1.5f, ly[i]));
            logMax = MathF.Max(logMax, ly[i]);
            follow = MathF.Max(follow - 1.5f, ly[i]);
            e += _ex[i];
        }

        if (e < 0.04f)
        {
            // No audio: leave the state alone.
            Array.Clear(features);
            return true;
        }

        Dct(features, ly);
        features[0] -= 12;
        features[1] -= 4;

        var ceps0 = _cepstralMem[_memId];
        var ceps1 = _cepstralMem[_memId < 1 ? CepsMem + _memId - 1 : _memId - 1];
        var ceps2 = _cepstralMem[_memId < 2 ? CepsMem + _memId - 2 : _memId - 2];
        for (var i = 0; i < NbBands; i++)
        {
            ceps0[i] = features[i];
        }

        _memId++;
        for (var i = 0; i < NbDeltaCeps; i++)
        {
            features[i] = ceps0[i] + ceps1[i] + ceps2[i];
            features[NbBands + i] = ceps0[i] - ceps2[i];
            features[NbBands + NbDeltaCeps + i] = ceps0[i] - 2 * ceps1[i] + ceps2[i];
        }

        if (_memId == CepsMem)
        {
            _memId = 0;
        }

        // Spectral variability.
        var specVariability = 0f;
        for (var i = 0; i < CepsMem; i++)
        {
            var minDist = 1e15f;
            for (var j = 0; j < CepsMem; j++)
            {
                if (j == i)
                {
                    continue;
                }

                var dist = 0f;
                for (var k = 0; k < NbBands; k++)
                {
                    var d = _cepstralMem[i][k] - _cepstralMem[j][k];
                    dist += d * d;
                }

                minDist = MathF.Min(minDist, dist);
            }

            specVariability += minDist;
        }

        features[NbBands + 3 * NbDeltaCeps + 1] = specVariability / CepsMem - 2.1f;
        return false;
    }

    private static void InterpBandGain(Span<float> g, ReadOnlySpan<float> bandE)
    {
        g[..FreqSize].Clear();
        for (var i = 0; i < NbBands - 1; i++)
        {
            var bandSize = (EBand5ms[i + 1] - EBand5ms[i]) << FrameSizeShift;
            var start = EBand5ms[i] << FrameSizeShift;
            for (var j = 0; j < bandSize; j++)
            {
                var frac = (float)j / bandSize;
                g[start + j] = (1f - frac) * bandE[i] + frac * bandE[i + 1];
            }
        }
    }

    private static void PitchFilter(Complex32[] x, Complex32[] p, float[] ex, float[] ep, float[] exp, float[] g)
    {
        Span<float> r = stackalloc float[NbBands];
        Span<float> norm = stackalloc float[NbBands];
        Span<float> newE = stackalloc float[NbBands];
        Span<float> rf = stackalloc float[FreqSize];
        Span<float> normf = stackalloc float[FreqSize];

        for (var i = 0; i < NbBands; i++)
        {
            float ri;
            if (exp[i] > g[i])
            {
                ri = 1;
            }
            else
            {
                ri = (float)(exp[i] * exp[i] * (1 - g[i] * g[i]) / (.001 + g[i] * g[i] * (1 - exp[i] * exp[i])));
            }

            ri = MathF.Sqrt(Math.Clamp(ri, 0f, 1f));
            ri *= (float)Math.Sqrt(ex[i] / (1e-8 + ep[i]));
            r[i] = ri;
        }

        InterpBandGain(rf, r);
        for (var i = 0; i < FreqSize; i++)
        {
            x[i].Re += rf[i] * p[i].Re;
            x[i].Im += rf[i] * p[i].Im;
        }

        ComputeBandEnergy(newE, x);
        for (var i = 0; i < NbBands; i++)
        {
            norm[i] = (float)Math.Sqrt(ex[i] / (1e-8 + newE[i]));
        }

        InterpBandGain(normf, norm);
        for (var i = 0; i < FreqSize; i++)
        {
            x[i].Re *= normf[i];
            x[i].Im *= normf[i];
        }
    }

    // ---- Network ------------------------------------------------------------------------------

    private static float TansigApprox(float x)
    {
        // Tests are reversed to catch NaNs.
        if (!(x < 8))
        {
            return 1;
        }

        if (!(x > -8))
        {
            return -1;
        }

        var sign = 1f;
        if (x < 0)
        {
            x = -x;
            sign = -1;
        }

        var i = (int)MathF.Floor(.5f + 25 * x);
        x -= .04f * i;
        var y = TansigTable[i];
        var dy = 1 - y * y;
        y += x * dy * (1 - y * x);
        return sign * y;
    }

    private static float SigmoidApprox(float x) => .5f + .5f * TansigApprox(.5f * x);

    private static float Activate(RnnActivation activation, float x) => activation switch
    {
        RnnActivation.Sigmoid => SigmoidApprox(x),
        RnnActivation.Tanh => TansigApprox(x),
        _ => MathF.Max(0, x),
    };

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var n = b.Length;
        var i = 0;
        var sum = 0f;
        if (System.Numerics.Vector.IsHardwareAccelerated && n >= System.Numerics.Vector<float>.Count)
        {
            var acc = System.Numerics.Vector<float>.Zero;
            var width = System.Numerics.Vector<float>.Count;
            for (; i <= n - width; i += width)
            {
                acc += new System.Numerics.Vector<float>(a.Slice(i, width)) * new System.Numerics.Vector<float>(b.Slice(i, width));
            }

            sum = System.Numerics.Vector.Sum(acc);
        }

        for (; i < n; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    private static void ComputeDense(DenseLayer layer, Span<float> output, ReadOnlySpan<float> input)
    {
        var m = layer.Inputs;
        var x = input[..m];
        for (var i = 0; i < layer.Neurons; i++)
        {
            var sum = layer.Bias[i] + Dot(layer.Weights.AsSpan(i * m, m), x);
            output[i] = Activate(layer.Activation, WeightsScale * sum);
        }
    }

    private void ComputeGru(GruLayer gru, float[] state, ReadOnlySpan<float> input)
    {
        var m = gru.Inputs;
        var n = gru.Neurons;
        var x = input[..m];
        var s = state.AsSpan(0, n);
        var iw = gru.InputWeights;
        var rw = gru.RecurrentWeights;

        for (var i = 0; i < n; i++)
        {
            var sum = gru.Bias[i] + Dot(iw.AsSpan(i * m, m), x) + Dot(rw.AsSpan(i * n, n), s);
            _z[i] = SigmoidApprox(WeightsScale * sum);
        }

        for (var i = 0; i < n; i++)
        {
            var sum = gru.Bias[n + i] + Dot(iw.AsSpan((n + i) * m, m), x) + Dot(rw.AsSpan((n + i) * n, n), s);
            _r[i] = SigmoidApprox(WeightsScale * sum);
        }

        Span<float> rs = stackalloc float[n];
        for (var j = 0; j < n; j++)
        {
            rs[j] = state[j] * _r[j];
        }

        for (var i = 0; i < n; i++)
        {
            var sum = gru.Bias[2 * n + i] + Dot(iw.AsSpan((2 * n + i) * m, m), x) + Dot(rw.AsSpan((2 * n + i) * n, n), rs);
            sum = Activate(gru.Activation, WeightsScale * sum);
            _h[i] = _z[i] * state[i] + (1f - _z[i]) * sum;
        }

        _h.AsSpan(0, n).CopyTo(state);
    }

    private void ComputeRnn(float[] gains, out float vad, float[] input)
    {
        var model = _model;
        var inputSize = input.Length; // 42
        ComputeDense(model.InputDense, _denseOut, input);
        ComputeGru(model.VadGru, _vadGruState, _denseOut);
        ComputeDense(model.VadOutput, _vad, _vadGruState);
        vad = _vad[0];

        var dense = model.InputDense.Neurons;
        var vadSize = model.VadGru.Neurons;
        var noiseSize = model.NoiseGru.Neurons;
        _denseOut.AsSpan(0, dense).CopyTo(_noiseInput);
        _vadGruState.AsSpan(0, vadSize).CopyTo(_noiseInput.AsSpan(dense));
        input.AsSpan(0, inputSize).CopyTo(_noiseInput.AsSpan(dense + vadSize));
        ComputeGru(model.NoiseGru, _noiseGruState, _noiseInput);

        _vadGruState.AsSpan(0, vadSize).CopyTo(_denoiseInput);
        _noiseGruState.AsSpan(0, noiseSize).CopyTo(_denoiseInput.AsSpan(vadSize));
        input.AsSpan(0, inputSize).CopyTo(_denoiseInput.AsSpan(vadSize + noiseSize));
        ComputeGru(model.DenoiseGru, _denoiseGruState, _denoiseInput);
        ComputeDense(model.DenoiseOutput, gains, _denoiseGruState);
    }

    // ---- Tables -------------------------------------------------------------------------------

    private static float[] MakeWindow()
    {
        var w = new float[WindowSize];
        for (var i = 0; i < FrameSize; i++)
        {
            var s = Math.Sin(.5 * Math.PI * (i + .5) / FrameSize);
            w[i] = (float)Math.Sin(.5 * Math.PI * s * s);
            w[WindowSize - 1 - i] = w[i];
        }

        return w;
    }

    /// <summary>DctTable[i * 22 + k] = cos((k + .5)·i·π/22), the first row scaled by √½.</summary>
    private static float[] MakeDct()
    {
        var t = new float[NbBands * NbBands];
        for (var i = 0; i < NbBands; i++)
        {
            for (var k = 0; k < NbBands; k++)
            {
                var v = MathF.Cos((float)((k + .5) * i * Math.PI / NbBands));
                if (i == 0)
                {
                    v *= MathF.Sqrt(.5f);
                }

                t[i * NbBands + k] = v;
            }
        }

        return t;
    }

    /// <summary>tanh at steps of 0.04 from 0 to 8 (201 values), as RNNoise's table.</summary>
    private static float[] MakeTansigTable()
    {
        var t = new float[201];
        for (var i = 0; i < t.Length; i++)
        {
            // RNNoise's table holds tanh rounded to six decimals.
            t[i] = (float)Math.Round(Math.Tanh(i * 0.04), 6);
        }

        return t;
    }
}

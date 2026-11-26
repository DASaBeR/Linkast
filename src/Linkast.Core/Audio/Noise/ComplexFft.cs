// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core.Audio.Noise;

/// <summary>A complex number in single precision, laid out as two floats.</summary>
internal struct Complex32
{
    public float Re;
    public float Im;

    public Complex32(float re, float im)
    {
        Re = re;
        Im = im;
    }
}

/// <summary>
/// Mixed-radix complex FFT (radix 2, 3, 4 and 5) for sizes such as 960 that are not a power of two.
/// The same decomposition as KISS FFT: recursive decimation in time with precomputed twiddles.
/// <see cref="Forward"/> is unscaled; <see cref="Inverse"/> is the unscaled inverse transform.
/// </summary>
internal sealed class ComplexFft
{
    private readonly int _n;
    private readonly int[] _factors;
    private readonly Complex32[] _twiddles;
    private readonly Complex32[] _scratch;

    public ComplexFft(int n)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 2);
        _n = n;
        _factors = Factor(n);
        _twiddles = new Complex32[n];
        for (var i = 0; i < n; i++)
        {
            var phase = -2.0 * Math.PI * i / n;
            _twiddles[i] = new Complex32((float)Math.Cos(phase), (float)Math.Sin(phase));
        }

        _scratch = new Complex32[n];
    }

    public int Size => _n;

    /// <summary>X[k] = Σ x[n]·e^(−2πikn/N). <paramref name="output"/> must not overlap <paramref name="input"/>.</summary>
    public void Forward(ReadOnlySpan<Complex32> input, Span<Complex32> output)
    {
        if (input.Length < _n || output.Length < _n)
        {
            throw new ArgumentException("The buffers are smaller than the transform.");
        }

        Work(output, input, 0, 1, 0);
    }

    /// <summary>x[n] = Σ X[k]·e^(+2πikn/N), unscaled: conj(FFT(conj(X))).</summary>
    public void Inverse(ReadOnlySpan<Complex32> input, Span<Complex32> output)
    {
        if (input.Length < _n || output.Length < _n)
        {
            throw new ArgumentException("The buffers are smaller than the transform.");
        }

        var conj = _scratch;
        for (var i = 0; i < _n; i++)
        {
            conj[i] = new Complex32(input[i].Re, -input[i].Im);
        }

        Work(output, conj, 0, 1, 0);
        for (var i = 0; i < _n; i++)
        {
            output[i].Im = -output[i].Im;
        }
    }

    private static int[] Factor(int n)
    {
        var factors = new List<int>();
        var p = 4;
        var floorSqrt = (int)Math.Floor(Math.Sqrt(n));
        do
        {
            while (n % p != 0)
            {
                p = p switch { 4 => 2, 2 => 3, _ => p + 2 };
                if (p > floorSqrt)
                {
                    p = n;
                }
            }

            if (p is not (2 or 3 or 4 or 5))
            {
                throw new ArgumentException($"Only sizes made of the factors 2, 3 and 5 are supported (found {p}).");
            }

            n /= p;
            factors.Add(p);
            factors.Add(n);
        }
        while (n > 1);

        return [.. factors];
    }

    private void Work(Span<Complex32> fout, ReadOnlySpan<Complex32> f, int fOffset, int fstride, int factorIndex)
    {
        var p = _factors[factorIndex];
        var m = _factors[factorIndex + 1];

        if (m == 1)
        {
            for (var i = 0; i < p; i++)
            {
                fout[i] = f[fOffset + i * fstride];
            }
        }
        else
        {
            for (var i = 0; i < p; i++)
            {
                Work(fout.Slice(i * m, m), f, fOffset + i * fstride, fstride * p, factorIndex + 2);
            }
        }

        switch (p)
        {
            case 2: Butterfly2(fout, fstride, m); break;
            case 3: Butterfly3(fout, fstride, m); break;
            case 4: Butterfly4(fout, fstride, m); break;
            default: Butterfly5(fout, fstride, m); break;
        }
    }

    private static Complex32 Mul(Complex32 a, Complex32 b) => new(a.Re * b.Re - a.Im * b.Im, a.Re * b.Im + a.Im * b.Re);

    private void Butterfly2(Span<Complex32> fout, int fstride, int m)
    {
        for (var k = 0; k < m; k++)
        {
            var t = Mul(fout[k + m], _twiddles[k * fstride]);
            fout[k + m] = new Complex32(fout[k].Re - t.Re, fout[k].Im - t.Im);
            fout[k] = new Complex32(fout[k].Re + t.Re, fout[k].Im + t.Im);
        }
    }

    private void Butterfly3(Span<Complex32> fout, int fstride, int m)
    {
        var epi3 = _twiddles[fstride * m];
        for (var k = 0; k < m; k++)
        {
            var s1 = Mul(fout[k + m], _twiddles[k * fstride]);
            var s2 = Mul(fout[k + 2 * m], _twiddles[2 * k * fstride]);
            var s3 = new Complex32(s1.Re + s2.Re, s1.Im + s2.Im);
            var s0 = new Complex32(s1.Re - s2.Re, s1.Im - s2.Im);

            var a = fout[k];
            var bRe = a.Re - 0.5f * s3.Re;
            var bIm = a.Im - 0.5f * s3.Im;
            s0 = new Complex32(s0.Re * epi3.Im, s0.Im * epi3.Im);
            fout[k] = new Complex32(a.Re + s3.Re, a.Im + s3.Im);
            fout[k + 2 * m] = new Complex32(bRe + s0.Im, bIm - s0.Re);
            fout[k + m] = new Complex32(bRe - s0.Im, bIm + s0.Re);
        }
    }

    private void Butterfly4(Span<Complex32> fout, int fstride, int m)
    {
        for (var k = 0; k < m; k++)
        {
            var s0 = Mul(fout[k + m], _twiddles[k * fstride]);
            var s1 = Mul(fout[k + 2 * m], _twiddles[2 * k * fstride]);
            var s2 = Mul(fout[k + 3 * m], _twiddles[3 * k * fstride]);

            var a = fout[k];
            var s5 = new Complex32(a.Re - s1.Re, a.Im - s1.Im);
            a = new Complex32(a.Re + s1.Re, a.Im + s1.Im);
            var s3 = new Complex32(s0.Re + s2.Re, s0.Im + s2.Im);
            var s4 = new Complex32(s0.Re - s2.Re, s0.Im - s2.Im);

            fout[k + 2 * m] = new Complex32(a.Re - s3.Re, a.Im - s3.Im);
            fout[k] = new Complex32(a.Re + s3.Re, a.Im + s3.Im);
            fout[k + m] = new Complex32(s5.Re + s4.Im, s5.Im - s4.Re);
            fout[k + 3 * m] = new Complex32(s5.Re - s4.Im, s5.Im + s4.Re);
        }
    }

    private void Butterfly5(Span<Complex32> fout, int fstride, int m)
    {
        var ya = _twiddles[fstride * m];
        var yb = _twiddles[fstride * 2 * m];
        for (var u = 0; u < m; u++)
        {
            var s0 = fout[u];
            var s1 = Mul(fout[u + m], _twiddles[u * fstride]);
            var s2 = Mul(fout[u + 2 * m], _twiddles[2 * u * fstride]);
            var s3 = Mul(fout[u + 3 * m], _twiddles[3 * u * fstride]);
            var s4 = Mul(fout[u + 4 * m], _twiddles[4 * u * fstride]);

            var s7 = new Complex32(s1.Re + s4.Re, s1.Im + s4.Im);
            var s10 = new Complex32(s1.Re - s4.Re, s1.Im - s4.Im);
            var s8 = new Complex32(s2.Re + s3.Re, s2.Im + s3.Im);
            var s9 = new Complex32(s2.Re - s3.Re, s2.Im - s3.Im);

            fout[u] = new Complex32(s0.Re + s7.Re + s8.Re, s0.Im + s7.Im + s8.Im);

            var s5 = new Complex32(s0.Re + s7.Re * ya.Re + s8.Re * yb.Re, s0.Im + s7.Im * ya.Re + s8.Im * yb.Re);
            var s6 = new Complex32(s10.Im * ya.Im + s9.Im * yb.Im, -(s10.Re * ya.Im) - s9.Re * yb.Im);
            fout[u + m] = new Complex32(s5.Re - s6.Re, s5.Im - s6.Im);
            fout[u + 4 * m] = new Complex32(s5.Re + s6.Re, s5.Im + s6.Im);

            var s11 = new Complex32(s0.Re + s7.Re * yb.Re + s8.Re * ya.Re, s0.Im + s7.Im * yb.Re + s8.Im * ya.Re);
            var s12 = new Complex32(-(s10.Im * yb.Im) + s9.Im * ya.Im, s10.Re * yb.Im - s9.Re * ya.Im);
            fout[u + 2 * m] = new Complex32(s11.Re + s12.Re, s11.Im + s12.Im);
            fout[u + 3 * m] = new Complex32(s11.Re - s12.Re, s11.Im - s12.Im);
        }
    }
}

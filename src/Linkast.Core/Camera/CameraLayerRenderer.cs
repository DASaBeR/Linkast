// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core.Camera;

/// <summary>
/// Renders a camera frame into a finished BGRA layer (straight alpha): crop and zoom, mirror,
/// brightness/contrast/saturation, light temporal denoise, sharpen, shape mask and border.
/// The same renderer feeds the preview and the stream, so both always match.
/// Not thread-safe: use one instance per consumer. Buffers are reused between frames.
/// </summary>
public sealed class CameraLayerRenderer
{
    private const int Fp = 16; // fixed-point fraction bits for sampling coordinates

    private float[] _dist = [];
    private double _glowMargin;
    private int _maskW, _maskH;
    private (LayerShape, double, double) _maskKey;

    private readonly byte[] _lut = new byte[256];
    private (double, double) _lutKey = (double.NaN, double.NaN);

    private int[] _xMap = [];
    private byte[] _work = [];
    private byte[] _previous = [];
    private bool _hasPrevious;

    /// <summary>Processing path, for diagnostics.</summary>
    public static string ProcessingPath => "CPU";

    /// <summary>Renders <paramref name="source"/> into <paramref name="destination"/> (width × height × 4 bytes).</summary>
    /// <param name="segmentation">Person mask for <paramref name="source"/>; needed for background effects. Null shows the image unprocessed.</param>
    /// <param name="backgroundImage">Image for <see cref="BackgroundMode.Image"/>; null falls back to blur.</param>
    /// <param name="context">Time and audio level for animated and audio-reactive effects.</param>
    /// <param name="framing">Auto-framing crop; overrides manual crop and zoom when framing is enabled.</param>
    public void Render(BgraFrame source, CameraLayerSettings settings, byte[] destination, int width, int height,
        SegmentationMask? segmentation = null, BgraFrame? backgroundImage = null,
        FrameContext context = default, FramingCrop? framing = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, width * height * 4);

        var s = settings.Clamped();
        if (s.Framing.Enabled && framing is { } crop)
        {
            var margin = GlowMargin(s.Effects, width, height);
            var aspect = (width - (2 * margin)) / Math.Max(1, height - (2 * margin));
            s = s with
            {
                // Source coordinates: mirroring flips only the output, never the crop position.
                CenterX = crop.CenterX,
                CenterY = crop.CenterY,
                Zoom = crop.ToZoom(source.Width, source.Height, aspect, s.Framing.MaxZoom),
            };
        }
        EnsureMask(s, width, height);
        EnsureLut(s.Adjustments, LowLightGamma(source, s.Adjustments.LowLightBoost));
        Sample(source, s, destination, width, height);

        var background = s.Background;
        var usePerson = background.NeedsSegmentation && segmentation is not null;
        if (usePerson)
        {
            SamplePerson(segmentation!, source.Width, source.Height, width, height, background.EdgeSoftness);
        }

        if (s.Adjustments.Denoise > 0)
        {
            Denoise(destination, width, height, s.Adjustments.Denoise);
        }
        else
        {
            _hasPrevious = false;
        }

        if (s.Adjustments.Sharpen > 0)
        {
            Sharpen(destination, width, height, s.Adjustments.Sharpen);
        }

        if (usePerson)
        {
            switch (background.Mode)
            {
                case BackgroundMode.Image when backgroundImage is not null:
                    CompositeOverImage(destination, width, height, backgroundImage);
                    break;
                case BackgroundMode.Blur:
                case BackgroundMode.Image: // image missing: blur is the safe fallback
                    CompositeOverBlur(destination, width, height, background.BlurStrength);
                    break;
            }
        }

        ApplyMask(destination, width, height, s, usePerson && background.Mode == BackgroundMode.Remove, context);
    }

    // ---- Background effects ----------------------------------------------------------------

    private byte[] _person = [];
    private byte[] _personTmp = [];
    private int[] _yMap = [];
    private int[] _maskCol0 = [];
    private int[] _maskColFx = [];
    private float[] _smallColor = [];
    private float[] _smallWeight = [];
    private float[] _smallTmp = [];
    private BgraFrame? _imageSource;
    private byte[] _imageScaled = [];
    private (int W, int H) _imageScaledSize;

    /// <summary>Person alpha per output pixel, sampled from the mask at the same source position as the colour.</summary>
    private void SamplePerson(SegmentationMask mask, int srcW, int srcH, int w, int h, double softness)
    {
        var n = w * h;
        if (_person.Length != n)
        {
            _person = new byte[n];
            _personTmp = new byte[n];
        }

        // xMap / yMap hold source coordinates in 16.16 fixed point; scale them into mask space.
        // Column positions are the same for every row, so they are computed once.
        var mx = (double)mask.Width / srcW;
        var my = (double)mask.Height / srcH;
        var alpha = mask.Alpha;
        var mw = mask.Width;
        if (_maskCol0.Length != w)
        {
            _maskCol0 = new int[w];
            _maskColFx = new int[w];
        }

        for (var x = 0; x < w; x++)
        {
            var sx = Math.Max(0, (((_xMap[x] / 65536.0) + 0.5) * mx) - 0.5);
            var x0 = Math.Min((int)sx, mw - 1);
            _maskCol0[x] = x0;
            _maskColFx[x] = x0 >= mw - 1 ? 0 : Math.Clamp((int)((sx - x0) * 256), 0, 256);
        }

        for (var y = 0; y < h; y++)
        {
            var sy = Math.Max(0, (((_yMap[y] / 65536.0) + 0.5) * my) - 0.5);
            var y0 = Math.Min((int)sy, mask.Height - 1);
            var row0 = y0 * mw;
            var row1 = Math.Min(y0 + 1, mask.Height - 1) * mw;
            var fy = Math.Clamp((int)((sy - y0) * 256), 0, 256);
            var o = y * w;
            for (var x = 0; x < w; x++)
            {
                var x0 = _maskCol0[x];
                var fx = _maskColFx[x];
                var x1 = fx == 0 ? x0 : x0 + 1;
                var top = (alpha[row0 + x0] * (256 - fx)) + (alpha[row0 + x1] * fx);
                var bottom = (alpha[row1 + x0] * (256 - fx)) + (alpha[row1 + x1] * fx);
                _person[o + x] = (byte)(((top * (256 - fy)) + (bottom * fy)) >> 16);
            }
        }

        var radius = softness <= 0 ? 0 : 1 + (int)Math.Round(softness * 0.012 * Math.Min(w, h));
        if (radius > 0)
        {
            BoxBlurBytes(_person, _personTmp, w, h, radius);
        }
    }

    /// <summary>Background blur that excludes the person, so their colours don't glow around the outline.</summary>
    private void CompositeOverBlur(byte[] img, int w, int h, double strength)
    {
        const int f = 4; // blur at quarter resolution: same look, ~16x cheaper
        int sw = Math.Max(1, w / f), sh = Math.Max(1, h / f), sn = sw * sh;
        if (_smallWeight.Length != sn)
        {
            _smallColor = new float[sn * 3];
            _smallWeight = new float[sn];
            _smallTmp = new float[Math.Max(sn * 3, sn)];
        }

        Array.Clear(_smallColor);
        Array.Clear(_smallWeight);
        for (var y = 0; y < sh * f; y++)
        {
            var row = (y / f) * sw;
            for (var x = 0; x < sw * f; x++)
            {
                var p = (y * w) + x;
                var bgw = 255 - _person[p];
                var s = row + (x / f);
                var o = p * 4;
                _smallColor[(s * 3) + 0] += img[o] * bgw;
                _smallColor[(s * 3) + 1] += img[o + 1] * bgw;
                _smallColor[(s * 3) + 2] += img[o + 2] * bgw;
                _smallWeight[s] += bgw;
            }
        }

        // Two box passes approximate a Gaussian closely enough at this scale.
        var radius = 1 + (int)Math.Round(strength * 12);
        for (var pass = 0; pass < 2; pass++)
        {
            BoxBlurFloat(_smallColor, 3, sw, sh, radius);
            BoxBlurFloat(_smallWeight, 1, sw, sh, radius);
        }

        for (var y = 0; y < h; y++)
        {
            var sy = Math.Max(0, ((y + 0.5f) / f) - 0.5f);
            var y0 = Math.Min((int)sy, sh - 1);
            var y1 = Math.Min(y0 + 1, sh - 1);
            var ty = sy - y0;
            for (var x = 0; x < w; x++)
            {
                var p = (y * w) + x;
                var a = _person[p];
                if (a == 255)
                {
                    continue;
                }

                var sx = Math.Max(0, ((x + 0.5f) / f) - 0.5f);
                var x0 = Math.Min((int)sx, sw - 1);
                var x1 = Math.Min(x0 + 1, sw - 1);
                var tx = sx - x0;
                int i00 = (y0 * sw) + x0, i01 = (y0 * sw) + x1, i10 = (y1 * sw) + x0, i11 = (y1 * sw) + x1;
                var wsum = Lerp(Lerp(_smallWeight[i00], _smallWeight[i01], tx), Lerp(_smallWeight[i10], _smallWeight[i11], tx), ty);
                var o = p * 4;
                for (var c = 0; c < 3; c++)
                {
                    var csum = Lerp(Lerp(_smallColor[(i00 * 3) + c], _smallColor[(i01 * 3) + c], tx), Lerp(_smallColor[(i10 * 3) + c], _smallColor[(i11 * 3) + c], tx), ty);

                    // Where only person pixels are nearby (weight ~0), fall back to the pixel itself.
                    var bg = wsum > 1f ? csum / wsum : img[o + c];
                    img[o + c] = (byte)Math.Clamp(bg + ((img[o + c] - bg) * a / 255f), 0, 255);
                }
            }
        }
    }

    private void CompositeOverImage(byte[] img, int w, int h, BgraFrame background)
    {
        if (!ReferenceEquals(_imageSource, background) || _imageScaledSize != (w, h))
        {
            // Scale to cover (no letterboxing), centred, once per image and size.
            _imageScaled = new byte[w * h * 4];
            var scale = Math.Max((double)w / background.Width, (double)h / background.Height);
            var offX = ((background.Width * scale) - w) / 2;
            var offY = ((background.Height * scale) - h) / 2;
            for (var y = 0; y < h; y++)
            {
                var sy = Math.Clamp((int)((y + offY) / scale), 0, background.Height - 1);
                for (var x = 0; x < w; x++)
                {
                    var sx = Math.Clamp((int)((x + offX) / scale), 0, background.Width - 1);
                    Buffer.BlockCopy(background.Pixels, ((sy * background.Width) + sx) * 4, _imageScaled, ((y * w) + x) * 4, 4);
                }
            }

            _imageSource = background;
            _imageScaledSize = (w, h);
        }

        for (var p = 0; p < w * h; p++)
        {
            var a = _person[p];
            if (a == 255)
            {
                continue;
            }

            var o = p * 4;
            for (var c = 0; c < 3; c++)
            {
                img[o + c] = (byte)(_imageScaled[o + c] + (((img[o + c] - _imageScaled[o + c]) * a) / 255));
            }
        }
    }

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);

    /// <summary>Separable box blur on a byte plane (running sums).</summary>
    private static void BoxBlurBytes(byte[] data, byte[] tmp, int w, int h, int r)
    {
        var div = (2 * r) + 1;
        for (var y = 0; y < h; y++)
        {
            var row = y * w;
            var sum = 0;
            for (var i = -r; i <= r; i++)
            {
                sum += data[row + Math.Clamp(i, 0, w - 1)];
            }

            for (var x = 0; x < w; x++)
            {
                tmp[row + x] = (byte)(sum / div);
                sum += data[row + Math.Min(x + r + 1, w - 1)] - data[row + Math.Max(x - r, 0)];
            }
        }

        for (var x = 0; x < w; x++)
        {
            var sum = 0;
            for (var i = -r; i <= r; i++)
            {
                sum += tmp[(Math.Clamp(i, 0, h - 1) * w) + x];
            }

            for (var y = 0; y < h; y++)
            {
                data[(y * w) + x] = (byte)(sum / div);
                sum += tmp[(Math.Min(y + r + 1, h - 1) * w) + x] - tmp[(Math.Max(y - r, 0) * w) + x];
            }
        }
    }

    /// <summary>Separable box blur on interleaved float channels.</summary>
    private void BoxBlurFloat(float[] data, int channels, int w, int h, int r)
    {
        var div = (2 * r) + 1f;
        var tmp = _smallTmp;
        for (var c = 0; c < channels; c++)
        {
            for (var y = 0; y < h; y++)
            {
                float sum = 0;
                for (var i = -r; i <= r; i++)
                {
                    sum += data[((y * w) + Math.Clamp(i, 0, w - 1)) * channels + c];
                }

                for (var x = 0; x < w; x++)
                {
                    tmp[((y * w) + x) * channels + c] = sum / div;
                    sum += data[((y * w) + Math.Min(x + r + 1, w - 1)) * channels + c] - data[((y * w) + Math.Max(x - r, 0)) * channels + c];
                }
            }

            for (var x = 0; x < w; x++)
            {
                float sum = 0;
                for (var i = -r; i <= r; i++)
                {
                    sum += tmp[((Math.Clamp(i, 0, h - 1) * w) + x) * channels + c];
                }

                for (var y = 0; y < h; y++)
                {
                    data[((y * w) + x) * channels + c] = sum / div;
                    sum += tmp[((Math.Min(y + r + 1, h - 1) * w) + x) * channels + c] - tmp[((Math.Max(y - r, 0) * w) + x) * channels + c];
                }
            }
        }
    }

    /// <summary>Fills the layer with fully transparent pixels (camera off or unavailable).</summary>
    public static void RenderTransparent(byte[] destination, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(destination);
        Array.Clear(destination, 0, width * height * 4);
    }

    /// <summary>Resets temporal state, for example after switching cameras.</summary>
    public void Reset() => _hasPrevious = false;

    /// <summary>Alpha of the shape mask at a pixel (0 to 255). Exposed for tests and hit-testing.</summary>
    public static byte MaskAlpha(LayerShape shape, double cornerRadius, int width, int height, int x, int y) =>
        (byte)Math.Round(Math.Clamp(0.5 - SignedDistance(shape, cornerRadius, 0, width, height, x, y), 0, 1) * 255);

    /// <summary>Space reserved around the shape for a glow, in pixels (0 when no glow effect is on).</summary>
    public static double GlowMargin(LayerEffectSettings fx, int w, int h) =>
        fx?.NeedsGlowSpace == true ? 0.08 * Math.Min(w, h) : 0;

    /// <summary>
    /// Precomputes the signed distance to the shape edge for every pixel (negative inside). Border
    /// width, glow and animations are then cheap per-frame functions of this field.
    /// </summary>
    private void EnsureMask(CameraLayerSettings s, int w, int h)
    {
        var inset = GlowMargin(s.Effects, w, h);
        var key = (s.Shape, s.CornerRadius, inset);
        if (_maskW == w && _maskH == h && _maskKey == key)
        {
            return;
        }

        _dist = new float[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                _dist[(y * w) + x] = (float)SignedDistance(s.Shape, s.CornerRadius, inset, w, h, x, y);
            }
        }

        _maskW = w;
        _maskH = h;
        _maskKey = key;
        _glowMargin = inset;
        _hasPrevious = false;
    }

    /// <summary>Signed distance from a pixel centre to a rounded rectangle inset by <paramref name="inset"/> pixels.</summary>
    private static double SignedDistance(LayerShape shape, double cornerRadius, double inset, int w, int h, int x, int y)
    {
        var sw = w - (2 * inset);
        var sh = h - (2 * inset);
        var min = Math.Min(sw, sh);
        var r = shape switch
        {
            LayerShape.Circle => min / 2.0,
            LayerShape.RoundedRectangle => cornerRadius * min,
            _ => 0,
        };
        var px = Math.Abs(x + 0.5 - (w / 2.0)) - ((sw / 2.0) - r);
        var py = Math.Abs(y + 0.5 - (h / 2.0)) - ((sh / 2.0) - r);
        var outside = Math.Sqrt((Math.Max(px, 0) * Math.Max(px, 0)) + (Math.Max(py, 0) * Math.Max(py, 0)));
        return outside + Math.Min(Math.Max(px, py), 0) - r;
    }

    private double _smoothedGamma = 1;

    /// <summary>
    /// Gamma that lifts the frame's average brightness toward a normal exposure. Gamma (rather than
    /// a plain gain) brightens shadows most and keeps highlights from clipping. Smoothed over time so
    /// the picture does not pump when someone moves.
    /// </summary>
    private double LowLightGamma(BgraFrame src, double boost)
    {
        if (boost <= 0)
        {
            _smoothedGamma = 1;
            return 1;
        }

        // Sparse luma average: every 16th pixel is plenty for exposure.
        long sum = 0;
        var n = 0;
        var px = src.Pixels;
        for (var i = 0; i < px.Length; i += 64)
        {
            sum += ((77 * px[i + 2]) + (150 * px[i + 1]) + (29 * px[i])) >> 8;
            n++;
        }

        var mean = Math.Max(1, sum / (double)Math.Max(1, n)) / 255.0;
        const double target = 0.42;
        var gamma = mean >= target ? 1 : Math.Log(target) / Math.Log(mean); // < 1 brightens
        gamma = 1 - ((1 - Math.Clamp(gamma, 0.4, 1)) * boost);
        _smoothedGamma += (gamma - _smoothedGamma) * 0.1;
        return Math.Round(_smoothedGamma, 3);
    }

    private void EnsureLut(CameraAdjustments a, double gamma = 1)
    {
        if (_lutKey == (a.Brightness, a.Contrast) && _lutGamma == gamma)
        {
            return;
        }

        for (var i = 0; i < 256; i++)
        {
            var g = Math.Pow(i / 255.0, gamma);
            var v = ((g - 0.5) * a.Contrast) + 0.5 + (a.Brightness * 0.5);
            _lut[i] = (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);
        }

        _lutKey = (a.Brightness, a.Contrast);
        _lutGamma = gamma;
    }

    private double _lutGamma = 1;

    private void Sample(BgraFrame src, CameraLayerSettings s, byte[] dst, int w, int h)
    {
        var (cx, cy, cw, ch) = LayerGeometry.SourceCrop(src.Width, src.Height, w / (double)h, s.Zoom, s.CenterX, s.CenterY);
        if (_xMap.Length != w)
        {
            _xMap = new int[w];
        }

        var maxX = (src.Width - 1) << Fp;
        for (var dx = 0; dx < w; dx++)
        {
            var u = (dx + 0.5) / w;
            if (s.Mirror)
            {
                u = 1 - u;
            }

            var sx = cx + (u * cw) - 0.5;
            _xMap[dx] = Math.Clamp((int)(sx * (1 << Fp)), 0, Math.Max(0, maxX - 1));
        }

        var sat = (int)Math.Round(s.Adjustments.Saturation * 256);
        var lutNeutral = s.Adjustments.Brightness == 0 && s.Adjustments.Contrast == 1 && _lutGamma == 1;
        var srcPx = src.Pixels;
        var stride = src.Stride;
        var maxY = (src.Height - 1) << Fp;

        if (_yMap.Length != h)
        {
            _yMap = new int[h];
        }

        for (var dy = 0; dy < h; dy++)
        {
            var sy = Math.Clamp((int)((cy + (((dy + 0.5) / h) * ch) - 0.5) * (1 << Fp)), 0, Math.Max(0, maxY - 1));
            _yMap[dy] = sy;
            var y0 = sy >> Fp;
            var fy = (sy >> 8) & 0xFF;
            var y1 = Math.Min(y0 + 1, src.Height - 1);
            var row0 = y0 * stride;
            var row1 = y1 * stride;
            var o = dy * w * 4;

            for (var dx = 0; dx < w; dx++, o += 4)
            {
                var sxf = _xMap[dx];
                var x0 = sxf >> Fp;
                var fx = (sxf >> 8) & 0xFF;
                var x1 = Math.Min(x0 + 1, src.Width - 1) * 4;
                x0 *= 4;

                var b = Lerp2(srcPx[row0 + x0], srcPx[row0 + x1], srcPx[row1 + x0], srcPx[row1 + x1], fx, fy);
                var g = Lerp2(srcPx[row0 + x0 + 1], srcPx[row0 + x1 + 1], srcPx[row1 + x0 + 1], srcPx[row1 + x1 + 1], fx, fy);
                var r = Lerp2(srcPx[row0 + x0 + 2], srcPx[row0 + x1 + 2], srcPx[row1 + x0 + 2], srcPx[row1 + x1 + 2], fx, fy);

                if (!lutNeutral)
                {
                    b = _lut[b];
                    g = _lut[g];
                    r = _lut[r];
                }

                if (sat != 256)
                {
                    var l = ((77 * r) + (150 * g) + (29 * b)) >> 8;
                    b = Clamp8(l + (((b - l) * sat) >> 8));
                    g = Clamp8(l + (((g - l) * sat) >> 8));
                    r = Clamp8(l + (((r - l) * sat) >> 8));
                }

                dst[o] = (byte)b;
                dst[o + 1] = (byte)g;
                dst[o + 2] = (byte)r;
                dst[o + 3] = 255;
            }
        }
    }

    private void Denoise(byte[] img, int w, int h, double strength)
    {
        var n = w * h * 4;
        if (!_hasPrevious || _previous.Length != n)
        {
            _previous = new byte[n];
            Buffer.BlockCopy(img, 0, _previous, 0, n);
            _hasPrevious = true;
            return;
        }

        // Blend only small differences (sensor noise); larger ones are motion and pass through.
        var threshold = (int)(4 + (strength * 14));
        var keep = (int)(strength * 0.65 * 256);
        for (var i = 0; i < n; i += 4)
        {
            for (var c = 0; c < 3; c++)
            {
                var cur = img[i + c];
                var prev = _previous[i + c];
                var d = cur - prev;
                if (d > -threshold && d < threshold)
                {
                    cur = (byte)(cur - ((d * keep) >> 8));
                    img[i + c] = cur;
                }

                _previous[i + c] = cur;
            }
        }
    }

    private void Sharpen(byte[] img, int w, int h, double strength)
    {
        if (w < 3 || h < 3)
        {
            return;
        }

        var n = w * h * 4;
        if (_work.Length != n)
        {
            _work = new byte[n];
        }

        Buffer.BlockCopy(img, 0, _work, 0, n);
        var amount = (int)(strength * 1.2 * 256);
        var stride = w * 4;
        for (var y = 1; y < h - 1; y++)
        {
            for (var x = 1; x < w - 1; x++)
            {
                var i = (y * stride) + (x * 4);
                for (var c = 0; c < 3; c++)
                {
                    var center = _work[i + c];
                    var blur = (_work[i + c - 4] + _work[i + c + 4] + _work[i + c - stride] + _work[i + c + stride] + (4 * center)) >> 3;
                    img[i + c] = (byte)Clamp8(center + (((center - blur) * amount) >> 8));
                }
            }
        }
    }

    /// <summary>
    /// Applies shape alpha, border and glow for this frame from the distance field. Content alpha is
    /// the shape (times the person mask in Remove mode); the glow sits behind the content.
    /// </summary>
    private void ApplyMask(byte[] img, int w, int h, CameraLayerSettings s, bool cutOutPerson, FrameContext ctx)
    {
        var (widthScale, color, glow) = LayerEffectMath.Evaluate(s.Effects, s.BorderColor, ctx);
        var bw = (float)(s.BorderWidth * Math.Min(w - (2 * _glowMargin), h - (2 * _glowMargin)) * widthScale);
        var margin = (float)_glowMargin;
        var glowStrength = (float)glow;
        var pixels = w * h;
        for (var p = 0; p < pixels; p++)
        {
            var d = _dist[p];
            var o = p * 4;
            var shapeA = Math.Clamp(0.5f - d, 0f, 1f);
            var contentA = cutOutPerson ? shapeA * _person[p] / 255f : shapeA;

            var glowA = 0f;
            if (glowStrength > 0 && margin > 0 && d > -0.5f && d < margin)
            {
                var t = 1 - (Math.Max(d, 0) / margin);
                glowA = glowStrength * t * t;
            }

            var outA = contentA + (glowA * (1 - contentA));
            if (outA <= 0.002f)
            {
                img[o] = img[o + 1] = img[o + 2] = img[o + 3] = 0;
                continue;
            }

            if (contentA > 0 && bw > 0)
            {
                var b = Math.Clamp(d + bw + 0.5f, 0f, 1f);
                if (b > 0)
                {
                    img[o] = (byte)(img[o] + ((color.B - img[o]) * b));
                    img[o + 1] = (byte)(img[o + 1] + ((color.G - img[o + 1]) * b));
                    img[o + 2] = (byte)(img[o + 2] + ((color.R - img[o + 2]) * b));
                }
            }

            if (glowA > 0)
            {
                // Straight-alpha "content over glow".
                var gw = glowA * (1 - contentA);
                img[o] = (byte)(((img[o] * contentA) + (color.B * gw)) / outA);
                img[o + 1] = (byte)(((img[o + 1] * contentA) + (color.G * gw)) / outA);
                img[o + 2] = (byte)(((img[o + 2] * contentA) + (color.R * gw)) / outA);
            }

            img[o + 3] = (byte)((outA * 255) + 0.5f);
        }
    }

    private static int Lerp2(int a, int b, int c, int d, int fx, int fy)
    {
        var top = (a << 8) + ((b - a) * fx);
        var bottom = (c << 8) + ((d - c) * fx);
        return ((top << 8) + ((bottom - top) * fy)) >> 16;
    }

    private static int Clamp8(int v) => v < 0 ? 0 : v > 255 ? 255 : v;
}

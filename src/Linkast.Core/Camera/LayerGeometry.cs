// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core.Camera;

/// <summary>A rectangle in normalized coordinates (0 to 1) of some reference frame.</summary>
public readonly record struct NormalizedRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    /// <summary>Keeps the rectangle inside 0..1 with a minimum size, preserving size where possible.</summary>
    public NormalizedRect Clamped(double minSize = 0.02)
    {
        var w = Math.Clamp(double.IsFinite(Width) ? Width : 0.25, minSize, 1);
        var h = Math.Clamp(double.IsFinite(Height) ? Height : 0.25, minSize, 1);
        var x = Math.Clamp(double.IsFinite(X) ? X : 0, 0, 1 - w);
        var y = Math.Clamp(double.IsFinite(Y) ? Y : 0, 0, 1 - h);
        return new NormalizedRect(x, y, w, h);
    }
}

/// <summary>A rectangle in whole pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>Pure geometry shared by the preview, the editor and the stream, so all three agree.</summary>
public static class LayerGeometry
{
    /// <summary>
    /// The default placement: bottom-right corner, 28 % of the canvas width, with a margin,
    /// at the layer's aspect ratio.
    /// </summary>
    public static NormalizedRect DefaultPlacement(double layerAspect, int canvasWidth, int canvasHeight)
    {
        const double widthFraction = 0.28, margin = 0.025;
        var w = widthFraction;
        var h = w * canvasWidth / layerAspect / canvasHeight;
        if (h > 0.6)
        {
            h = 0.6;
            w = h * canvasHeight * layerAspect / canvasWidth;
        }

        var marginY = margin * canvasWidth / canvasHeight;
        return new NormalizedRect(1 - w - margin, 1 - h - marginY, w, h).Clamped();
    }

    /// <summary>Adjusts the placement height so the rectangle has <paramref name="layerAspect"/> on this canvas.</summary>
    public static NormalizedRect FitAspect(NormalizedRect placement, double layerAspect, int canvasWidth, int canvasHeight)
    {
        var h = placement.Width * canvasWidth / layerAspect / canvasHeight;
        return new NormalizedRect(placement.X, placement.Y, placement.Width, h).Clamped();
    }

    /// <summary>
    /// The largest rectangle of <paramref name="layerAspect"/> that fits inside <paramref name="area"/>,
    /// centred in it. Used so a layout's slot always has the camera's own shape (a circle stays round).
    /// Returns the area unchanged when it already has that aspect (within 1 %).
    /// </summary>
    public static NormalizedRect FitInside(NormalizedRect area, double layerAspect, int canvasWidth, int canvasHeight)
    {
        var a = area.Clamped();
        var w = a.Width * canvasWidth;
        var h = a.Height * canvasHeight;
        if (layerAspect <= 0 || w <= 0 || h <= 0 || Math.Abs((w / h) - layerAspect) / layerAspect < 0.01)
        {
            return a;
        }

        var (fw, fh) = w / h > layerAspect ? (h * layerAspect, h) : (w, w / layerAspect);
        return new NormalizedRect(
            a.X + ((w - fw) / 2 / canvasWidth),
            a.Y + ((h - fh) / 2 / canvasHeight),
            fw / canvasWidth,
            fh / canvasHeight).Clamped();
    }

    /// <summary>Converts a normalized placement to even pixel coordinates (encoders prefer even sizes).</summary>
    public static PixelRect ToPixels(NormalizedRect placement, int canvasWidth, int canvasHeight)
    {
        var p = placement.Clamped();
        var w = Even(Math.Max(2, (int)Math.Round(p.Width * canvasWidth)));
        var h = Even(Math.Max(2, (int)Math.Round(p.Height * canvasHeight)));
        var x = Math.Clamp((int)Math.Round(p.X * canvasWidth), 0, Math.Max(0, canvasWidth - w));
        var y = Math.Clamp((int)Math.Round(p.Y * canvasHeight), 0, Math.Max(0, canvasHeight - h));
        return new PixelRect(x, y, w, h);
    }

    /// <summary>
    /// The source region to sample for a layer of <paramref name="layerAspect"/>: the largest crop of that
    /// aspect, shrunk by <paramref name="zoom"/>, centred on (centerX, centerY) and kept inside the source.
    /// </summary>
    public static (double X, double Y, double Width, double Height) SourceCrop(
        int sourceWidth, int sourceHeight, double layerAspect, double zoom, double centerX, double centerY)
    {
        double cw, ch;
        if ((double)sourceWidth / sourceHeight > layerAspect)
        {
            ch = sourceHeight;
            cw = ch * layerAspect;
        }
        else
        {
            cw = sourceWidth;
            ch = cw / layerAspect;
        }

        zoom = Math.Clamp(zoom, 1, 4);
        cw /= zoom;
        ch /= zoom;
        var x = Math.Clamp((centerX * sourceWidth) - (cw / 2), 0, sourceWidth - cw);
        var y = Math.Clamp((centerY * sourceHeight) - (ch / 2), 0, sourceHeight - ch);
        return (x, y, cw, ch);
    }

    private static int Even(int v) => v - (v & 1);
}

// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Linkast.App.Services;

/// <summary>
/// Draws overlay pictures and the "Be right back" card, for the output preview and for the stream, so
/// both always show the same thing. All methods must run on the UI thread.
/// </summary>
public static class OverlayRenderer
{
    private static readonly Uri LogoUri = new("pack://application:,,,/Assets/linkast-logo.png");

    /// <summary>Loads a picture file, or null if it is missing or not a picture.</summary>
    public static BitmapSource? LoadImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0]; // a GIF shows its first frame
            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The built-in away card: the navy brand background, the Linkast logo and the text.</summary>
    public static BitmapSource RenderAwayCard(int width, int height, string title, string? subtitle)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var navy = new LinearGradientBrush(Color.FromRgb(0x1D, 0x1B, 0x48), Color.FromRgb(0x09, 0x0A, 0x1C), new Point(0, 0), new Point(0.6, 1));
            dc.DrawRectangle(navy, null, new Rect(0, 0, width, height));

            // A soft glow of the brand gradient behind the logo.
            var glow = new RadialGradientBrush(Color.FromArgb(70, 0x2F, 0x6B, 0xFF), Color.FromArgb(0, 0x2F, 0x6B, 0xFF));
            var unit = Math.Min(width, height);
            dc.DrawEllipse(glow, null, new Point(width / 2.0, height * 0.38), unit * 0.45, unit * 0.45);

            var logo = new BitmapImage(LogoUri);
            var logoSize = unit * 0.28;
            dc.DrawImage(logo, new Rect((width - logoSize) / 2, height * 0.38 - logoSize / 2, logoSize, logoSize));

            var display = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var text = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var dpi = 1.0;
            var titleText = new FormattedText(title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, display, unit * 0.085, Brushes.White, dpi)
            {
                MaxTextWidth = width * 0.9,
                TextAlignment = TextAlignment.Center,
            };
            var titleTop = height * 0.38 + logoSize / 2 + unit * 0.05;
            dc.DrawText(titleText, new Point(width * 0.05, titleTop));

            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                var subText = new FormattedText(subtitle, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, text, unit * 0.04,
                    new SolidColorBrush(Color.FromRgb(0xB5, 0xBC, 0xD9)), dpi)
                {
                    MaxTextWidth = width * 0.9,
                    TextAlignment = TextAlignment.Center,
                };
                dc.DrawText(subText, new Point(width * 0.05, titleTop + titleText.Height + unit * 0.02));
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// <paramref name="source"/> fitted inside <paramref name="width"/> × <paramref name="height"/> without
    /// stretching, as straight-alpha BGRA for the layer pipe. Fit leaves transparent bars where the shapes
    /// differ; <paramref name="cover"/> fills the whole area and crops the edges instead (the away screen
    /// must hide the game completely).
    /// </summary>
    public static byte[] ToLayerFrame(BitmapSource source, int width, int height, bool cover = false)
    {
        var sx = (double)width / source.PixelWidth;
        var sy = (double)height / source.PixelHeight;
        var scale = cover ? Math.Max(sx, sy) : Math.Min(sx, sy);
        var drawW = source.PixelWidth * scale;
        var drawH = source.PixelHeight * scale;
        var fitW = Math.Clamp((int)Math.Round(drawW), 1, width);
        var fitH = Math.Clamp((int)Math.Round(drawH), 1, height);

        // Scale with high quality, then convert to straight-alpha BGRA (Bgra32 is not premultiplied).
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            dc.DrawImage(source, new Rect((fitW - drawW) / 2, (fitH - drawH) / 2, drawW, drawH));
        }

        var scaled = new RenderTargetBitmap(fitW, fitH, 96, 96, PixelFormats.Pbgra32);
        scaled.Render(visual);
        var straight = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[fitW * fitH * 4];
        straight.CopyPixels(pixels, fitW * 4, 0);

        var frame = new byte[width * height * 4];
        var offX = (width - fitW) / 2;
        var offY = (height - fitH) / 2;
        for (var y = 0; y < fitH; y++)
        {
            Buffer.BlockCopy(pixels, y * fitW * 4, frame, ((offY + y) * width + offX) * 4, fitW * 4);
        }

        return frame;
    }

    /// <summary>Width ÷ height of a picture, used to keep overlays from stretching.</summary>
    public static double AspectOf(BitmapSource? source) =>
        source is { PixelWidth: > 0, PixelHeight: > 0 } ? (double)source.PixelWidth / source.PixelHeight : 16.0 / 9;
}

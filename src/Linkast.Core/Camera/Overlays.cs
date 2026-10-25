// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core.Camera;

/// <summary>What an overlay shows. Overlays are drawn above the cameras.</summary>
public abstract record OverlaySource;

/// <summary>A picture file (PNG, JPG, GIF's first frame, BMP), for example a logo. Transparency is kept.</summary>
public sealed record ImageOverlaySource(string Path) : OverlaySource;

/// <summary>A web page with a transparent background, for example donation and follower alerts or a chat box.</summary>
/// <param name="Url">Secret: alert URLs contain account tokens.</param>
/// <param name="PageWidth">Width the page is laid out at, in CSS pixels.</param>
/// <param name="PageHeight">Height the page is laid out at, in CSS pixels.</param>
public sealed record WebOverlaySource(Secret Url, int PageWidth, int PageHeight) : OverlaySource;

/// <summary>
/// The "Be right back" screen: covers the whole stream so the game is hidden without stopping the
/// stream. Either a picture, or a card with the Linkast look and the given text.
/// </summary>
public sealed record AwayScreenSource(string Title, string? Subtitle, string? ImagePath) : OverlaySource;

/// <summary>Places an overlay on the output canvas for one stream.</summary>
/// <param name="Placement">Normalized to the output canvas.</param>
/// <param name="StartHidden">Starts off the canvas; shown later without a restart (the away screen).</param>
public sealed record OverlayLayerBinding(string LayerId, OverlaySource Source, NormalizedRect Placement, bool StartHidden = false);

public interface IOverlayInputProvider
{
    /// <summary>
    /// Creates a pipe input that renders <paramref name="overlay"/> at the given pixel size. The input
    /// chooses its own frame rate: low for still pictures, higher for animated web pages.
    /// </summary>
    IVideoLayerInput Open(OverlayLayerBinding overlay, int width, int height, int maxFrameRate);
}

/// <summary>Where an overlay sits on the stream. Simple choices instead of free dragging.</summary>
public enum OverlayPosition
{
    FullScreen,
    TopLeft,
    TopCenter,
    TopRight,
    MiddleLeft,
    Center,
    MiddleRight,
    BottomLeft,
    BottomCenter,
    BottomRight,
}

public static class OverlayGeometry
{
    /// <summary>Gap from the canvas edge, as a fraction of the longer side (the same margin cameras snap to).</summary>
    public const double Margin = 0.025;

    /// <summary>
    /// The overlay's rectangle on a <paramref name="canvasWidth"/> × <paramref name="canvasHeight"/> canvas:
    /// <paramref name="size"/> (0.05–1) of the canvas width, at the source's own aspect ratio, never larger
    /// than the canvas. Full screen fits the whole canvas, centred.
    /// </summary>
    public static NormalizedRect Place(OverlayPosition position, double size, double aspect, double canvasWidth, double canvasHeight)
    {
        if (canvasWidth <= 0 || canvasHeight <= 0)
        {
            return new NormalizedRect(0, 0, 1, 1);
        }

        aspect = double.IsFinite(aspect) && aspect > 0 ? aspect : 16.0 / 9;
        var canvasAspect = canvasWidth / canvasHeight;
        double w, h;
        if (position == OverlayPosition.FullScreen)
        {
            (w, h) = aspect >= canvasAspect ? (1.0, canvasAspect / aspect) : (aspect / canvasAspect, 1.0);
            return new NormalizedRect((1 - w) / 2, (1 - h) / 2, w, h);
        }

        w = Math.Clamp(size, 0.05, 1);
        h = w * canvasAspect / aspect;
        if (h > 1)
        {
            w /= h;
            h = 1;
        }

        var mx = Margin * Math.Max(canvasWidth, canvasHeight) / canvasWidth;
        var my = Margin * Math.Max(canvasWidth, canvasHeight) / canvasHeight;
        var x = position switch
        {
            OverlayPosition.TopLeft or OverlayPosition.MiddleLeft or OverlayPosition.BottomLeft => mx,
            OverlayPosition.TopRight or OverlayPosition.MiddleRight or OverlayPosition.BottomRight => 1 - w - mx,
            _ => (1 - w) / 2,
        };
        var y = position switch
        {
            OverlayPosition.TopLeft or OverlayPosition.TopCenter or OverlayPosition.TopRight => my,
            OverlayPosition.BottomLeft or OverlayPosition.BottomCenter or OverlayPosition.BottomRight => 1 - h - my,
            _ => (1 - h) / 2,
        };
        return new NormalizedRect(Math.Clamp(x, 0, 1 - w), Math.Clamp(y, 0, 1 - h), w, h);
    }
}

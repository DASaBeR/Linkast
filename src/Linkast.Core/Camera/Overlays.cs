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
/// stream. A video that loops, a picture, or a card with the Linkast look and the given text.
/// </summary>
/// <param name="VideoPath">A video file FFmpeg plays on a loop (its sound is not streamed). Wins over the picture.</param>
public sealed record AwayScreenSource(string Title, string? Subtitle, string? ImagePath, string? VideoPath = null) : OverlaySource;

/// <summary>
/// A scene's picture or video for one stream. FFmpeg reads the file itself: a picture once, a video on a
/// loop. It covers the whole canvas and is shown or hidden live as scenes change.
/// </summary>
/// <param name="AudioChannel">For a video: the mixer channel its sound plays on (see <see cref="AudioChannels"/>).</param>
/// <param name="AudioGain">The starting gain of that sound (0 while the scene is not on).</param>
public sealed record MediaLayerBinding(string LayerId, string FilePath, bool IsVideo, bool StartHidden, string? AudioChannel = null, double AudioGain = 0);

/// <summary>Places an overlay on the output canvas for one stream.</summary>
/// <param name="Placement">Normalized to the output canvas.</param>
/// <param name="StartHidden">Starts off the canvas; shown later without a restart (the away screen).</param>
public sealed record OverlayLayerBinding(string LayerId, OverlaySource Source, NormalizedRect Placement, bool StartHidden = false)
{
    /// <summary>The part of the overlay that is shown (fractions of it); the rest is trimmed away.</summary>
    public NormalizedRect Crop { get; init; } = NormalizedRect.Full;
}

public interface IOverlayInputProvider
{
    /// <summary>
    /// Creates a pipe input that renders <paramref name="overlay"/> at the given pixel size. The input
    /// chooses its own frame rate: low for still pictures, higher for animated web pages.
    /// </summary>
    IVideoLayerInput Open(OverlayLayerBinding overlay, int width, int height, int maxFrameRate);
}

/// <summary>Where an overlay sits on the stream: one of nine spots, full screen, or wherever it was dragged.</summary>
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

    /// <summary>Placed by hand in the preview; the rectangle is stored with the overlay.</summary>
    Custom,
}

public static class OverlayGeometry
{
    /// <summary>
    /// The position and size that come closest to <paramref name="rect"/> (for example an overlay
    /// imported from OBS): nearly the whole canvas is full screen, otherwise the nearest of the nine spots.
    /// </summary>
    public static (OverlayPosition Position, double Size) Nearest(NormalizedRect rect)
    {
        if (rect.Width >= 0.9 && rect.Height >= 0.9)
        {
            return (OverlayPosition.FullScreen, 1);
        }

        var cx = rect.X + (rect.Width / 2);
        var cy = rect.Y + (rect.Height / 2);
        var col = cx < 1 / 3.0 ? 0 : cx > 2 / 3.0 ? 2 : 1;
        var row = cy < 1 / 3.0 ? 0 : cy > 2 / 3.0 ? 2 : 1;
        OverlayPosition[] spots =
        [
            OverlayPosition.TopLeft, OverlayPosition.TopCenter, OverlayPosition.TopRight,
            OverlayPosition.MiddleLeft, OverlayPosition.Center, OverlayPosition.MiddleRight,
            OverlayPosition.BottomLeft, OverlayPosition.BottomCenter, OverlayPosition.BottomRight,
        ];
        return (spots[(row * 3) + col], Math.Round(Math.Clamp(rect.Width, 0.05, 1), 2));
    }

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
        if (position is OverlayPosition.FullScreen or OverlayPosition.Custom)
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

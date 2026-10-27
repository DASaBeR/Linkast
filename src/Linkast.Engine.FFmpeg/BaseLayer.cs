// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Linkast.Core.Camera;

namespace Linkast.Engine.FFmpeg;

/// <summary>
/// The filter options that put the captured game or screen on the canvas: <c>crop@base</c> (the user's
/// trim, and for Fill also the cut to the area's shape), <c>scale@base</c> and <c>overlay@base</c>.
/// Every value is an expression of the picture's own size (iw, ih), so it holds whatever size the
/// capture has, and the same values are used in the starting graph and as live commands.
/// </summary>
/// <remarks>
/// The only crop is before the scale. A crop after a scale whose size changes live keeps the size it
/// was set up with and reads outside the smaller picture (FFmpeg crashed in a test).
/// </remarks>
public sealed record BaseLayerArgs(
    string CropW,
    string CropH,
    string CropX,
    string CropY,
    string ScaleW,
    string ScaleH,
    string OverlayX,
    string OverlayY)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The live commands, in the order they must be sent (crop, then scale, then position).</summary>
    public IEnumerable<(string Target, string Command, string Argument)> Commands()
    {
        yield return ("crop@base", "w", CropW);
        yield return ("crop@base", "h", CropH);
        yield return ("crop@base", "x", CropX);
        yield return ("crop@base", "y", CropY);
        yield return ("scale@base", "w", ScaleW);
        yield return ("scale@base", "h", ScaleH);
        yield return ("overlay@base", "x", OverlayX);
        yield return ("overlay@base", "y", OverlayY);
    }

    /// <summary>
    /// The arguments for the game picture at <paramref name="placement"/> on a <paramref name="canvasWidth"/> ×
    /// <paramref name="canvasHeight"/> canvas, showing the part <paramref name="crop"/> of the capture.
    /// Fit shows all of it inside the area, centred; Fill covers the area and cuts the overflow evenly.
    /// Hidden keeps everything and moves it just off the right edge, ready to come back.
    /// </summary>
    public static BaseLayerArgs For(NormalizedRect placement, NormalizedRect crop, BaseFit fit, int canvasWidth, int canvasHeight, bool hidden = false)
    {
        var r = LayerGeometry.ToPixels(placement, canvasWidth, canvasHeight);
        var c = crop.Clamped(SceneLayout.MinCrop);
        string tx = F(c.X), ty = F(c.Y), tw = F(c.Width), th = F(c.Height);
        string rw = r.Width.ToString(Inv), rh = r.Height.ToString(Inv), rx = r.X.ToString(Inv), ry = r.Y.ToString(Inv);
        var hiddenX = (canvasWidth + 16).ToString(Inv);

        if (fit == BaseFit.Fill)
        {
            // The largest piece of the trimmed area with the area's shape, centred, scaled to exactly the area.
            var a = F((double)r.Width / r.Height);
            return new BaseLayerArgs(
                $"min(iw*{tw},ih*{th}*{a})",
                $"min(ih*{th},iw*{tw}/{a})",
                $"iw*{tx}+(iw*{tw}-ow)/2",
                $"ih*{ty}+(ih*{th}-oh)/2",
                rw,
                rh,
                hidden ? hiddenX : rx,
                ry);
        }

        return new BaseLayerArgs(
            $"iw*{tw}",
            $"ih*{th}",
            $"iw*{tx}",
            $"ih*{ty}",
            $"trunc(min({rw},{rh}*iw/ih)/2)*2",
            $"trunc(min({rh},{rw}*ih/iw)/2)*2",
            hidden ? hiddenX : $"{rx}+({rw}-w)/2",
            $"{ry}+({rh}-h)/2");
    }

    /// <summary>A fraction or ratio as plain digits (never a culture-specific separator).</summary>
    private static string F(double value) => value.ToString("0.######", Inv);
}

// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Linkast.Core.Camera;

/// <summary>A video capture device as discovered by the platform. <see cref="Id"/> is stable across restarts.</summary>
public sealed record CameraDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One capture mode a camera offers.</summary>
/// <param name="Subtype">Native pixel format, for example NV12, YUY2 or MJPG.</param>
public sealed record CameraFormat(int Width, int Height, uint FrameRateNumerator, uint FrameRateDenominator, string Subtype)
{
    public double FrameRate => FrameRateDenominator == 0 ? 0 : (double)FrameRateNumerator / FrameRateDenominator;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Width} × {Height} @ {FrameRate:0.##} fps ({Subtype})");
}

/// <summary>What the user asked for. Null fields mean "choose automatically".</summary>
public sealed record CameraFormatRequest(int? Width = null, int? Height = null, double? FrameRate = null, string? Subtype = null)
{
    public static CameraFormatRequest Auto { get; } = new();

    public bool IsAuto => Width is null && Height is null && FrameRate is null && Subtype is null;

    /// <summary>
    /// Picks the best matching format. Automatic mode prefers 1280x720 or the largest size up to
    /// 1920x1080, then 30 fps (or the closest above), then uncompressed NV12/YUY2 over MJPG.
    /// Never throws: falls back to the closest available format.
    /// </summary>
    public CameraFormat? Choose(IReadOnlyList<CameraFormat> formats)
    {
        ArgumentNullException.ThrowIfNull(formats);
        if (formats.Count == 0)
        {
            return null;
        }

        if (!IsAuto)
        {
            var exact = formats
                .Where(f => (Width is null || f.Width == Width) && (Height is null || f.Height == Height)
                    && (Subtype is null || string.Equals(f.Subtype, Subtype, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(f => FrameRate is { } fr ? Math.Abs(f.FrameRate - fr) : 0)
                .ThenBy(f => SubtypeRank(f.Subtype))
                .FirstOrDefault();
            if (exact is not null)
            {
                return exact;
            }
        }

        var targetPixels = 1280L * 720;
        return formats
            .OrderBy(f => (long)f.Width * f.Height > 1920L * 1080 ? 1 : 0)
            .ThenBy(f => Math.Abs(((long)f.Width * f.Height) - (Width is { } w && Height is { } h ? (long)w * h : targetPixels)))
            .ThenBy(f => Math.Abs(f.FrameRate - (FrameRate ?? 30)) < 0.5 ? 0 : f.FrameRate >= (FrameRate ?? 30) ? 1 : 2)
            .ThenBy(f => Math.Abs(f.FrameRate - (FrameRate ?? 30)))
            .ThenBy(f => SubtypeRank(f.Subtype))
            .First();
    }

    private static int SubtypeRank(string subtype) => subtype.ToUpperInvariant() switch
    {
        "NV12" => 0,
        "YUY2" => 1,
        "RGB32" or "ARGB32" or "BGRA8" => 2,
        "MJPG" => 3,
        _ => 4,
    };
}

/// <summary>Lifecycle of a camera as the user sees it.</summary>
public enum CameraState
{
    /// <summary>Not in use. The device is closed.</summary>
    Off,
    Starting,
    Running,

    /// <summary>The device disappeared or failed; Linkast will reopen it when it returns.</summary>
    Unavailable,

    /// <summary>The device cannot be used (access denied, in use, unsupported) until the user acts.</summary>
    Error,
}

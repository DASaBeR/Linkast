// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Engine.FFmpeg;

/// <summary>
/// Notices a window capture that comes out black (an exclusive-fullscreen game can do this with
/// Windows Graphics Capture). A branch of the filter graph looks at a 32 × 18 copy of the picture twice
/// a second; blackdetect marks the frames where black starts and ends, and the metadata filter writes
/// those marks to stderr, where the engine reads them. The cost is too small to measure at 1080p60.
/// </summary>
public static class BlackProbe
{
    /// <summary>
    /// Splits the capture into [cap], which the graph continues with, and a probe ending in a nullsink.
    /// The file name is quoted so its ':' reaches the metadata filter.
    /// </summary>
    public const string Graph =
        "split=2[cap][probe];[probe]fps=2,scale=32:18:flags=area,blackdetect=pic_th=0.98:pix_th=0.05,"
        + "metadata=mode=print:key=lavfi.black_start:file='pipe\\:2':direct=1,"
        + "metadata=mode=print:key=lavfi.black_end:file='pipe\\:2':direct=1,nullsink";

    private const string StartPrefix = "lavfi.black_start=";
    private const string EndPrefix = "lavfi.black_end=";

    /// <summary>True when black starts, false when it ends, null for any other line.</summary>
    public static bool? Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var t = line.Trim();
        return t.StartsWith(StartPrefix, StringComparison.Ordinal) ? true
            : t.StartsWith(EndPrefix, StringComparison.Ordinal) ? false
            : null;
    }

    /// <summary>
    /// A line the probe printed: a mark, or the "frame:N pts:… pts_time:…" header before it. FFmpeg's
    /// own progress uses "frame=" and goes to stdout, so it is never mistaken for one.
    /// </summary>
    public static bool IsProbeLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var t = line.Trim();
        return Parse(t) is not null
            || (t.StartsWith("frame:", StringComparison.Ordinal) && t.Contains(" pts_time:", StringComparison.Ordinal));
    }
}

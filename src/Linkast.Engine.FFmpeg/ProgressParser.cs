// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Linkast.Core;

namespace Linkast.Engine.FFmpeg;

/// <summary>
/// Parses the key=value blocks FFmpeg writes with <c>-progress pipe:1</c>.
/// Each block ends with <c>progress=continue</c> or <c>progress=end</c>.
/// </summary>
public sealed class ProgressParser
{
    private readonly Dictionary<string, string> _block = new(StringComparer.Ordinal);

    /// <summary>Feeds one line. Returns a snapshot when a block completes, otherwise null.</summary>
    public EncoderProgress? Feed(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var eq = line.IndexOf('=', StringComparison.Ordinal);
        if (eq <= 0)
        {
            return null;
        }

        var key = line[..eq].Trim();
        var value = line[(eq + 1)..].Trim();
        if (key != "progress")
        {
            _block[key] = value;
            return null;
        }

        var snapshot = new EncoderProgress(
            Frame: Long("frame"),
            Fps: Double("fps"),
            BitrateKbps: ParseBitrate(Get("bitrate")),
            TotalSizeBytes: Long("total_size"),
            OutTime: ParseOutTime(),
            DuplicatedFrames: Long("dup_frames"),
            DroppedFrames: Long("drop_frames"),
            Speed: ParseSpeed(Get("speed")),
            IsEnd: value == "end");
        _block.Clear();
        return snapshot;
    }

    private string? Get(string key) => _block.TryGetValue(key, out var v) ? v : null;

    private long Long(string key) =>
        long.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private double Double(string key) =>
        double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private TimeSpan ParseOutTime()
    {
        // out_time_us is authoritative; out_time_ms is (despite its name) also microseconds.
        foreach (var key in new[] { "out_time_us", "out_time_ms" })
        {
            if (long.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
            {
                return TimeSpan.FromMicroseconds(us);
            }
        }

        return TimeSpan.Zero;
    }

    internal static double ParseBitrate(string? value)
    {
        // "6012.3kbits/s" or "N/A"
        if (value is null)
        {
            return 0;
        }

        var end = value.IndexOf("kbits", StringComparison.Ordinal);
        var number = end > 0 ? value[..end] : value;
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    internal static double ParseSpeed(string? value)
    {
        // "1.01x" or "N/A"
        if (value is null)
        {
            return 0;
        }

        var number = value.TrimEnd('x', 'X', ' ');
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}

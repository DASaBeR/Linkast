// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Linkast.Core;

/// <summary>Where and how a session is recorded to a local file.</summary>
/// <param name="Folder">An existing or creatable absolute folder. Files are named by start time.</param>
public sealed record RecordingSpec(string Folder)
{
    /// <summary>After the session, each MKV file is copied into an MP4 (no re-encode) and the MKV removed.</summary>
    public bool RemuxToMp4 { get; init; }
}

public static class RecordingFiles
{
    public const string Extension = ".mkv";

    /// <summary>
    /// A new, unused file name in <paramref name="folder"/>, for example "Linkast 2026-10-25 21-04-09.mkv".
    /// MKV is written while recording because it stays playable if Linkast or the PC stops suddenly.
    /// </summary>
    public static string NewPath(string folder, DateTime localNow)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        var stem = "Linkast " + localNow.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
        var path = Path.Combine(folder, stem + Extension);
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{stem} ({n.ToString(CultureInfo.InvariantCulture)}){Extension}");
        }

        return path;
    }

    /// <summary>The user's Videos folder, Linkast subfolder.</summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Linkast");
}

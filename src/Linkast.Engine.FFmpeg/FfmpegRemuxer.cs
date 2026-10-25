// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;

namespace Linkast.Engine.FFmpeg;

/// <summary>Copies a finished MKV recording into an MP4 without re-encoding.</summary>
public static class FfmpegRemuxer
{
    /// <summary>
    /// Writes <c>name.mp4</c> next to <paramref name="mkvPath"/> and deletes the MKV once the MP4 is complete.
    /// Returns the MP4 path, or null if remuxing failed (the MKV is then kept untouched).
    /// </summary>
    public static async Task<string?> RemuxToMp4Async(string ffmpegPath, string mkvPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(ffmpegPath);
        ArgumentException.ThrowIfNullOrEmpty(mkvPath);
        if (!Path.IsPathFullyQualified(mkvPath) || !File.Exists(mkvPath))
        {
            return null;
        }

        var mp4 = Path.ChangeExtension(mkvPath, ".mp4");
        for (var n = 2; File.Exists(mp4); n++)
        {
            mp4 = Path.ChangeExtension(mkvPath, null) + $" ({n.ToString(System.Globalization.CultureInfo.InvariantCulture)}).mp4";
        }

        var partial = mp4 + ".part";
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", mkvPath, "-map", "0", "-c", "copy", "-movflags", "+faststart", "-f", "mp4", partial })
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var drainOut = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var drainErr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(drainOut, drainErr).ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(partial) || new FileInfo(partial).Length == 0)
            {
                TryDelete(partial);
                return null;
            }

            File.Move(partial, mp4);
            TryDelete(mkvPath);
            return mp4;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            TryDelete(partial);
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;

namespace Linkast.Engine.FFmpeg;

/// <summary>
/// Asks FFmpeg whether a video file has a sound track. Its sound is only mixed when it has one: a graph
/// that reads <c>[n:a]</c> from a silent video fails to start.
/// </summary>
public static class MediaProbe
{
    public static async Task<bool> HasAudioAsync(string ffmpegPath, string file, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(ffmpegPath);
        ArgumentException.ThrowIfNullOrEmpty(file);
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-i", file })
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            _ = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var info = await process.StandardError.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return HasAudioStream(info);
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    /// <summary>True when FFmpeg's description of an input lists an audio stream ("Stream #0:1: Audio: aac ...").</summary>
    public static bool HasAudioStream(string ffmpegInfo)
    {
        ArgumentNullException.ThrowIfNull(ffmpegInfo);
        return ffmpegInfo.Split('\n').Any(l => l.TrimStart().StartsWith("Stream #", StringComparison.Ordinal) && l.Contains(": Audio:", StringComparison.Ordinal));
    }
}

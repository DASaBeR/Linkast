// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Linkast.Core;

namespace Linkast.Engine.FFmpeg;

/// <summary>A located ffmpeg.exe with its identified version and build configuration.</summary>
public sealed record FfmpegInstallation(string Path, string Source, string Version, string Configuration)
{
    public bool IsGplBuild => Configuration.Contains("--enable-gpl", StringComparison.Ordinal);

    /// <summary>One line for diagnostic reports.</summary>
    public string Describe() => $"ffmpeg {Version} ({Source}: {Path}){(IsGplBuild ? " GPL" : string.Empty)}";
}

/// <summary>
/// Finds ffmpeg.exe. Order: <c>LINKAST_FFMPEG</c> environment variable, the bundled copy in
/// <c>&lt;app&gt;\ffmpeg\ffmpeg.exe</c>, then <c>PATH</c> (development convenience).
/// </summary>
public static class FfmpegLocator
{
    public const string EnvironmentVariable = "LINKAST_FFMPEG";

    public static IEnumerable<(string Path, string Source)> Candidates()
    {
        var env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env))
        {
            yield return (env, "environment");
        }

        yield return (System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"), "bundled");

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;
            try
            {
                candidate = System.IO.Path.Combine(dir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            }
            catch (ArgumentException)
            {
                continue;
            }

            yield return (candidate, "PATH");
        }
    }

    public static async Task<FfmpegInstallation> LocateAsync(CancellationToken cancellationToken = default)
    {
        foreach (var (path, source) in Candidates())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var (version, configuration) = await ReadVersionAsync(path, cancellationToken).ConfigureAwait(false);
            if (version is not null)
            {
                return new FfmpegInstallation(System.IO.Path.GetFullPath(path), source, version, configuration ?? string.Empty);
            }
        }

        throw new StreamFailureException(new StreamError(
            ErrorCategory.Configuration,
            "ffmpeg.missing",
            $"FFmpeg was not found. Reinstall Linkast or set {EnvironmentVariable} to the path of ffmpeg.exe."));
    }

    private static async Task<(string? Version, string? Configuration)> ReadVersionAsync(string path, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-version");

        try
        {
            using var p = Process.Start(psi);
            if (p is null)
            {
                return (null, null);
            }

            var output = await p.StandardOutput.ReadToEndAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            string? version = null, configuration = null;
            foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith("ffmpeg version ", StringComparison.Ordinal))
                {
                    version = line["ffmpeg version ".Length..].Split(' ')[0];
                }
                else if (line.StartsWith("configuration:", StringComparison.Ordinal))
                {
                    configuration = line["configuration:".Length..].Trim();
                }
            }

            return (version, configuration);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or TimeoutException or IOException)
        {
            return (null, null);
        }
    }
}

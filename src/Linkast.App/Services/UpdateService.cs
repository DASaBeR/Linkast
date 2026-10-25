// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace Linkast.App.Services;

/// <summary>Outcome of an update check, already phrased for the UI.</summary>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, string? NewVersion, string Message);

public enum UpdateCheckStatus
{
    UpToDate,
    Available,
    NotInstalled,
    NotConfigured,
    Failed,
}

/// <summary>
/// Update checks against the project's GitHub Releases. Policy (decided with the maintainer):
/// one check at start-up plus the "Check for updates" button; a newer version is only shown as
/// text, never as a dialog, and is installed only when the user asks. Nothing else is sent:
/// the check is a plain request for the public release list.
/// </summary>
public sealed partial class UpdateService(ILogger<UpdateService> logger)
{
    private UpdateManager? _manager;
    private UpdateInfo? _pending;

    /// <summary>"owner/name" from the build, or null when the build has no release repository.</summary>
    public static string? Repository { get; } = typeof(UpdateService).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "LinkastReleaseRepository")?.Value is { Length: > 0 } r && r.Contains('/', StringComparison.Ordinal) ? r : null;

    /// <summary>The running version, for example "0.2.0-beta.1".</summary>
    public static string CurrentVersion { get; } = DisplayVersion(typeof(UpdateService).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0");

    public static string? RepositoryUrl => Repository is null ? null : "https://github.com/" + Repository;

    /// <summary>Strips the source-revision suffix (+abc123) the SDK appends.</summary>
    public static string DisplayVersion(string informational)
    {
        ArgumentNullException.ThrowIfNull(informational);
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }

    /// <summary>
    /// Testing only: a local folder with Velopack release files used instead of GitHub, so the whole
    /// update flow can be tried before a release is published.
    /// </summary>
    public const string TestSourceVariable = "LINKAST_UPDATE_SOURCE";

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var testFolder = Environment.GetEnvironmentVariable(TestSourceVariable);
        var url = RepositoryUrl;
        if (url is null && string.IsNullOrEmpty(testFolder))
        {
            return new(UpdateCheckStatus.NotConfigured, null, "This build has no release page set, so it can't check for updates.");
        }

        try
        {
            // Pre-releases are included while Linkast is in beta.
            _manager ??= new UpdateManager(string.IsNullOrEmpty(testFolder)
                ? new GithubSource(url!, accessToken: null, prerelease: true)
                : new SimpleFileSource(new System.IO.DirectoryInfo(testFolder)));
            if (!_manager.IsInstalled)
            {
                return new(UpdateCheckStatus.NotInstalled, null, "Updates work in the installed version. This copy isn't installed.");
            }

            var info = await _manager.CheckForUpdatesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            _pending = info;
            if (info is null)
            {
                return new(UpdateCheckStatus.UpToDate, null, "You have the latest version.");
            }

            var version = info.TargetFullRelease.Version.ToString();
            LogAvailable(version);
            return new(UpdateCheckStatus.Available, version, $"Version {version} is ready to update.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Offline, GitHub unreachable or rate-limited: never an error dialog, just a quiet note.
            LogFailed(ex.GetType().Name, ex.Message);
            return new(UpdateCheckStatus.Failed, null, "Couldn't check for updates. Check your internet connection and try again.");
        }
    }

    /// <summary>Downloads the update found by the last check, then restarts Linkast into it.</summary>
    public async Task InstallAsync(IProgress<int>? progress, CancellationToken cancellationToken = default)
    {
        if (_manager is null || _pending is null)
        {
            throw new InvalidOperationException("Check for updates first.");
        }

        await _manager.DownloadUpdatesAsync(_pending, p => progress?.Report(p), cancelToken: cancellationToken).ConfigureAwait(false);
        LogInstalling(_pending.TargetFullRelease.Version.ToString());
        _manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Update available: {Version}")]
    private partial void LogAvailable(string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Update check failed: {Type} {Message}")]
    private partial void LogFailed(string type, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "Installing update {Version} and restarting")]
    private partial void LogInstalling(string version);
}

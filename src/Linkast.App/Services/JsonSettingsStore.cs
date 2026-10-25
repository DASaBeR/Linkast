// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Linkast.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Linkast.App.Services;

/// <summary>
/// Reads and writes <c>settings.json</c> in the Linkast data folder. Writes are atomic
/// (temp file then replace), so a crash mid-save never leaves a half-written file. A corrupt
/// file is kept aside, never silently overwritten.
/// </summary>
public sealed partial class JsonSettingsStore(ILogger<JsonSettingsStore> logger)
{
    private readonly Lock _gate = new();

    public static string FilePath => Path.Combine(App.DataDirectory, "settings.json");

    public SettingsLoadOutcome LastOutcome { get; private set; }

    public LinkastSettings Load()
    {
        string? json = null;
        try
        {
            if (File.Exists(FilePath))
            {
                json = File.ReadAllText(FilePath);
            }
        }
        catch (IOException ex)
        {
            LogReadFailed(ex.Message);
        }

        var (settings, outcome) = SettingsSerializer.Deserialize(json);
        LastOutcome = outcome;
        if (outcome is SettingsLoadOutcome.Corrupt or SettingsLoadOutcome.NewerVersion)
        {
            var aside = FilePath + $".{(outcome == SettingsLoadOutcome.Corrupt ? "corrupt" : "newer")}-{DateTime.Now:yyyyMMdd-HHmmss}.json";
            try
            {
                File.Copy(FilePath, aside, overwrite: false);
            }
            catch (IOException)
            {
                // Best effort.
            }

            LogKeptAside(outcome.ToString(), aside);
        }

        return settings;
    }

    public void Save(LinkastSettings settings)
    {
        var json = SettingsSerializer.Serialize(settings);
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(App.DataDirectory);
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, json);
                if (File.Exists(FilePath))
                {
                    File.Replace(temp, FilePath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(temp, FilePath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogWriteFailed(ex.Message);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Settings could not be read: {Reason}")]
    private partial void LogReadFailed(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Settings file {Outcome}; a copy was kept at {Path}")]
    private partial void LogKeptAside(string outcome, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Settings could not be saved: {Reason}")]
    private partial void LogWriteFailed(string reason);
}

// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linkast.Core;
using Linkast.Core.Camera;
using Linkast.Core.Obs;
using Linkast.Core.Settings;

namespace Linkast.App.ViewModels;

/// <summary>The parts of an OBS setup that can be taken over, each one optional.</summary>
public enum ObsImportPart
{
    Stream,
    Quality,
    Capture,
    Cameras,
    Overlays,
    Audio,
    Recording,
}

/// <summary>One line of the import checklist.</summary>
public sealed partial class ObsImportItem(ObsImportPart part, string title, string detail) : ObservableObject
{
    public ObsImportPart Part { get; } = part;

    public string Title { get; } = title;

    public string Detail { get; } = detail;

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = true;

    public override string ToString() => $"{Title}: {Detail}";
}

/// <summary>Settings › Import from OBS: finds OBS's settings, shows what was found, imports what is ticked.</summary>
public sealed partial class ObsImportViewModel : ObservableObject
{
    /// <summary>Raised with the import and the ticked parts. The main view model applies them.</summary>
    public event EventHandler<(ObsImport Import, IReadOnlySet<ObsImportPart> Parts)>? ImportRequested;

    public ObservableCollection<ObsImportItem> Items { get; } = [];

    public ObservableCollection<string> Skipped { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFound))]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    public partial ObsImport? Found { get; set; }

    public bool HasFound => Found is not null;

    /// <summary>What happened last, for example "Found the profile 'My Stream'" or "Imported 5 settings".</summary>
    [ObservableProperty]
    public partial string? Message { get; set; }

    [RelayCommand]
    private async Task FindAsync() => await LoadFromAsync(ObsImporter.DefaultFolder);

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "OBS settings folder (the folder that contains 'basic')",
            InitialDirectory = Directory.Exists(ObsImporter.DefaultFolder) ? ObsImporter.DefaultFolder : null,
        };
        if (dialog.ShowDialog() == true)
        {
            await LoadFromAsync(dialog.FolderName);
        }
    }

    private async Task LoadFromAsync(string folder)
    {
        Items.Clear();
        Skipped.Clear();
        Found = null;
        var import = await Task.Run(() => ObsImporter.Read(folder));
        if (import is null)
        {
            Message = "No OBS settings were found. If OBS keeps them somewhere else, choose that folder.";
            return;
        }

        Found = import;
        Message = import.SceneName is null
            ? $"Found the OBS profile \"{import.ProfileName}\". Tick what to bring over."
            : $"Found the OBS profile \"{import.ProfileName}\" and its scene \"{import.SceneName}\". Tick what to bring over.";
        foreach (var item in Describe(import))
        {
            Items.Add(item);
        }

        foreach (var s in import.Skipped)
        {
            Skipped.Add(s);
        }
    }

    private bool CanImport() => Found is not null;

    [RelayCommand(CanExecute = nameof(CanImport))]
    private void Import()
    {
        if (Found is not { } import)
        {
            return;
        }

        var parts = Items.Where(i => i.IsChecked).Select(i => i.Part).ToHashSet();
        if (parts.Count == 0)
        {
            Message = "Nothing is ticked.";
            return;
        }

        ImportRequested?.Invoke(this, (import, parts));
    }

    /// <summary>The checklist lines. The stream key is never shown in full.</summary>
    private static IEnumerable<ObsImportItem> Describe(ObsImport i)
    {
        if (i.Stream is { } s)
        {
            var platform = StreamPlatforms.Get(StreamPlatforms.Detect(s.ServerUrl));
            var where = platform.Platform == StreamPlatform.Custom ? s.ServerUrl : $"{platform.Name} ({s.ServerUrl})";
            yield return new(ObsImportPart.Stream, "Where to stream", $"{where}, stream key {Mask(s.Key)}");
        }

        if (i.Video is { } v)
        {
            var quality = string.Create(CultureInfo.InvariantCulture, $"{v.Width} × {v.Height} at {v.Fps} FPS");
            if (i.VideoBitrateKbps is { } kbps)
            {
                quality += string.Create(CultureInfo.InvariantCulture, $", {kbps} kbps");
            }

            quality += i.EncoderId switch
            {
                "h264_nvenc" => ", NVIDIA encoder",
                "h264_amf" => ", AMD encoder",
                "h264_qsv" => ", Intel encoder",
                "libx264" => ", x264 (CPU) encoder",
                _ => string.Empty,
            };
            yield return new(ObsImportPart.Quality, "Quality", quality);
        }

        if (i.Capture is { } c)
        {
            yield return new(ObsImportPart.Capture, "What to capture", c.Kind == ObsCaptureKind.Display
                ? c.MonitorIndex is { } m ? string.Create(CultureInfo.InvariantCulture, $"Display {m + 1}") : "Entire display"
                : $"Window: {c.WindowTitle ?? c.WindowExe ?? "a game or app"}{(c.WindowExe is null ? string.Empty : $" [{c.WindowExe}]")}");
        }

        if (i.Cameras.Count > 0)
        {
            yield return new(ObsImportPart.Cameras, "Cameras", string.Join(", ", i.Cameras.Select(cam => cam.DeviceName)) + ", with their places");
        }

        if (i.Overlays.Count > 0)
        {
            yield return new(ObsImportPart.Overlays, "Overlays (replace yours)",
                string.Join(", ", i.Overlays.Select(o => $"{o.Name} ({(o.Kind == OverlayKind.Web ? "alert/web page" : "picture")})")));
        }

        if (i.DesktopAudio || i.Microphone)
        {
            yield return new(ObsImportPart.Audio, "Audio", (i.DesktopAudio, i.Microphone) switch
            {
                (true, true) => "System audio and microphone on",
                (true, false) => "System audio on, microphone off",
                _ => "Microphone on, system audio off",
            });
        }

        if (i.RecordingFolder is { } folder)
        {
            yield return new(ObsImportPart.Recording, "Recordings folder", folder);
        }
    }

    private static string Mask(Secret key)
    {
        var k = key.Reveal();
        return k.Length switch
        {
            0 => "(empty)",
            <= 6 => "••••",
            _ => "••••" + k[^4..],
        };
    }
}

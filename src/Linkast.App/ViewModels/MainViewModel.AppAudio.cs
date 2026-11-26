// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linkast.Core;
using Linkast.Core.Settings;
using Linkast.Platform.Windows.Audio;
using Linkast.Platform.Windows.Capture;

namespace Linkast.App.ViewModels;

/// <summary>A program that can be given its own fader.</summary>
public sealed record AppAudioChoice(string Program, string Name, bool PlayingNow)
{
    public override string ToString() => PlayingNow ? $"{Name}  (playing now)" : Name;
}

/// <summary>
/// Apps with their own fader: music (Spotify), a browser, a voice chat (Discord). Each is captured by
/// program, with its child processes, through Windows' process loopback, and found again by name when it
/// restarts. Windows can leave only Linkast itself out of Desktop audio, so an app on its own fader is
/// also in Desktop audio: the Audio tab says so while both are on.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Apps with their own fader, in the order they were added.</summary>
    public ObservableCollection<AppAudioSetting> AppAudioSources { get; } = [];

    /// <summary>Programs to choose from: those playing sound now first, then those with a window.</summary>
    public ObservableCollection<AppAudioChoice> AppAudioChoices { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddAppAudioCommand))]
    public partial AppAudioChoice? SelectedAppAudioChoice { get; set; }

    /// <summary>Capturing one program's sound needs Windows 10 version 2004 or later.</summary>
    public bool CanCaptureAppAudio => AppAudioSupported;

    private static bool AppAudioSupported => Environment.OSVersion.Version.Build >= 19041;

    /// <summary>An app on its own fader is also in Desktop audio while that is on and not muted: heard twice.</summary>
    public bool AppAudioHeardTwice =>
        AppAudioSources.Count > 0 && SystemAudioEnabled && Mixer.Get(AudioChannels.System) is { Muted: false };

    public bool HasAppAudio => AppAudioSources.Count > 0;

    /// <summary>Fills the choices: run when the list is opened, so a program started a moment ago is in it.</summary>
    [RelayCommand]
    private void RefreshAppAudioChoices()
    {
        var playing = AudioDeviceCatalog.ProgramsWithSound();
        var withWindow = VideoSourceCatalog.ListWindows().Select(w => w.ProcessName).OfType<string>();
        var choices = playing.Select(p => (Program: p, Playing: true))
            .Concat(withWindow.Where(p => !playing.Contains(p)).Select(p => (Program: p, Playing: false)))
            .Where(c => SettingsSerializer.IsProgramFile(c.Program) && !NotAnApp.Contains(c.Program)
                && AppAudioSources.All(a => AudioChannels.ForApp(a.Program) != AudioChannels.ForApp(c.Program)))
            .DistinctBy(c => c.Program, StringComparer.OrdinalIgnoreCase)
            .Select(c => new AppAudioChoice(c.Program, FriendlyName(c.Program), c.Playing))
            .OrderByDescending(c => c.PlayingNow)
            .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var keep = SelectedAppAudioChoice?.Program;
        AppAudioChoices.Clear();
        foreach (var c in choices)
        {
            AppAudioChoices.Add(c);
        }

        SelectedAppAudioChoice = AppAudioChoices.FirstOrDefault(c => c.Program == keep) ?? AppAudioChoices.FirstOrDefault();
    }

    private bool CanAddAppAudio() => SelectedAppAudioChoice is not null && AppAudioSources.Count < SettingsSerializer.MaxAppAudio;

    [RelayCommand(CanExecute = nameof(CanAddAppAudio))]
    private void AddAppAudio()
    {
        if (SelectedAppAudioChoice is not { } choice)
        {
            return;
        }

        AppAudioSources.Add(new AppAudioSetting(choice.Program, choice.Name));
        AppendLog($"{choice.Name} has its own fader in the mixer{(IsActive ? " and is in the stream now" : string.Empty)}.");
        OnAppAudioChanged();

        // Taken out and added again while live: its sound is still in the stream, silent; its fader sets it again.
        var channel = AudioChannels.ForApp(choice.Program);
        ApplyLiveGain(channel, GainOf(channel));
        AppAudioChoices.Remove(choice);
        SelectedAppAudioChoice = AppAudioChoices.FirstOrDefault();
    }

    [RelayCommand]
    private void RemoveAppAudio(AppAudioSetting? app)
    {
        if (app is null || !AppAudioSources.Remove(app))
        {
            return;
        }

        // A sound stays in a running stream once added: silent from now on.
        ApplyLiveGain(AudioChannels.ForApp(app.Program), 0);
        AppendLog($"{app.Name} no longer has its own fader.");
        OnAppAudioChanged();
    }

    private void OnAppAudioChanged()
    {
        RefreshMixerChannels();
        UpdateStreamMeters();
        AddAppAudioCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(AppAudioHeardTwice));
        OnPropertyChanged(nameof(HasAppAudio));
        ScheduleSave();
        SendLiveAppAudio();
    }

    /// <summary>While live, an added app goes into the stream at once (its fader works from then on).</summary>
    private void SendLiveAppAudio()
    {
        if (_engine is null || !IsActive || State == Core.StreamState.Stopping)
        {
            return;
        }

        var audio = BuildAudioSources();
        if (_engine.ReplaceSources(_liveCaptures, audio, _liveStack))
        {
            _liveAudio = audio;
        }
    }

    /// <summary>The sound of each app with its own fader: found by program name, now and whenever it restarts.</summary>
    private IEnumerable<AudioSourceSpec> AppAudioSpecs() =>
        AppAudioSupported
            ? AppAudioSources.Select(a => new ProcessAudioSource(0, a.Name)
            {
                ProgramName = a.Program,
                Channel = AudioChannels.ForApp(a.Program),
                Gain = GainOf(AudioChannels.ForApp(a.Program)),
                Optional = true,
            })
            : [];

    private void LoadAppAudio(IReadOnlyList<AppAudioSetting> apps)
    {
        AppAudioSources.Clear();
        foreach (var a in apps)
        {
            AppAudioSources.Add(a);
        }

        OnPropertyChanged(nameof(AppAudioHeardTwice));
        OnPropertyChanged(nameof(HasAppAudio));
    }

    /// <summary>Programs that have windows but no sound of their own worth a fader.</summary>
    private static readonly HashSet<string> NotAnApp = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "ApplicationFrameHost.exe", "TextInputHost.exe", "SystemSettings.exe", "ShellExperienceHost.exe",
        "StartMenuExperienceHost.exe", "SearchHost.exe", "LockApp.exe", "dwm.exe", "audiodg.exe",
    };

    /// <summary>"Google Chrome" for chrome.exe: the program's own description when it can be read, else its file name.</summary>
    private static string FriendlyName(string program)
    {
        var plain = Path.GetFileNameWithoutExtension(program);
        try
        {
            if (ProgramProcesses.FindRoot(program) is { } pid)
            {
                using var process = Process.GetProcessById((int)pid);
                if (process.MainModule?.FileName is { } path
                    && FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 and <= 40 } description)
                {
                    return description.Trim();
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Another user's or a protected process: its file name will do.
        }

        return plain.Length > 0 ? char.ToUpperInvariant(plain[0]) + plain[1..] : program;
    }
}

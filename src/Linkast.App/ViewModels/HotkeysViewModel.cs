// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linkast.Core.Settings;

namespace Linkast.App.ViewModels;

/// <summary>One action in Settings › Hotkeys.</summary>
public sealed partial class HotkeyRowViewModel(HotkeyAction action, string label) : ObservableObject
{
    public HotkeyAction Action { get; } = action;

    public string Label { get; } = label;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    public partial Hotkey? Hotkey { get; set; }

    public string Text => Hotkey?.ToString() ?? "None";

    /// <summary>Why the key does not work (taken by another app, already used, not allowed), or null.</summary>
    [ObservableProperty]
    public partial string? Problem { get; set; }

    /// <summary>Read by screen readers for the row.</summary>
    public override string ToString() => $"{Label}: {Text}";
}

/// <summary>Global hotkeys: which key does what, and whether Windows accepted each key.</summary>
public sealed partial class HotkeysViewModel : ObservableObject
{
    public HotkeysViewModel()
    {
        Rows =
        [
            new(HotkeyAction.GoLive, "Go live / End stream"),
            new(HotkeyAction.MuteMicrophone, "Mute / unmute microphone"),
            new(HotkeyAction.ToggleCameras, "Cameras off / on"),
            new(HotkeyAction.BeRightBack, "Be right back on / off"),
            new(HotkeyAction.Record, "Start / stop recording"),
        ];
        Load(new HotkeySettings());
    }

    /// <summary>Raised when a key or the on/off switch changes: register again and save.</summary>
    public event EventHandler? SettingsChanged;

    public ObservableCollection<HotkeyRowViewModel> Rows { get; }

    [ObservableProperty]
    public partial bool Enabled { get; set; } = true;

    private bool _loading;

    partial void OnEnabledChanged(bool value)
    {
        if (!_loading)
        {
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Load(HotkeySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _loading = true;
        Enabled = settings.Enabled;
        foreach (var row in Rows)
        {
            row.Hotkey = settings.Get(row.Action);
            row.Problem = null;
        }

        _loading = false;
    }

    public HotkeySettings ToSettings() => new()
    {
        Enabled = Enabled,
        Bindings = Rows.Where(r => r.Hotkey is not null).ToDictionary(r => r.Action, r => r.Hotkey!.ToString()),
    };

    /// <summary>Sets a key typed by the user. A key Linkast already uses for something else is refused.</summary>
    public void Assign(HotkeyRowViewModel row, Hotkey? hotkey)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (hotkey is { IsValid: false })
        {
            row.Problem = $"{hotkey} can't be a hotkey. Use Ctrl, Alt or Win together with a letter, a number or an F key.";
            return;
        }

        if (hotkey is not null && Rows.FirstOrDefault(r => r != row && Equals(r.Hotkey, hotkey)) is { } other)
        {
            row.Problem = $"{hotkey} is already used for \"{other.Label}\".";
            return;
        }

        row.Problem = null;
        if (Equals(row.Hotkey, hotkey))
        {
            return;
        }

        row.Hotkey = hotkey;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Clear(HotkeyRowViewModel row) => Assign(row, null);

    [RelayCommand]
    private void ResetDefaults()
    {
        Load(new HotkeySettings { Enabled = Enabled });
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows which keys Windows refused because another app holds them.</summary>
    public void ShowRegistrationResult(IReadOnlySet<HotkeyAction> failed)
    {
        ArgumentNullException.ThrowIfNull(failed);
        foreach (var row in Rows)
        {
            if (failed.Contains(row.Action))
            {
                row.Problem = $"Another app already uses {row.Hotkey}. Choose a different key.";
            }
            else if (row.Problem?.StartsWith("Another app", StringComparison.Ordinal) == true)
            {
                row.Problem = null;
            }
        }
    }
}

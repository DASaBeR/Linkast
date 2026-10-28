// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linkast.Core;
using Linkast.Platform.Windows.Capture;

namespace Linkast.App.ViewModels;

/// <summary>
/// Exclusive-fullscreen games: notices when the captured window may come out black and offers to
/// capture its display instead. Detection only reads from Windows; the game itself is never touched,
/// and Linkast never changes another program's compatibility settings (it only explains how).
/// </summary>
public sealed partial class MainViewModel
{
    private readonly FullscreenTracker _fullscreen = new();
    private DispatcherTimer? _fullscreenTimer;

    // The running stream's picture of the window has been black for a few seconds.
    private bool _captureLooksBlack;

    /// <summary>What to tell the user about the captured window, or null when all looks fine.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCaptureHint))]
    public partial string? CaptureHint { get; set; }

    public bool HasCaptureHint => CaptureHint is not null;

    /// <summary>Offer "Capture this display instead" with the hint.</summary>
    [ObservableProperty]
    public partial bool CanCaptureDisplayInstead { get; set; }

    /// <summary>Watches the captured window every 2 seconds while the Stream page is open or a stream runs.</summary>
    private void UpdateFullscreenWatch()
    {
        var watch = IsWindowSource && SelectedWindow is not null && (IsStreamPageVisible || IsActive);
        if (!watch)
        {
            _fullscreenTimer?.Stop();
            _fullscreen.Reset();
            UpdateCaptureHint();
            return;
        }

        _fullscreenTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => InspectCapturedWindow(), _ui);
        if (!_fullscreenTimer.IsEnabled)
        {
            _fullscreenTimer.Start();
            InspectCapturedWindow();
        }
    }

    private void InspectCapturedWindow()
    {
        if (SelectedWindow is not { } window)
        {
            return;
        }

        var seen = FullscreenDetector.Inspect(window.Handle, out var minimized);
        if (_fullscreen.Observe(window.Handle, seen, minimized))
        {
            UpdateCaptureHint();
        }
    }

    /// <summary>From telemetry. Black only counts while the window is (or was last seen) fullscreen: a dark scene in a normal window is not a capture problem.</summary>
    private void UpdateCaptureBlack(bool black)
    {
        if (black != _captureLooksBlack)
        {
            _captureLooksBlack = black;
            UpdateCaptureHint();
        }
    }

    private void UpdateCaptureHint()
    {
        var s = _fullscreen.Current;
        var program = s.ExecutableName ?? "This program";
        var parts = new List<string>();
        var black = _captureLooksBlack && IsWindowSource && s.Kind != FullscreenKind.None;
        if (black)
        {
            parts.Add($"The stream shows {program} as black. This happens with games in exclusive fullscreen. Capture its display instead, or switch the game to borderless or windowed mode.");
        }
        else if (s.Kind == FullscreenKind.Exclusive)
        {
            parts.Add($"{program} is in exclusive fullscreen, which can capture as black. If it does, capture its display instead, or switch the game to borderless or windowed mode.");
        }

        if (s.OptimizationsDisabled)
        {
            parts.Add($"Fullscreen optimizations are turned off for {program}, which makes a black capture more likely in fullscreen. To turn them back on: right-click {program} › Properties › Compatibility, and clear \"Disable fullscreen optimizations\".");
        }

        CaptureHint = parts.Count == 0 ? null : string.Join("\n\n", parts);
        CanCaptureDisplayInstead = black || s.Kind == FullscreenKind.Exclusive || (s.Kind == FullscreenKind.Fullscreen && s.OptimizationsDisabled);
    }

    /// <summary>
    /// Switches from the game window to Desktop Duplication of the display it is on, after the user
    /// confirms that everything on that display may appear on stream. A running stream switches to it.
    /// </summary>
    [RelayCommand]
    private void CaptureDisplayInstead()
    {
        var handle = _fullscreen.Current.MonitorHandle
            ?? (SelectedWindow is { } w ? FullscreenDetector.Inspect(w.Handle, out _).MonitorHandle : null);
        var monitor = Monitors.FirstOrDefault(m => m.Handle == handle) ?? Monitors.FirstOrDefault(m => m.IsPrimary) ?? Monitors.FirstOrDefault();
        if (monitor is null)
        {
            return;
        }

        var live = IsActive;
        var capture = Views.ConfirmDialog.Ask(
            "Capture this display instead?",
            $"Linkast will capture all of {monitor} instead of only the game window.\n\n"
            + "Everything on that display may appear on stream, including notifications, chat and other windows. Close or move anything private first."
            + (live ? "\n\nViewers see a short pause while the stream switches." : string.Empty),
            "Capture display",
            "Keep the game window",
            Views.ConfirmKind.Warning);
        if (!capture)
        {
            return;
        }

        // A running stream switches to the display by itself (see MainViewModel.LiveSource).
        SelectedSourceKind = SourceKinds.First(k => k.Kind == VideoSourceKind.Display);
        SelectedMonitor = monitor;
        UseDesktopDuplication = true;
        AppendLog($"Capturing {monitor} with Desktop Duplication instead of the game window.");
    }
}

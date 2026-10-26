// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core;

/// <summary>How a captured window is being shown.</summary>
public enum FullscreenKind
{
    /// <summary>Normal, maximized or minimized window.</summary>
    None,

    /// <summary>Fills its monitor without a frame: borderless, or exclusive fullscreen run as a window by Windows.</summary>
    Fullscreen,

    /// <summary>Windows reports a Direct3D app in exclusive fullscreen and this window is in front.</summary>
    Exclusive,
}

/// <param name="MonitorHandle">HMONITOR of the monitor the window is on, when known.</param>
/// <param name="ExecutableName">File name of the window's program, for messages (for example "game.exe").</param>
/// <param name="OptimizationsDisabled">"Disable fullscreen optimizations" is ticked for the program.</param>
public sealed record FullscreenState(FullscreenKind Kind, long? MonitorHandle, string? ExecutableName, bool OptimizationsDisabled)
{
    public static FullscreenState Unknown { get; } = new(FullscreenKind.None, null, null, false);
}

/// <summary>An axis-aligned rectangle in screen pixels (right and bottom exclusive).</summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom);

/// <summary>
/// The decisions behind fullscreen detection, kept free of Win32 so they can be tested. All inputs come
/// from read-only Windows queries; nothing here touches the captured program.
/// </summary>
public static class FullscreenRules
{
    /// <summary>The compatibility flag Windows sets when "Disable fullscreen optimizations" is ticked.</summary>
    public const string DisableOptimizationsFlag = "DISABLEDXMAXIMIZEDWINDOWEDMODE";

    /// <summary>True when the window reaches every edge of the monitor.</summary>
    public static bool CoversMonitor(ScreenRect window, ScreenRect monitor) =>
        window.Left <= monitor.Left && window.Top <= monitor.Top && window.Right >= monitor.Right && window.Bottom >= monitor.Bottom;

    /// <param name="exclusiveReported">Windows reports a Direct3D app in exclusive fullscreen (QUNS_RUNNING_D3D_FULL_SCREEN).</param>
    public static FullscreenKind Classify(bool minimized, bool foreground, bool hasCaption, bool coversMonitor, bool exclusiveReported)
    {
        // A maximized window with an auto-hidden taskbar also covers the monitor, but keeps its title bar.
        if (minimized || hasCaption || !coversMonitor)
        {
            return FullscreenKind.None;
        }

        return foreground && exclusiveReported ? FullscreenKind.Exclusive : FullscreenKind.Fullscreen;
    }

    /// <summary>Reads a value from AppCompatFlags\Layers, such as "~ DISABLEDXMAXIMIZEDWINDOWEDMODE HIGHDPIAWARE".</summary>
    public static bool HasOptimizationsDisabled(string? layers) =>
        layers is not null
        && layers.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(DisableOptimizationsFlag, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Remembers what was last seen for one captured window. A game in exclusive fullscreen minimizes
/// when the user switches to Linkast, so the state seen while playing is kept until the window is
/// seen again as an ordinary visible window, or another window is chosen.
/// </summary>
public sealed class FullscreenTracker
{
    private long? _window;

    public FullscreenState Current { get; private set; } = FullscreenState.Unknown;

    /// <summary>Records one observation. Returns true when <see cref="Current"/> changed.</summary>
    public bool Observe(long window, FullscreenState seen, bool minimized)
    {
        ArgumentNullException.ThrowIfNull(seen);
        var next = _window != window || !minimized || seen.Kind != FullscreenKind.None
            ? seen
            : Current with { OptimizationsDisabled = seen.OptimizationsDisabled };
        _window = window;
        if (next == Current)
        {
            return false;
        }

        Current = next;
        return true;
    }

    public void Reset()
    {
        _window = null;
        Current = FullscreenState.Unknown;
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Linkast.Core.Settings;

namespace Linkast.App.Services;

/// <summary>
/// System-wide hotkeys through Win32 <c>RegisterHotKey</c> on the main window, so they work while a game
/// has the focus. Windows refuses a key another app already registered; that is reported per action.
/// UI thread only.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;

    private readonly Dictionary<int, HotkeyAction> _registered = [];
    private HwndSource? _source;

    /// <summary>Raised on the UI thread when a hotkey is pressed.</summary>
    public event EventHandler<HotkeyAction>? Pressed;

    /// <summary>Hooks the window's messages. Call once the window has a handle.</summary>
    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _source = HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle());
        _source.AddHook(WndProc);
    }

    /// <summary>
    /// Replaces all registrations. Returns the actions whose key could not be registered (usually
    /// because another app uses it).
    /// </summary>
    public IReadOnlySet<HotkeyAction> Apply(HotkeySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        UnregisterAll();
        var failed = new HashSet<HotkeyAction>();
        if (_source is null || !settings.Enabled)
        {
            return failed;
        }

        var id = 1;
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            if (settings.Get(action) is not { } hotkey || !Enum.TryParse<Key>(hotkey.Key, out var key))
            {
                continue;
            }

            var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (RegisterHotKey(_source.Handle, id, (uint)hotkey.Modifiers | ModNoRepeat, vk))
            {
                _registered[id] = action;
                id++;
            }
            else
            {
                failed.Add(action);
            }
        }

        return failed;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private void UnregisterAll()
    {
        if (_source is not null)
        {
            foreach (var id in _registered.Keys)
            {
                UnregisterHotKey(_source.Handle, id);
            }
        }

        _registered.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _registered.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            Pressed?.Invoke(this, action);
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Linkast.Core;
using Microsoft.Win32;

namespace Linkast.Platform.Windows.Capture;

/// <summary>
/// Tells whether a window is shown fullscreen, using read-only Windows queries only. The captured
/// program is never touched: its process is opened with PROCESS_QUERY_LIMITED_INFORMATION (the access
/// Task Manager uses) just to read the program's path, and nothing is loaded into it.
/// </summary>
public static partial class FullscreenDetector
{
    private const int GwlStyle = -16;
    private const long WsCaption = 0x00C00000;
    private const uint MonitorDefaultToNearest = 2;
    private const int QunsRunningD3DFullScreen = 3;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const string LayersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";

    /// <summary>Inspects a top-level window. Returns <see cref="FullscreenState.Unknown"/> when it has closed.</summary>
    public static FullscreenState Inspect(long handle, out bool minimized)
    {
        var hwnd = (nint)handle;
        minimized = false;
        if (!IsWindow(hwnd))
        {
            return FullscreenState.Unknown;
        }

        minimized = IsIconic(hwnd);
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var covers = false;
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (GetWindowRect(hwnd, out var w) && monitor != 0 && GetMonitorInfoW(monitor, ref info))
        {
            var m = info.rcMonitor;
            covers = FullscreenRules.CoversMonitor(new(w.Left, w.Top, w.Right, w.Bottom), new(m.Left, m.Top, m.Right, m.Bottom));
        }

        var hasCaption = (GetWindowLongPtrW(hwnd, GwlStyle) & WsCaption) == WsCaption;
        var exclusive = SHQueryUserNotificationState(out var state) == 0 && state == QunsRunningD3DFullScreen;
        var kind = FullscreenRules.Classify(minimized, GetForegroundWindow() == hwnd, hasCaption, covers, exclusive);

        var path = ExecutablePath(hwnd);
        return new FullscreenState(
            kind,
            monitor == 0 ? null : monitor,
            path is null ? null : Path.GetFileName(path),
            path is not null && OptimizationsDisabled(path));
    }

    /// <summary>The window's program path, or null when Windows does not allow reading it.</summary>
    private static string? ExecutablePath(nint hwnd)
    {
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == 0)
        {
            return null;
        }

        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            return QueryFullProcessImageNameW(process, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>"Disable fullscreen optimizations" is stored per program, for the user or for everyone.</summary>
    private static bool OptimizationsDisabled(string path)
    {
        foreach (var root in (ReadOnlySpan<RegistryKey>)[Registry.CurrentUser, Registry.LocalMachine])
        {
            try
            {
                using var key = root.OpenSubKey(LayersKey, writable: false);
                if (FullscreenRules.HasOptimizationsDisabled(key?.GetValue(path) as string))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Not readable: treat as not set.
            }
        }

        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint hMonitor, ref MONITORINFO info);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint hwnd, int index);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(nint process, uint flags, [Out] char[] name, ref uint size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}

// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;
using Linkast.Core;

namespace Linkast.Platform.Windows.Capture;

public sealed record MonitorInfo(int Index, long Handle, string DeviceName, int Width, int Height, bool IsPrimary)
{
    public override string ToString() => $"Display {Index + 1}: {Width}x{Height}{(IsPrimary ? " (primary)" : string.Empty)}";

    public DisplaySource ToSource(CaptureApi api) => new(Index, api, true, Handle);
}

/// <param name="ProcessId">The program that owns the window, for capturing its own sound.</param>
public sealed record WindowInfo(long Handle, string Title, string? ProcessName, uint ProcessId = 0)
{
    public override string ToString() => ProcessName is null ? Title : $"{Title}  [{ProcessName}]";

    public WindowSource ToSource() => new(Handle, Title, ProcessName);
}

/// <summary>Enumerates monitors and capturable top-level windows with Win32.</summary>
public static partial class VideoSourceCatalog
{
    private const uint MonitorInfoFPrimary = 1;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;
    private const int DwmwaCloaked = 14;

    /// <summary>
    /// Monitors in enumeration order. The index is also used as the Desktop Duplication output index,
    /// which matches DXGI order on single-GPU systems (see docs/risks.md for multi-GPU caveats).
    /// </summary>
    public static IReadOnlyList<MonitorInfo> ListMonitors()
    {
        var result = new List<MonitorInfo>();
        bool Callback(nint hMonitor, nint hdc, nint rect, nint data)
        {
            var info = new MONITORINFOEXW { cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>() };
            if (GetMonitorInfoW(hMonitor, ref info))
            {
                result.Add(new MonitorInfo(
                    result.Count,
                    hMonitor,
                    info.szDevice,
                    info.rcMonitor.Right - info.rcMonitor.Left,
                    info.rcMonitor.Bottom - info.rcMonitor.Top,
                    (info.dwFlags & MonitorInfoFPrimary) != 0));
            }

            return true;
        }

        var callback = new MonitorEnumProc(Callback);
        EnumDisplayMonitors(0, 0, callback, 0);
        GC.KeepAlive(callback);
        return result;
    }

    /// <summary>Visible, titled, non-tool, non-cloaked top-level windows, excluding this process.</summary>
    public static IReadOnlyList<WindowInfo> ListWindows()
    {
        var own = Environment.ProcessId;
        var result = new List<WindowInfo>();
        bool Callback(nint hwnd, nint data)
        {
            if (!IsWindowVisible(hwnd) || (GetWindowLongPtrW(hwnd, GwlExStyle) & WsExToolWindow) != 0)
            {
                return true;
            }

            if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return true;
            }

            var length = GetWindowTextLengthW(hwnd);
            if (length == 0)
            {
                return true;
            }

            var buffer = new char[length + 1];
            var copied = GetWindowTextW(hwnd, buffer, buffer.Length);
            var title = new string(buffer, 0, copied);
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            _ = GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == own)
            {
                return true;
            }

            string? processName = null;
            try
            {
                using var p = Process.GetProcessById((int)pid);
                processName = p.ProcessName + ".exe";
            }
            catch (ArgumentException)
            {
                // Process exited.
            }
            catch (InvalidOperationException)
            {
                // Access denied for some protected processes.
            }

            result.Add(new WindowInfo(hwnd, title, processName, pid));
            return true;
        }

        var callback = new EnumWindowsProc(Callback);
        EnumWindows(callback, 0);
        GC.KeepAlive(callback);
        return [.. result.OrderBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase).ThenBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase)];
    }

    public static bool WindowExists(long handle) => IsWindow((nint)handle);

    private delegate bool MonitorEnumProc(nint hMonitor, nint hdc, nint lprcMonitor, nint dwData);

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEXW
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

#pragma warning disable SYSLIB1054 // Delegate and by-value string marshalling need DllImport.
    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint hdc, nint lprcClip, MonitorEnumProc lpfnEnum, nint dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFOEXW lpmi);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);
#pragma warning restore SYSLIB1054

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint hwnd, int index);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowTextLengthW(nint hwnd);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowTextW(nint hwnd, [Out] char[] text, int maxCount);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
}

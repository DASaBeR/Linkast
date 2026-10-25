// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Linkast.Core.Camera;

namespace Linkast.Platform.Windows.Capture;

/// <summary>
/// Low-rate still snapshots of a monitor or window for the scene layout editor. This is a layout
/// reference only; the stream itself is captured by FFmpeg with WGC/DDA. Uses GDI, so hardware
/// accelerated exclusive-fullscreen games may appear black here even when the stream is fine.
/// </summary>
public static partial class ScreenSnapshot
{
    private const int SrcCopy = 0x00CC0020;
    private const int CaptureBlt = 0x40000000;
    private const uint PwRenderFullContent = 2;

    /// <summary>Captures a monitor, scaled to at most <paramref name="maxWidth"/> pixels wide.</summary>
    public static BgraFrame? CaptureMonitor(MonitorInfo monitor, int maxWidth = 640)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW((nint)monitor.Handle, ref info))
        {
            return null;
        }

        var r = info.rcMonitor;
        return Grab(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, maxWidth, window: 0);
    }

    /// <summary>Captures a window's contents (works for windows that are partly covered).</summary>
    public static BgraFrame? CaptureWindow(long handle, int maxWidth = 640)
    {
        if (!GetWindowRect((nint)handle, out var r))
        {
            return null;
        }

        return Grab(0, 0, r.Right - r.Left, r.Bottom - r.Top, maxWidth, window: (nint)handle);
    }

    private static BgraFrame? Grab(int x, int y, int width, int height, int maxWidth, nint window)
    {
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var outW = Math.Min(width, maxWidth);
        var outH = Math.Max(1, (int)((long)height * outW / width));

        var screenDc = GetDC(0);
        var memDc = CreateCompatibleDC(screenDc);
        var full = CreateCompatibleBitmap(screenDc, width, height);
        var small = CreateCompatibleBitmap(screenDc, outW, outH);
        var smallDc = CreateCompatibleDC(screenDc);
        try
        {
            var oldFull = SelectObject(memDc, full);
            var ok = window != 0
                ? PrintWindow(window, memDc, PwRenderFullContent)
                : BitBlt(memDc, 0, 0, width, height, screenDc, x, y, SrcCopy | CaptureBlt);
            if (!ok)
            {
                SelectObject(memDc, oldFull);
                return null;
            }

            var oldSmall = SelectObject(smallDc, small);
            _ = SetStretchBltMode(smallDc, 4); // HALFTONE
            StretchBlt(smallDc, 0, 0, outW, outH, memDc, 0, 0, width, height, SrcCopy);

            var frame = new BgraFrame(outW, outH);
            var bmi = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = outW,
                biHeight = -outH, // top-down
                biPlanes = 1,
                biBitCount = 32,
            };
            SelectObject(smallDc, oldSmall);
            var lines = GetDIBits(smallDc, small, 0, (uint)outH, frame.Pixels, ref bmi, 0);
            SelectObject(memDc, oldFull);
            if (lines == 0)
            {
                return null;
            }

            for (var i = 3; i < frame.Pixels.Length; i += 4)
            {
                frame.Pixels[i] = 255;
            }

            return frame;
        }
        finally
        {
            DeleteObject(full);
            DeleteObject(small);
            DeleteDC(memDc);
            DeleteDC(smallDc);
            _ = ReleaseDC(0, screenDc);
        }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint hMonitor, ref MONITORINFO info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrintWindow(nint hwnd, nint hdc, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hwnd, nint hdc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleBitmap(nint hdc, int width, int height);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint hdc, nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(nint hdc, int x, int y, int cx, int cy, nint hdcSrc, int x1, int y1, int rop);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StretchBlt(nint hdcDest, int xDest, int yDest, int wDest, int hDest, nint hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, int rop);

    [LibraryImport("gdi32.dll")]
    private static partial int SetStretchBltMode(nint hdc, int mode);

    [LibraryImport("gdi32.dll")]
    private static partial int GetDIBits(nint hdc, nint hbm, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint hdc);
}

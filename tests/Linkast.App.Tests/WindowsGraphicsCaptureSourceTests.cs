// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Linkast.Core.Rendering;
using Linkast.Platform.Windows.Rendering;

namespace Linkast.App.Tests;

public class WindowsGraphicsCaptureSourceTests
{
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    private const uint MONITOR_DEFAULTTOPRIMARY = 1;

    [Fact]
    public async Task WindowsGraphicsCaptureSource_starts_and_disposes_monitor_capture_cleanly()
    {
        var hMon = MonitorFromWindow(IntPtr.Zero, MONITOR_DEFAULTTOPRIMARY);
        Assert.NotEqual(IntPtr.Zero, hMon);

        using var deviceManager = new D3D11DeviceManager();
        var source = new WindowsGraphicsCaptureSource("test-mon", deviceManager, hMon, isWindow: false);

        try
        {
            Assert.Equal("test-mon", source.Id);
            Assert.Equal(SceneSourceState.Capturing, source.State);
            Assert.True(source.Width > 0);
            Assert.True(source.Height > 0);

            // Frame acquisition is non-blocking
            _ = await source.AcquireFrameAsync();
        }
        finally
        {
            await source.DisposeAsync();
        }

        Assert.Equal(SceneSourceState.Lost, source.State);
    }

    [Fact]
    public async Task WindowsGraphicsCaptureSource_marks_lost_for_invalid_window_handle()
    {
        using var deviceManager = new D3D11DeviceManager();
        var badHwnd = new IntPtr(0x12345678);
        var source = new WindowsGraphicsCaptureSource("invalid-win", deviceManager, badHwnd, isWindow: true);

        try
        {
            Assert.Equal("invalid-win", source.Id);
            Assert.Equal(SceneSourceState.Lost, source.State);
            var frame = await source.AcquireFrameAsync();
            Assert.Null(frame);
        }
        finally
        {
            await source.DisposeAsync();
        }

        Assert.Equal(SceneSourceState.Lost, source.State);
    }
}

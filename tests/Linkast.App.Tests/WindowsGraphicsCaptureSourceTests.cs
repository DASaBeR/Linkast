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

    [Fact]
    public async Task A_capture_that_Windows_ends_starts_again_on_its_own()
    {
        var hMon = MonitorFromWindow(IntPtr.Zero, MONITOR_DEFAULTTOPRIMARY);
        using var deviceManager = new D3D11DeviceManager();
        var source = new WindowsGraphicsCaptureSource("ended-mon", deviceManager, hMon, isWindow: false);
        var states = new System.Collections.Concurrent.ConcurrentQueue<SceneSourceState>();
        source.StateChanged += (_, s) => states.Enqueue(s);

        try
        {
            Assert.Equal(SceneSourceState.Capturing, source.State);

            // What Windows does when it ends a capture (a game changing display mode): the item's Closed event.
            typeof(WindowsGraphicsCaptureSource)
                .GetMethod("OnItemClosed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(source, [null, null]);
            Assert.Equal(SceneSourceState.Lost, source.State);

            var deadline = DateTime.UtcNow + WindowsGraphicsCaptureSource.RecoveryInterval * 5;
            while (source.State != SceneSourceState.Capturing && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            // Before, it stayed lost and the stream was black until the source was added again.
            Assert.Equal(SceneSourceState.Capturing, source.State);
            Assert.Equal([SceneSourceState.Lost, SceneSourceState.Capturing], states.ToArray());
            Assert.True(source.Width > 0);
        }
        finally
        {
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_lost_window_capture_keeps_trying_quietly_and_stops_when_disposed()
    {
        using var deviceManager = new D3D11DeviceManager();
        var source = new WindowsGraphicsCaptureSource("gone-win", deviceManager, new IntPtr(0x12345678), isWindow: true);
        var changes = 0;
        source.StateChanged += (_, _) => Interlocked.Increment(ref changes);

        // A window that is gone can't come back: the retries leave it lost without raising anything.
        await Task.Delay(WindowsGraphicsCaptureSource.RecoveryInterval * 2.5);
        Assert.Equal(SceneSourceState.Lost, source.State);
        Assert.Equal(0, Volatile.Read(ref changes));

        await source.DisposeAsync();
        await Task.Delay(WindowsGraphicsCaptureSource.RecoveryInterval * 1.5);
        Assert.Equal(SceneSourceState.Lost, source.State);
    }
}

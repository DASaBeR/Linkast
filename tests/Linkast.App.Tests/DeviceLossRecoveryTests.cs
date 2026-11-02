// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Linkast.Core.Camera;
using Linkast.Platform.Windows.Rendering;

namespace Linkast.App.Tests;

public class DeviceLossRecoveryTests
{
    [Fact]
    public async Task Compositor_and_sources_recover_cleanly_after_device_recreation()
    {
        using var manager = new D3D11DeviceManager();
        int width = 64;
        int height = 64;
        using var compositor = new D3D11SceneCompositor(manager, width, height);
        using var cache = new SourceFrameCache(manager);

        var oldDevice = manager.Device;
        var oldContext = manager.Context;
        var oldCanvas = compositor.CanvasTexture;

        byte[] red = new byte[32 * 32 * 4];
        Array.Fill(red, (byte)200);
        await using var imageSource = new ImageD3D11SceneSource("test-overlay", manager, red, 32, 32);

        var sources = new Dictionary<string, ID3D11SceneSource>(StringComparer.Ordinal)
        {
            ["test-overlay"] = imageSource
        };

        var placements = new Dictionary<string, NormalizedRect>(StringComparer.Ordinal)
        {
            ["test-overlay"] = NormalizedRect.Full
        };

        using var memoryStream = new MemoryStream();
        var scheduler = new MediaOutputScheduler(
            compositor,
            cache,
            () => new SceneLayout(),
            () => sources,
            memoryStream,
            targetFps: 60,
            placementsProvider: () => placements);

        // Render initial frame on device 1
        scheduler.RenderSingleFrame();
        Assert.Equal((long)compositor.Nv12ByteSize, memoryStream.Length);

        // Simulate device loss & restoration
        bool restored = manager.RecreateDevice();
        Assert.True(restored);
        Assert.NotSame(oldDevice, manager.Device);
        Assert.NotSame(oldContext, manager.Context);
        Assert.NotSame(oldCanvas, compositor.CanvasTexture);

        // Render frame on device 2
        memoryStream.SetLength(0);
        scheduler.RenderSingleFrame();
        Assert.Equal((long)compositor.Nv12ByteSize, memoryStream.Length);

        // Verify the source's texture was recreated on the new device
        var newSourceTex = await imageSource.AcquireFrameAsync();
        Assert.NotNull(newSourceTex);
        Assert.Equal(manager.Device.NativePointer, newSourceTex.Device.NativePointer);
    }

    [Fact]
    public async Task MediaOutputScheduler_render_loop_survives_and_recovers_from_device_loss()
    {
        using var manager = new D3D11DeviceManager();
        int width = 64;
        int height = 64;
        using var compositor = new D3D11SceneCompositor(manager, width, height);
        using var cache = new SourceFrameCache(manager);

        byte[] red = new byte[32 * 32 * 4];
        Array.Fill(red, (byte)200);
        await using var imageSource = new ImageD3D11SceneSource("test-overlay", manager, red, 32, 32);

        var sources = new Dictionary<string, ID3D11SceneSource>(StringComparer.Ordinal)
        {
            ["test-overlay"] = imageSource
        };

        var placements = new Dictionary<string, NormalizedRect>(StringComparer.Ordinal)
        {
            ["test-overlay"] = NormalizedRect.Full
        };

        using var memoryStream = new MemoryStream();
        var scheduler = new MediaOutputScheduler(
            compositor,
            cache,
            () => new SceneLayout(),
            () => sources,
            memoryStream,
            targetFps: 60,
            placementsProvider: () => placements);

        scheduler.Start();
        Assert.True(scheduler.IsRunning);

        // Wait for loop to produce initial frames
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (scheduler.FramesRendered < 2 && deadline.Elapsed < TimeSpan.FromSeconds(3))
        {
            await Task.Delay(10);
        }
        Assert.True(scheduler.FramesRendered >= 2);
        long initialFrames = scheduler.FramesRendered;

        // Trigger device loss & restoration
        bool restored = manager.RecreateDevice();
        Assert.True(restored);

        // Verify scheduler loop stays alive and continues incrementing FramesRendered
        deadline.Restart();
        while (scheduler.FramesRendered < initialFrames + 2 && deadline.Elapsed < TimeSpan.FromSeconds(3))
        {
            await Task.Delay(10);
        }

        Assert.True(scheduler.IsRunning);
        Assert.True(scheduler.FramesRendered > initialFrames);

        await scheduler.DisposeAsync();
        Assert.False(scheduler.IsRunning);
    }
}

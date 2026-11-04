// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Linkast.Core.Camera;
using Linkast.Platform.Windows.Rendering;

namespace Linkast.App.Tests;

public class MediaOutputSchedulerTests
{
    [Fact]
    public void MediaOutputScheduler_renders_single_frame_to_stream_correctly()
    {
        using var manager = new D3D11DeviceManager();
        int width = 64;
        int height = 64;
        using var compositor = new D3D11SceneCompositor(manager, width, height);
        using var cache = new SourceFrameCache();

        var layout = new SceneLayout
        {
            Name = "TestLayout",
            ShowCapture = false
        };

        using var memoryStream = new MemoryStream();
        var scheduler = new MediaOutputScheduler(
            compositor,
            cache,
            () => layout,
            () => new Dictionary<string, ID3D11SceneSource>(),
            memoryStream,
            targetFps: 60);

        scheduler.RenderSingleFrame();

        Assert.Equal((long)compositor.Nv12ByteSize, memoryStream.Length);
    }

    [Fact]
    public async Task MediaOutputScheduler_runs_render_loop_and_stops_cleanly()
    {
        using var manager = new D3D11DeviceManager();
        int width = 64;
        int height = 64;
        using var compositor = new D3D11SceneCompositor(manager, width, height);
        using var cache = new SourceFrameCache();

        var layout = new SceneLayout
        {
            Name = "TestLayout",
            ShowCapture = false
        };

        using var memoryStream = new MemoryStream();
        var scheduler = new MediaOutputScheduler(
            compositor,
            cache,
            () => layout,
            () => new Dictionary<string, ID3D11SceneSource>(),
            memoryStream,
            targetFps: 60);

        scheduler.Start();
        Assert.True(scheduler.IsRunning);

        // Allow ~100 ms for loop to tick
        await Task.Delay(100);

        await scheduler.DisposeAsync();
        Assert.False(scheduler.IsRunning);
        Assert.True(scheduler.FramesRendered > 0);
    }

    [Fact]
    public async Task MediaOutputScheduler_renders_layers_in_z_stack_order_and_skips_hidden()
    {
        using var manager = new D3D11DeviceManager();
        int width = 64;
        int height = 64;
        using var compositor = new D3D11SceneCompositor(manager, width, height);
        using var cache = new SourceFrameCache();

        var layout = new SceneLayout { Name = "LiveScene" };

        byte[] blue = new byte[32 * 32 * 4];
        byte[] red = new byte[32 * 32 * 4];
        byte[] green = new byte[16 * 16 * 4];
        Array.Fill(blue, (byte)200);
        Array.Fill(red, (byte)150);
        Array.Fill(green, (byte)100);

        await using var bgSource = new ImageD3D11SceneSource("bg-media", manager, blue, 32, 32);
        await using var camSource = new ImageD3D11SceneSource("camera-1", manager, red, 32, 32);
        await using var logoSource = new ImageD3D11SceneSource("overlay-logo", manager, green, 16, 16);

        var sources = new Dictionary<string, ID3D11SceneSource>(StringComparer.Ordinal)
        {
            ["bg-media"] = bgSource,
            ["camera-1"] = camSource,
            ["overlay-logo"] = logoSource,
        };

        var placements = new Dictionary<string, NormalizedRect>(StringComparer.Ordinal)
        {
            ["bg-media"] = NormalizedRect.Full,
            ["camera-1"] = new NormalizedRect(0, 0, 0.5, 0.5),
            ["overlay-logo"] = new NormalizedRect(0.25, 0.25, 0.5, 0.5),
        };

        var crops = new Dictionary<string, NormalizedRect>(StringComparer.Ordinal)
        {
            ["camera-1"] = NormalizedRect.Full,
        };

        var stackOrder = new List<string> { "bg-media", "camera-1", "overlay-logo" };

        using var memoryStream = new MemoryStream();
        var scheduler = new MediaOutputScheduler(
            compositor,
            cache,
            () => layout,
            () => sources,
            memoryStream,
            targetFps: 60,
            cropsProvider: () => crops,
            placementsProvider: () => placements,
            stackOrderProvider: () => stackOrder);

        // Render frame with all 3 layers active
        scheduler.RenderSingleFrame();
        Assert.Equal((long)compositor.Nv12ByteSize, memoryStream.Length);

        // Hide camera-1 by removing from placements
        placements.Remove("camera-1");
        memoryStream.SetLength(0);
        scheduler.RenderSingleFrame();
        Assert.Equal((long)compositor.Nv12ByteSize, memoryStream.Length);
    }

    [Fact]
    public async Task MediaOutputScheduler_applies_a_reordered_stack_with_the_same_layers()
    {
        using var manager = new D3D11DeviceManager();
        using var compositor = new D3D11SceneCompositor(manager, 64, 64);
        using var cache = new SourceFrameCache();
        var layout = new SceneLayout { Name = "Reorder" };

        byte[] white = new byte[16 * 16 * 4];
        Array.Fill(white, (byte)255);
        byte[] black = new byte[16 * 16 * 4];
        for (int i = 3; i < black.Length; i += 4)
        {
            black[i] = 255;
        }

        await using var a = new ImageD3D11SceneSource("a", manager, white, 16, 16);
        await using var b = new ImageD3D11SceneSource("b", manager, black, 16, 16);
        var sources = new Dictionary<string, ID3D11SceneSource>(StringComparer.Ordinal) { ["a"] = a, ["b"] = b };
        var placements = new Dictionary<string, NormalizedRect>(StringComparer.Ordinal)
        {
            ["a"] = NormalizedRect.Full,
            ["b"] = NormalizedRect.Full,
        };

        IReadOnlyList<string> stack = new[] { "a", "b" };
        using var output = new MemoryStream();
        var scheduler = new MediaOutputScheduler(
            compositor, cache, () => layout, () => sources, output, targetFps: 60,
            placementsProvider: () => placements,
            stackOrderProvider: () => stack);

        // NV12 readback is double-buffered, so a frame appears one render late.
        scheduler.RenderSingleFrame();
        output.SetLength(0);
        scheduler.RenderSingleFrame();
        byte topIsBlack = output.ToArray()[64 * 32 + 32];

        stack = new[] { "b", "a" };
        scheduler.RenderSingleFrame();
        output.SetLength(0);
        scheduler.RenderSingleFrame();
        byte topIsWhite = output.ToArray()[64 * 32 + 32];

        Assert.True(topIsBlack < 100, $"Y was {topIsBlack}");
        Assert.True(topIsWhite > 150, $"Y was {topIsWhite}");
    }

    [Fact]
    public async Task MediaOutputScheduler_GetOrderedKeys_produces_identical_order_regardless_of_sources_enumeration_order()
    {
        using var manager = new D3D11DeviceManager();
        using var compositor = new D3D11SceneCompositor(manager, 64, 64);
        using var cache = new SourceFrameCache();
        var layout = new SceneLayout { Name = "CardAndCam" };

        byte[] pixels = new byte[16 * 16 * 4];
        await using var card = new ImageD3D11SceneSource("scene-media-card", manager, pixels, 16, 16);
        await using var cam = new ImageD3D11SceneSource("camera-1", manager, pixels, 16, 16);

        var sourcesOrder1 = new Dictionary<string, ID3D11SceneSource>(StringComparer.Ordinal)
        {
            ["scene-media-card"] = card,
            ["camera-1"] = cam,
        };

        var sourcesOrder2 = new Dictionary<string, ID3D11SceneSource>(StringComparer.Ordinal)
        {
            ["camera-1"] = cam,
            ["scene-media-card"] = card,
        };

        var stackOrder = new List<string> { "scene-media-card", "camera-1" };

        using var output = new MemoryStream();
        var scheduler = new MediaOutputScheduler(
            compositor, cache, () => layout, () => sourcesOrder1, output, targetFps: 60,
            stackOrderProvider: () => stackOrder);

        var keys1 = scheduler.GetOrderedKeys(sourcesOrder1, stackOrder);
        var keys2 = scheduler.GetOrderedKeys(sourcesOrder2, stackOrder);

        Assert.Equal(["scene-media-card", "camera-1"], keys1);
        Assert.Equal(["scene-media-card", "camera-1"], keys2);
    }
}

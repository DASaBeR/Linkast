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
}

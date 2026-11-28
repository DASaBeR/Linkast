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

    [Fact]
    public async Task A_capture_that_cut_out_shows_black_not_what_is_under_it()
    {
        using var manager = new D3D11DeviceManager();
        using var compositor = new D3D11SceneCompositor(manager, 64, 64);
        using var cache = new SourceFrameCache();
        var layout = new SceneLayout { Name = "Game" };

        byte[] white = new byte[16 * 16 * 4];
        Array.Fill(white, (byte)255);
        await using var background = new ImageD3D11SceneSource("bg", manager, white, 16, 16);
        // A game window that is gone: the capture is lost.
        var game = new WindowsGraphicsCaptureSource("w1", manager, new IntPtr(0x12345678), isWindow: true);
        Assert.Equal(Linkast.Core.Rendering.SceneSourceState.Lost, game.State);

        var sources = new Dictionary<string, ID3D11SceneSource>(StringComparer.Ordinal) { ["bg"] = background, ["w1"] = game };
        var placements = new Dictionary<string, NormalizedRect>(StringComparer.Ordinal)
        {
            ["bg"] = NormalizedRect.Full,
            ["w1"] = new NormalizedRect(0, 0, 0.5, 1), // the left half
        };
        IReadOnlyList<string> stack = ["bg", "w1"];
        using var output = new MemoryStream();
        var scheduler = new MediaOutputScheduler(
            compositor, cache, () => layout, () => sources, output, targetFps: 60,
            placementsProvider: () => placements,
            stackOrderProvider: () => stack);

        try
        {
            // NV12 readback is double-buffered, so a frame appears one render late.
            scheduler.RenderSingleFrame();
            output.SetLength(0);
            scheduler.RenderSingleFrame();
            var y = output.ToArray();

            Assert.True(y[(64 * 32) + 16] < 20, $"Y in the capture's place was {y[(64 * 32) + 16]}"); // before: white
            Assert.True(y[(64 * 32) + 48] > 200, $"Y beside it was {y[(64 * 32) + 48]}");
        }
        finally
        {
            await game.DisposeAsync();
        }
    }

    [Fact]
    public void Where_no_layer_is_the_picture_is_black()
    {
        using var manager = new D3D11DeviceManager();
        using var compositor = new D3D11SceneCompositor(manager, 64, 64);
        using var cache = new SourceFrameCache();
        using var output = new MemoryStream();
        var scheduler = new MediaOutputScheduler(
            compositor, cache, () => new SceneLayout { Name = "Empty", ShowCapture = false },
            () => new Dictionary<string, ID3D11SceneSource>(), output, targetFps: 60);

        scheduler.RenderSingleFrame();
        output.SetLength(0);
        scheduler.RenderSingleFrame();

        var y = output.ToArray()[(64 * 32) + 32];
        Assert.True(y <= 17, $"Y was {y} (black is 16; the old navy was about 27)");
    }

    [Fact]
    public void MediaOutputScheduler_writes_to_primary_and_scaled_secondary_targets()
    {
        using var manager = new D3D11DeviceManager();
        using var compositor = new D3D11SceneCompositor(manager, 1920, 1080);
        using var cache = new SourceFrameCache();

        using var primaryStream = new MemoryStream();
        using var secondaryStream = new MemoryStream();
        using var primary = new D3D11OutputTarget(manager, "stream", 1920, 1080, primaryStream, isPrimary: true);
        using var secondary = new D3D11OutputTarget(manager, "recording", 1280, 720, secondaryStream, isPrimary: false);

        var scheduler = new MediaOutputScheduler(
            compositor,
            cache,
            () => new SceneLayout { Name = "Dual", ShowCapture = false },
            () => new Dictionary<string, ID3D11SceneSource>(),
            [primary, secondary],
            targetFps: 60);

        const int frames = 3;
        for (int i = 0; i < frames; i++)
        {
            Assert.True(scheduler.RenderSingleFrame());
        }

        Assert.Equal(frames * (long)primary.Nv12ByteSize, primaryStream.Length);
        Assert.Equal(frames * (long)secondary.Nv12ByteSize, secondaryStream.Length);
    }

    [Fact]
    public void MediaOutputScheduler_detaches_failing_secondary_target_without_stopping_primary()
    {
        using var manager = new D3D11DeviceManager();
        using var compositor = new D3D11SceneCompositor(manager, 64, 64);
        using var cache = new SourceFrameCache();

        using var primaryStream = new MemoryStream();
        using var secondaryStream = new ThrowingWriteStream();
        using var primary = new D3D11OutputTarget(manager, "stream", 64, 64, primaryStream, isPrimary: true);
        using var secondary = new D3D11OutputTarget(manager, "recording", 64, 64, secondaryStream, isPrimary: false);

        var scheduler = new MediaOutputScheduler(
            compositor,
            cache,
            () => new SceneLayout { Name = "Dual", ShowCapture = false },
            () => new Dictionary<string, ID3D11SceneSource>(),
            [primary, secondary],
            targetFps: 60);

        var detached = new List<string>();
        scheduler.OutputDetached += (_, e) => detached.Add(e.Name);

        Assert.True(scheduler.RenderSingleFrame());
        Assert.True(scheduler.RenderSingleFrame());

        Assert.False(secondary.IsAttached);
        Assert.True(primary.IsAttached);
        Assert.Equal(["recording"], detached);
        Assert.Equal(2 * (long)primary.Nv12ByteSize, primaryStream.Length);
    }

    [Fact]
    public async Task MediaOutputScheduler_ends_render_loop_when_primary_target_throws_IOException()
    {
        using var manager = new D3D11DeviceManager();
        using var compositor = new D3D11SceneCompositor(manager, 64, 64);
        using var cache = new SourceFrameCache();

        using var primaryStream = new ThrowingWriteStream();
        using var primary = new D3D11OutputTarget(manager, "stream", 64, 64, primaryStream, isPrimary: true);

        await using var scheduler = new MediaOutputScheduler(
            compositor,
            cache,
            () => new SceneLayout { Name = "FailPrimary", ShowCapture = false },
            () => new Dictionary<string, ID3D11SceneSource>(),
            [primary],
            targetFps: 60);

        scheduler.Start();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (scheduler.IsRunning && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.False(scheduler.IsRunning);
    }

    private sealed class ThrowingWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Broken pipe");
        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("Broken pipe");
    }

    [Fact]
    public void MediaOutputScheduler_acquires_shared_source_once_across_two_canvases()
    {
        using var manager = new D3D11DeviceManager();
        using var mainCompositor = new D3D11SceneCompositor(manager, 64, 64);
        using var vertCompositor = new D3D11SceneCompositor(manager, 64, 112);
        using var cache = new SourceFrameCache(manager);

        using var shared = new CountingWhiteSource("shared", manager);
        using var vertOnly = new CountingWhiteSource("vert-only", manager);
        var sources = new Dictionary<string, ID3D11SceneSource>(StringComparer.Ordinal)
        {
            ["shared"] = shared,
            ["vert-only"] = vertOnly,
        };

        var mainPlacements = new Dictionary<string, NormalizedRect>(StringComparer.Ordinal)
        {
            ["shared"] = NormalizedRect.Full,
        };
        var vertPlacements = new Dictionary<string, NormalizedRect>(StringComparer.Ordinal)
        {
            ["shared"] = new NormalizedRect(0, 0, 1, 0.5),
            ["vert-only"] = new NormalizedRect(0, 0.5, 1, 0.5),
        };
        var emptyCrops = new Dictionary<string, NormalizedRect>(StringComparer.Ordinal);
        IReadOnlyList<string> mainStack = ["shared"];
        IReadOnlyList<string> vertStack = ["shared", "vert-only"];

        using var mainStream = new MemoryStream();
        using var vertStream = new MemoryStream();
        using var mainTarget = new D3D11OutputTarget(manager, "stream", 64, 64, mainStream, isPrimary: true);
        using var vertTarget = new D3D11OutputTarget(manager, "vertical", 64, 112, vertStream, isPrimary: false);

        var scheduler = new MediaOutputScheduler(
            mainCompositor,
            cache,
            () => new SceneLayout { Name = "Main", ShowCapture = false },
            () => sources,
            [mainTarget],
            targetFps: 60,
            cropsProvider: () => emptyCrops,
            placementsProvider: () => mainPlacements,
            stackOrderProvider: () => mainStack);

        scheduler.AddCanvas(new SchedulerCanvas(
            "vertical",
            vertCompositor,
            () => vertPlacements,
            () => emptyCrops,
            () => vertStack,
            [vertTarget]));

        // Run 2 ticks because NV12 staging readback is double-buffered.
        Assert.True(scheduler.RenderSingleFrame());
        mainStream.SetLength(0);
        vertStream.SetLength(0);
        Assert.True(scheduler.RenderSingleFrame());

        Assert.Equal(2, shared.AcquireCount);
        Assert.Equal(2, vertOnly.AcquireCount);
        Assert.Equal((long)mainTarget.Nv12ByteSize, mainStream.Length);
        Assert.Equal((long)vertTarget.Nv12ByteSize, vertStream.Length);
    }

    [Fact]
    public void MediaOutputScheduler_draws_vertical_only_source_only_on_vertical_canvas()
    {
        using var manager = new D3D11DeviceManager();
        using var mainCompositor = new D3D11SceneCompositor(manager, 64, 64);
        using var vertCompositor = new D3D11SceneCompositor(manager, 64, 64);
        using var cache = new SourceFrameCache(manager);

        using var vertOnly = new CountingWhiteSource("vert-only", manager);
        var sources = new Dictionary<string, ID3D11SceneSource>(StringComparer.Ordinal) { ["vert-only"] = vertOnly };
        var mainPlacements = new Dictionary<string, NormalizedRect>(StringComparer.Ordinal);
        var vertPlacements = new Dictionary<string, NormalizedRect>(StringComparer.Ordinal) { ["vert-only"] = NormalizedRect.Full };
        var emptyCrops = new Dictionary<string, NormalizedRect>(StringComparer.Ordinal);
        IReadOnlyList<string> mainStack = [];
        IReadOnlyList<string> vertStack = ["vert-only"];

        using var mainStream = new MemoryStream();
        using var vertStream = new MemoryStream();
        using var mainTarget = new D3D11OutputTarget(manager, "stream", 64, 64, mainStream, isPrimary: true);
        using var vertTarget = new D3D11OutputTarget(manager, "vertical", 64, 64, vertStream, isPrimary: false);

        var scheduler = new MediaOutputScheduler(
            mainCompositor,
            cache,
            () => new SceneLayout { Name = "Main", ShowCapture = false },
            () => sources,
            [mainTarget],
            targetFps: 60,
            cropsProvider: () => emptyCrops,
            placementsProvider: () => mainPlacements,
            stackOrderProvider: () => mainStack);

        scheduler.AddCanvas(new SchedulerCanvas(
            "vertical",
            vertCompositor,
            () => vertPlacements,
            () => emptyCrops,
            () => vertStack,
            [vertTarget]));

        scheduler.RenderSingleFrame();
        mainStream.SetLength(0);
        vertStream.SetLength(0);
        scheduler.RenderSingleFrame();

        // Main canvas has no placement for vert-only -> black (Y=16); vertical canvas -> white (Y=235).
        byte mainY = mainStream.ToArray()[64 * 32 + 32];
        byte vertY = vertStream.ToArray()[64 * 32 + 32];
        Assert.True(mainY <= 17, $"Main Y was {mainY}");
        Assert.True(vertY >= 230, $"Vertical Y was {vertY}");
    }

    private sealed class CountingWhiteSource : ID3D11SceneSource, IDisposable
    {
        private readonly Vortice.Direct3D11.ID3D11Texture2D _texture;

        public CountingWhiteSource(string id, D3D11DeviceManager manager)
        {
            Id = id;
            var desc = new Vortice.Direct3D11.Texture2DDescription
            {
                Width = 16,
                Height = 16,
                MipLevels = 1,
                ArraySize = 1,
                Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                Usage = Vortice.Direct3D11.ResourceUsage.Default,
                BindFlags = Vortice.Direct3D11.BindFlags.ShaderResource | Vortice.Direct3D11.BindFlags.RenderTarget,
            };
            _texture = manager.Device.CreateTexture2D(desc);
            using var rtv = manager.Device.CreateRenderTargetView(_texture);
            manager.Context.ClearRenderTargetView(rtv, new Vortice.Mathematics.Color4(1, 1, 1, 1));
        }

        public string Id { get; }
        public Linkast.Core.Rendering.SceneSourceState State => Linkast.Core.Rendering.SceneSourceState.Capturing;
        public event EventHandler<Linkast.Core.Rendering.SceneSourceState>? StateChanged { add { } remove { } }
        public int AcquireCount { get; private set; }

        public ValueTask<Vortice.Direct3D11.ID3D11Texture2D?> AcquireFrameAsync(CancellationToken ct = default)
        {
            AcquireCount++;
            return ValueTask.FromResult<Vortice.Direct3D11.ID3D11Texture2D?>(_texture);
        }

        public ValueTask DisposeAsync()
        {
            _texture.Dispose();
            return ValueTask.CompletedTask;
        }

        public void Dispose() => _texture.Dispose();
    }
}

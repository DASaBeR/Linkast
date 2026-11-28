// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Linkast.Core.Camera;
using Linkast.Platform.Windows.Rendering;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Linkast.App.Tests;

public class D3D11SceneCompositorTests
{
    [Fact]
    public void D3D11SceneCompositor_initializes_and_calculates_frame_dimensions()
    {
        using var manager = new D3D11DeviceManager();
        using var compositor = new D3D11SceneCompositor(manager, 1920, 1080);

        Assert.Equal(1920, compositor.CanvasWidth);
        Assert.Equal(1080, compositor.CanvasHeight);
        Assert.Equal((uint)(1920 * 1080 * 1.5), compositor.Nv12ByteSize);
        Assert.NotNull(compositor.CanvasTexture);
    }

    [Fact]
    public void D3D11SceneCompositor_resizes_canvas_and_recalculates_buffer_size()
    {
        using var manager = new D3D11DeviceManager();
        using var compositor = new D3D11SceneCompositor(manager, 1920, 1080);

        compositor.ResizeCanvas(1280, 720);
        Assert.Equal(1280, compositor.CanvasWidth);
        Assert.Equal(720, compositor.CanvasHeight);
        Assert.Equal((uint)(1280 * 720 * 1.5), compositor.Nv12ByteSize);
    }

    [Fact]
    public void D3D11SceneCompositor_renders_layer_and_outputs_NV12_to_span()
    {
        using var manager = new D3D11DeviceManager();
        int width = 64;
        int height = 64;
        using var compositor = new D3D11SceneCompositor(manager, width, height);

        // Clear to black
        compositor.BeginFrame(new Color4(0, 0, 0, 1));

        // Create 32x32 red layer
        var layerDesc = new Texture2DDescription
        {
            Width = 32,
            Height = 32,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
        };
        using var layerTex = manager.Device.CreateTexture2D(layerDesc);
        using var layerRtv = manager.Device.CreateRenderTargetView(layerTex);
        manager.Context.ClearRenderTargetView(layerRtv, new Color4(1, 0, 0, 1));

        // Draw red layer in the center (0.25, 0.25, 0.5, 0.5)
        compositor.DrawLayer(layerTex, new NormalizedRect(0.25, 0.25, 0.5, 0.5), NormalizedRect.Full, 1.0f);

        // Convert to NV12
        byte[] nv12Bytes = new byte[compositor.Nv12ByteSize];
        int written = compositor.ConvertToNv12(nv12Bytes);
        Assert.Equal((int)compositor.Nv12ByteSize, written);

        // Top-left pixel (0, 0) should be Black (Y = 16)
        Assert.Equal(16, nv12Bytes[0]);

        // Center pixel (32, 32) should be Red:
        // BT.709 Y for Red(1, 0, 0) = 16 + 65.481 = 81.481 -> ~81
        int centerIndex = 32 * width + 32;
        Assert.InRange(nv12Bytes[centerIndex], 78, 84);
    }

    [Fact]
    public void D3D11SceneCompositor_writes_NV12_directly_to_stream()
    {
        using var manager = new D3D11DeviceManager();
        int width = 64;
        int height = 64;
        using var compositor = new D3D11SceneCompositor(manager, width, height);

        compositor.BeginFrame(new Color4(1, 1, 1, 1)); // Pure white

        using var memoryStream = new MemoryStream();
        int written = compositor.ConvertToNv12(memoryStream);

        Assert.Equal((int)compositor.Nv12ByteSize, written);
        Assert.Equal(compositor.Nv12ByteSize, (uint)memoryStream.Length);

        // For white, all Y bytes should be 235
        byte[] data = memoryStream.ToArray();
        for (int i = 0; i < width * height; i++)
        {
            Assert.Equal(235, data[i]);
        }
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(852, 480)]
    public void D3D11SceneCompositor_solid_red_has_uniform_Y_across_rows(int width, int height)
    {
        using var manager = new D3D11DeviceManager();
        using var compositor = new D3D11SceneCompositor(manager, width, height);

        byte[] nv12 = new byte[compositor.Nv12ByteSize];
        compositor.BeginFrame(new Color4(1, 0, 0, 1));
        compositor.ConvertToNv12(nv12);
        compositor.BeginFrame(new Color4(1, 0, 0, 1));
        int written = compositor.ConvertToNv12(nv12);
        Assert.Equal((int)compositor.Nv12ByteSize, written);

        byte expected = nv12[0];
        Assert.NotEqual(0, expected);
        foreach (int row in new[] { 0, 1, 2, 3, height - 1 })
        {
            int offset = row * width;
            for (int x = 0; x < width; x++)
            {
                Assert.True(nv12[offset + x] == expected, $"Row {row}, x={x}: expected Y={expected}, got {nv12[offset + x]}");
            }
        }
    }

    [Fact]
    public void D3D11OutputTarget_scales_four_quadrants_from_1080p_to_720p()
    {
        using var manager = new D3D11DeviceManager();
        using var canvas = new D3D11SceneCompositor(manager, 1920, 1080);
        using var reference720 = new D3D11SceneCompositor(manager, 1280, 720);
        using var scaledStream = new MemoryStream();
        using var target = new D3D11OutputTarget(manager, "recording", 1280, 720, scaledStream, isPrimary: false);

        void DrawQuadrants(D3D11SceneCompositor c)
        {
            c.BeginFrame(new Color4(0, 0, 0, 1));
            DrawSolidLayer(manager, c, new Color4(1, 0, 0, 1), new NormalizedRect(0, 0, 0.5, 0.5));       // Top-left: red
            DrawSolidLayer(manager, c, new Color4(0, 1, 0, 1), new NormalizedRect(0.5, 0, 0.5, 0.5));     // Top-right: green
            DrawSolidLayer(manager, c, new Color4(0, 0, 1, 1), new NormalizedRect(0, 0.5, 0.5, 0.5));     // Bottom-left: blue
            DrawSolidLayer(manager, c, new Color4(1, 1, 1, 1), new NormalizedRect(0.5, 0.5, 0.5, 0.5));   // Bottom-right: white
        }

        // Double-buffered NV12 readback: run twice and read the second frame.
        byte[] refBytes = new byte[reference720.Nv12ByteSize];
        DrawQuadrants(reference720);
        reference720.ConvertToNv12(refBytes);
        DrawQuadrants(reference720);
        reference720.ConvertToNv12(refBytes);

        DrawQuadrants(canvas);
        Assert.True(target.WriteFrame(canvas));
        scaledStream.SetLength(0);
        DrawQuadrants(canvas);
        Assert.True(target.WriteFrame(canvas));

        byte[] scaledBytes = scaledStream.ToArray();
        Assert.Equal(refBytes.Length, scaledBytes.Length);

        (int X, int Y)[] centers = [(320, 180), (960, 180), (320, 540), (960, 540)];
        foreach (var (x, y) in centers)
        {
            int idx = y * 1280 + x;
            Assert.InRange(scaledBytes[idx], refBytes[idx] - 2, refBytes[idx] + 2);
        }
    }

    [Fact]
    public void D3D11OutputTarget_one_to_one_is_byte_identical_to_ConvertToNv12()
    {
        using var manager = new D3D11DeviceManager();
        using var directCanvas = new D3D11SceneCompositor(manager, 640, 360);
        using var targetCanvas = new D3D11SceneCompositor(manager, 640, 360);
        using var directStream = new MemoryStream();
        using var targetStream = new MemoryStream();
        using var target = new D3D11OutputTarget(manager, "stream", 640, 360, targetStream, isPrimary: true);

        for (int i = 0; i < 2; i++)
        {
            directStream.SetLength(0);
            targetStream.SetLength(0);

            directCanvas.BeginFrame(new Color4(0.25f, 0.5f, 0.75f, 1f));
            directCanvas.ConvertToNv12(directStream);

            targetCanvas.BeginFrame(new Color4(0.25f, 0.5f, 0.75f, 1f));
            target.WriteFrame(targetCanvas);
        }

        Assert.Equal(directStream.ToArray(), targetStream.ToArray());
    }

    private static void DrawSolidLayer(D3D11DeviceManager manager, D3D11SceneCompositor compositor, Color4 color, NormalizedRect dest)
    {
        var desc = new Texture2DDescription
        {
            Width = 16,
            Height = 16,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        };
        using var tex = manager.Device.CreateTexture2D(desc);
        using var rtv = manager.Device.CreateRenderTargetView(tex);
        manager.Context.ClearRenderTargetView(rtv, color);
        compositor.DrawLayer(tex, dest, NormalizedRect.Full);
    }
}

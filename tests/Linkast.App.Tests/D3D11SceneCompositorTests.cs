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
}

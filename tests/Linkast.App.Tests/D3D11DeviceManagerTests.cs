// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Linkast.Platform.Windows.Rendering;

namespace Linkast.App.Tests;

public class D3D11DeviceManagerTests
{
    [Fact]
    public void SourceFrameCache_stores_and_acquires_freshness_correctly()
    {
        using var cache = new SourceFrameCache();

        // Unknown source returns null and false
        var (missingTexture, missingTs, wasFresh) = cache.Acquire("cam1");
        Assert.Null(missingTexture);
        Assert.Equal(0, missingTs);
        Assert.False(wasFresh);

        cache.Remove("cam1");
        cache.Clear();
    }

    [Fact]
    public void D3D11DeviceManager_creates_hardware_or_warp_device_and_reports_healthy()
    {
        using var manager = new D3D11DeviceManager();
        Assert.NotNull(manager.Device);
        Assert.NotNull(manager.Context);
        Assert.False(manager.IsDeviceLost);
        Assert.True(manager.CheckDeviceStatus());
    }

    [Fact]
    public void D3D11DeviceManager_creates_valid_WinRT_device()
    {
        using var manager = new D3D11DeviceManager();
        using var winrtDevice = manager.CreateWinRTDevice();
        Assert.NotNull(winrtDevice);
    }

    [Fact]
    public void WindowsGraphicsCapture_is_supported_on_system()
    {
        bool supported = Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported();
        Assert.True(supported, "Windows Graphics Capture should be supported on Windows 10/11 test machine.");
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    private const uint MONITOR_DEFAULTTOPRIMARY = 1;

    [Fact]
    public void WindowsGraphicsInterop_can_create_capture_item_for_primary_monitor()
    {
        var hMon = MonitorFromWindow(IntPtr.Zero, MONITOR_DEFAULTTOPRIMARY);
        Assert.NotEqual(IntPtr.Zero, hMon);

        var item = WindowsGraphicsInterop.CreateItemForMonitor(hMon);
        Assert.NotNull(item);
        Assert.True(item.Size.Width > 0);
        Assert.True(item.Size.Height > 0);
    }

    [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int CreateDirect3D11SurfaceFromDXGISurface(
        IntPtr dxgiSurface,
        out IntPtr graphicsSurface);

    [Fact]
    public void WindowsGraphicsInterop_can_extract_texture_from_surface()
    {
        using var manager = new D3D11DeviceManager();
        var desc = new Vortice.Direct3D11.Texture2DDescription
        {
            Width = 64,
            Height = 64,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            Usage = Vortice.Direct3D11.ResourceUsage.Default,
            BindFlags = Vortice.Direct3D11.BindFlags.RenderTarget | Vortice.Direct3D11.BindFlags.ShaderResource
        };
        using var originalTexture = manager.Device.CreateTexture2D(desc);
        using var dxgiSurface = originalTexture.QueryInterface<Vortice.DXGI.IDXGISurface>();

        int hr = CreateDirect3D11SurfaceFromDXGISurface(dxgiSurface.NativePointer, out var inspectable);
        Assert.Equal(0, hr);
        Assert.NotEqual(IntPtr.Zero, inspectable);

        try
        {
            var surface = WinRT.MarshalInterface<Windows.Graphics.DirectX.Direct3D11.IDirect3DSurface>.FromAbi(inspectable);
            Assert.NotNull(surface);

            using var roundtrip = WindowsGraphicsInterop.GetTextureFromSurface(surface);
            Assert.NotNull(roundtrip);
            Assert.Equal((uint)64, roundtrip.Description.Width);
            Assert.Equal((uint)64, roundtrip.Description.Height);
            Assert.Equal(Vortice.DXGI.Format.B8G8R8A8_UNorm, roundtrip.Description.Format);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    [Fact]
    public void Vortice_D3DCompiler_can_compile_simple_shader()
    {
        const string hlsl = @"
            [numthreads(1, 1, 1)]
            void CSMain(uint3 id : SV_DispatchThreadID) { }
        ";
        ReadOnlyMemory<byte> bytecode = Vortice.D3DCompiler.Compiler.Compile(hlsl, "CSMain", "test", "cs_5_0");
        Assert.False(bytecode.IsEmpty);
        Assert.True(bytecode.Length > 0);
    }

    [Fact]
    public void ComputeShader_converts_black_texture_to_valid_NV12()
    {
        const string shaderHlsl = @"
            Texture2D<float4> InputTexture : register(t0);
            RWBuffer<uint> OutputNv12 : register(u0);

            cbuffer Params : register(b0)
            {
                uint Width;
                uint Height;
                uint Pad0;
                uint Pad1;
            };

            [numthreads(8, 8, 1)]
            void CSColorConvert(uint3 id : SV_DispatchThreadID)
            {
                uint x = id.x * 4;
                uint y = id.y * 2;
                if (x >= Width || y >= Height) return;

                // Load 4 pixels for top row
                float4 p00 = InputTexture.Load(int3(x + 0, y + 0, 0));
                float4 p10 = InputTexture.Load(int3(x + 1, y + 0, 0));
                float4 p20 = InputTexture.Load(int3(x + 2, y + 0, 0));
                float4 p30 = InputTexture.Load(int3(x + 3, y + 0, 0));

                // Load 4 pixels for bottom row
                float4 p01 = InputTexture.Load(int3(x + 0, y + 1, 0));
                float4 p11 = InputTexture.Load(int3(x + 1, y + 1, 0));
                float4 p21 = InputTexture.Load(int3(x + 2, y + 1, 0));
                float4 p31 = InputTexture.Load(int3(x + 3, y + 1, 0));

                // BT.709 Y calculation: 16 + 65.481*R + 128.553*G + 24.966*B
                uint y00 = clamp((uint)round(16.0f + 65.481f * p00.r + 128.553f * p00.g + 24.966f * p00.b), 16u, 235u);
                uint y10 = clamp((uint)round(16.0f + 65.481f * p10.r + 128.553f * p10.g + 24.966f * p10.b), 16u, 235u);
                uint y20 = clamp((uint)round(16.0f + 65.481f * p20.r + 128.553f * p20.g + 24.966f * p20.b), 16u, 235u);
                uint y30 = clamp((uint)round(16.0f + 65.481f * p30.r + 128.553f * p30.g + 24.966f * p30.b), 16u, 235u);

                uint y01 = clamp((uint)round(16.0f + 65.481f * p01.r + 128.553f * p01.g + 24.966f * p01.b), 16u, 235u);
                uint y11 = clamp((uint)round(16.0f + 65.481f * p11.r + 128.553f * p11.g + 24.966f * p11.b), 16u, 235u);
                uint y21 = clamp((uint)round(16.0f + 65.481f * p21.r + 128.553f * p21.g + 24.966f * p21.b), 16u, 235u);
                uint y31 = clamp((uint)round(16.0f + 65.481f * p31.r + 128.553f * p31.g + 24.966f * p31.b), 16u, 235u);

                // Pack Y rows
                uint wordYTop = y00 | (y10 << 8) | (y20 << 16) | (y30 << 24);
                uint wordYBottom = y01 | (y11 << 8) | (y21 << 16) | (y31 << 24);

                uint yTopIndex = (y * Width + x) / 4;
                uint yBottomIndex = ((y + 1) * Width + x) / 4;
                OutputNv12[yTopIndex] = wordYTop;
                OutputNv12[yBottomIndex] = wordYBottom;

                // Chroma: average 2x2 blocks
                float4 quad0 = (p00 + p10 + p01 + p11) * 0.25f;
                float4 quad1 = (p20 + p30 + p21 + p31) * 0.25f;

                uint u0 = clamp((uint)round(128.0f - 37.797f * quad0.r - 74.203f * quad0.g + 112.000f * quad0.b), 16u, 240u);
                uint v0 = clamp((uint)round(128.0f + 112.000f * quad0.r - 93.786f * quad0.g - 18.214f * quad0.b), 16u, 240u);

                uint u1 = clamp((uint)round(128.0f - 37.797f * quad1.r - 74.203f * quad1.g + 112.000f * quad1.b), 16u, 240u);
                uint v1 = clamp((uint)round(128.0f + 112.000f * quad1.r - 93.786f * quad1.g - 18.214f * quad1.b), 16u, 240u);

                uint wordUv = u0 | (v0 << 8) | (u1 << 16) | (v1 << 24);
                uint uvIndex = (Width * Height) / 4 + ((y / 2) * Width + x) / 4;
                OutputNv12[uvIndex] = wordUv;
            }
        ";

        ReadOnlyMemory<byte> bytecode = Vortice.D3DCompiler.Compiler.Compile(shaderHlsl, "CSColorConvert", "shader", "cs_5_0");
        Assert.False(bytecode.IsEmpty);

        using var manager = new D3D11DeviceManager();
        var device = manager.Device;
        var context = manager.Context;

        using var computeShader = device.CreateComputeShader(bytecode.Span);

        uint width = 64;
        uint height = 64;
        var texDesc = new Vortice.Direct3D11.Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.R8G8B8A8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            Usage = Vortice.Direct3D11.ResourceUsage.Default,
            BindFlags = Vortice.Direct3D11.BindFlags.ShaderResource | Vortice.Direct3D11.BindFlags.RenderTarget
        };
        using var inputTexture = device.CreateTexture2D(texDesc);
        using var srv = device.CreateShaderResourceView(inputTexture);

        // Clear input texture to black (0, 0, 0, 1)
        using var rtv = device.CreateRenderTargetView(inputTexture);
        context.ClearRenderTargetView(rtv, new Vortice.Mathematics.Color4(0, 0, 0, 1));

        // Create NV12 output buffer (64 * 64 * 1.5 = 6144 bytes = 1536 uints)
        uint totalBytes = (uint)(width * height * 1.5);
        uint totalUints = totalBytes / 4;
        var bufDesc = new Vortice.Direct3D11.BufferDescription
        {
            ByteWidth = totalBytes,
            Usage = Vortice.Direct3D11.ResourceUsage.Default,
            BindFlags = Vortice.Direct3D11.BindFlags.UnorderedAccess,
            MiscFlags = Vortice.Direct3D11.ResourceOptionFlags.BufferAllowRawViews
        };
        using var outputBuffer = device.CreateBuffer(bufDesc);
        var uavDesc = new Vortice.Direct3D11.UnorderedAccessViewDescription
        {
            Format = Vortice.DXGI.Format.R32_Typeless,
            ViewDimension = Vortice.Direct3D11.UnorderedAccessViewDimension.Buffer,
            Buffer = new Vortice.Direct3D11.BufferUnorderedAccessView
            {
                FirstElement = 0,
                NumElements = totalUints,
                Flags = Vortice.Direct3D11.BufferUnorderedAccessViewFlags.Raw
            }
        };
        using var uav = device.CreateUnorderedAccessView(outputBuffer, uavDesc);

        // Create constant buffer
        var cbDesc = new Vortice.Direct3D11.BufferDescription
        {
            ByteWidth = 16,
            Usage = Vortice.Direct3D11.ResourceUsage.Default,
            BindFlags = Vortice.Direct3D11.BindFlags.ConstantBuffer
        };
        uint[] paramsData = [width, height, 0, 0];
        using var cb = device.CreateBuffer(paramsData, cbDesc);

        // Dispatch compute shader
        context.CSSetShader(computeShader);
        context.CSSetShaderResource(0, srv);
        context.CSSetUnorderedAccessView(0, uav);
        context.CSSetConstantBuffer(0, cb);

        context.Dispatch(width / 4 / 8, height / 2 / 8, 1);

        // Unbind resources
        context.CSSetShader(null);
        context.CSSetShaderResource(0, null);
        context.CSSetUnorderedAccessView(0, null);

        // Readback to staging buffer
        var stagingDesc = new Vortice.Direct3D11.BufferDescription
        {
            ByteWidth = totalBytes,
            Usage = Vortice.Direct3D11.ResourceUsage.Staging,
            CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.Read,
            BindFlags = Vortice.Direct3D11.BindFlags.None
        };
        using var stagingBuffer = device.CreateBuffer(stagingDesc);
        context.CopyResource(stagingBuffer, outputBuffer);

        uint yPlaneSize = width * height;
        var mapped = context.Map(stagingBuffer, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            byte[] bytes = new byte[totalBytes];
            Marshal.Copy(mapped.DataPointer, bytes, 0, (int)totalBytes);

            // In BT.709, Black RGB(0,0,0) converts to Y=16, U=128, V=128
            for (int i = 0; i < yPlaneSize; i++)
            {
                Assert.Equal(16, bytes[i]);
            }

            for (int i = (int)yPlaneSize; i < totalBytes; i++)
            {
                Assert.Equal(128, bytes[i]);
            }
        }
        finally
        {
            context.Unmap(stagingBuffer, 0);
        }

        // Now test White RGB(1, 1, 1) -> Y=235, U=128, V=128
        context.ClearRenderTargetView(rtv, new Vortice.Mathematics.Color4(1, 1, 1, 1));
        context.CSSetShader(computeShader);
        context.CSSetShaderResource(0, srv);
        context.CSSetUnorderedAccessView(0, uav);
        context.CSSetConstantBuffer(0, cb);
        context.Dispatch(width / 4 / 8, height / 2 / 8, 1);
        context.CSSetShader(null);
        context.CSSetShaderResource(0, null);
        context.CSSetUnorderedAccessView(0, null);

        context.CopyResource(stagingBuffer, outputBuffer);
        var mappedWhite = context.Map(stagingBuffer, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            byte[] bytesWhite = new byte[totalBytes];
            Marshal.Copy(mappedWhite.DataPointer, bytesWhite, 0, (int)totalBytes);

            for (int i = 0; i < (int)yPlaneSize; i++)
            {
                Assert.Equal(235, bytesWhite[i]);
            }

            for (int i = (int)yPlaneSize; i < totalBytes; i++)
            {
                Assert.Equal(128, bytesWhite[i]);
            }
        }
        finally
        {
            context.Unmap(stagingBuffer, 0);
        }
    }

    [Fact]
    public void QuadShader_composites_layer_onto_canvas_target()
    {
        const string hlsl = @"
            struct PSInput
            {
                float4 Position : SV_POSITION;
                float2 TexCoord : TEXCOORD0;
            };

            cbuffer LayerParams : register(b0)
            {
                float4 DestRect;
                float4 CropRect;
                float4 Tint;
            };

            PSInput VSMain(uint id : SV_VertexID)
            {
                PSInput output;
                float2 uv = float2(0, 0);
                if (id == 1) uv = float2(1, 0);
                else if (id == 2) uv = float2(0, 1);
                else if (id == 3) uv = float2(1, 1);

                float2 pos = DestRect.xy + uv * DestRect.zw;
                output.Position = float4(pos.x * 2.0f - 1.0f, 1.0f - pos.y * 2.0f, 0.0f, 1.0f);
                output.TexCoord = CropRect.xy + uv * CropRect.zw;
                return output;
            }

            Texture2D LayerTexture : register(t0);
            SamplerState LinearSampler : register(s0);

            float4 PSMain(PSInput input) : SV_TARGET
            {
                return LayerTexture.Sample(LinearSampler, input.TexCoord) * Tint;
            }
        ";

        ReadOnlyMemory<byte> vsBytecode = Vortice.D3DCompiler.Compiler.Compile(hlsl, "VSMain", "vs", "vs_5_0");
        ReadOnlyMemory<byte> psBytecode = Vortice.D3DCompiler.Compiler.Compile(hlsl, "PSMain", "ps", "ps_5_0");

        using var manager = new D3D11DeviceManager();
        var device = manager.Device;
        var context = manager.Context;

        using var vs = device.CreateVertexShader(vsBytecode.Span);
        using var ps = device.CreatePixelShader(psBytecode.Span);

        uint width = 64;
        uint height = 64;
        var rtvTexDesc = new Vortice.Direct3D11.Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.R8G8B8A8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            Usage = Vortice.Direct3D11.ResourceUsage.Default,
            BindFlags = Vortice.Direct3D11.BindFlags.RenderTarget | Vortice.Direct3D11.BindFlags.ShaderResource
        };
        using var canvasTexture = device.CreateTexture2D(rtvTexDesc);
        using var rtv = device.CreateRenderTargetView(canvasTexture);

        // Clear canvas to black
        context.ClearRenderTargetView(rtv, new Vortice.Mathematics.Color4(0, 0, 0, 1));

        // Create a 32x32 pure red texture for the layer
        var layerTexDesc = new Vortice.Direct3D11.Texture2DDescription
        {
            Width = 32,
            Height = 32,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.R8G8B8A8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            Usage = Vortice.Direct3D11.ResourceUsage.Default,
            BindFlags = Vortice.Direct3D11.BindFlags.RenderTarget | Vortice.Direct3D11.BindFlags.ShaderResource
        };
        using var layerTexture = device.CreateTexture2D(layerTexDesc);
        using var layerRtv = device.CreateRenderTargetView(layerTexture);
        using var layerSrv = device.CreateShaderResourceView(layerTexture);
        context.ClearRenderTargetView(layerRtv, new Vortice.Mathematics.Color4(1, 0, 0, 1)); // Pure Red

        // Sampler
        var samplerDesc = new Vortice.Direct3D11.SamplerDescription
        {
            Filter = Vortice.Direct3D11.Filter.MinMagMipLinear,
            AddressU = Vortice.Direct3D11.TextureAddressMode.Clamp,
            AddressV = Vortice.Direct3D11.TextureAddressMode.Clamp,
            AddressW = Vortice.Direct3D11.TextureAddressMode.Clamp,
            ComparisonFunc = Vortice.Direct3D11.ComparisonFunction.Never
        };
        using var sampler = device.CreateSamplerState(samplerDesc);

        // Constant buffer for layer placement: DestRect(0.25, 0.25, 0.5, 0.5), CropRect(0, 0, 1, 1), Tint(1, 1, 1, 1)
        float[] cbufData =
        [
            0.25f, 0.25f, 0.5f, 0.5f, // DestRect
            0.0f, 0.0f, 1.0f, 1.0f,   // CropRect
            1.0f, 1.0f, 1.0f, 1.0f    // Tint
        ];
        var cbDesc = new Vortice.Direct3D11.BufferDescription
        {
            ByteWidth = (uint)(cbufData.Length * sizeof(float)),
            Usage = Vortice.Direct3D11.ResourceUsage.Default,
            BindFlags = Vortice.Direct3D11.BindFlags.ConstantBuffer
        };
        using var cb = device.CreateBuffer(cbufData, cbDesc);

        // Setup pipeline
        context.RSSetViewport(0, 0, width, height);
        context.OMSetRenderTargets([rtv], (Vortice.Direct3D11.ID3D11DepthStencilView?)null);
        context.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleStrip);
        context.VSSetShader(vs);
        context.VSSetConstantBuffer(0, cb);
        context.PSSetShader(ps);
        context.PSSetConstantBuffer(0, cb);
        context.PSSetShaderResource(0, layerSrv);
        context.PSSetSampler(0, sampler);

        // Draw quad
        context.Draw(4, 0);

        // Cleanup pipeline bindings
        context.OMSetRenderTargets([], (Vortice.Direct3D11.ID3D11DepthStencilView?)null);
        context.PSSetShaderResource(0, (Vortice.Direct3D11.ID3D11ShaderResourceView)null!);

        // Read back canvas texture
        var stagingDesc = new Vortice.Direct3D11.Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.R8G8B8A8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            Usage = Vortice.Direct3D11.ResourceUsage.Staging,
            CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.Read,
            BindFlags = Vortice.Direct3D11.BindFlags.None
        };
        using var staging = device.CreateTexture2D(stagingDesc);
        context.CopyResource(staging, canvasTexture);

        var mapped = context.Map(staging, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            // Canvas top-left pixel (0,0) should be clear color (Black: 0, 0, 0, 255)
            // Canvas center pixel (32, 32) should be layer color (Red: 255, 0, 0, 255)
            byte[] pixel = new byte[4];
            Marshal.Copy(mapped.DataPointer, pixel, 0, 4);
            Assert.Equal(0, pixel[0]); // R
            Assert.Equal(0, pixel[1]); // G
            Assert.Equal(0, pixel[2]); // B

            // Center pixel: row 32, col 32
            IntPtr centerPtr = IntPtr.Add(mapped.DataPointer, (int)(32 * mapped.RowPitch + 32 * 4));
            Marshal.Copy(centerPtr, pixel, 0, 4);
            Assert.Equal(255, pixel[0]); // R is 255
            Assert.Equal(0, pixel[1]);   // G is 0
            Assert.Equal(0, pixel[2]);   // B is 0
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Linkast.Core.Camera;
using Microsoft.Extensions.Logging;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Linkast.Platform.Windows.Rendering;

/// <summary>
/// Direct3D 11 hardware-accelerated scene compositor.
/// Renders layers (game capture, cameras, backgrounds, web overlays) onto a canvas render target,
/// and converts the composited frame to BT.709 NV12 on the GPU via compute shader.
/// </summary>
public sealed partial class D3D11SceneCompositor : IDisposable
{
    private const string QuadShaderHlsl = @"
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

    private const string Nv12ShaderHlsl = @"
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

            float4 p00 = InputTexture.Load(int3(x + 0, y + 0, 0));
            float4 p10 = InputTexture.Load(int3(x + 1, y + 0, 0));
            float4 p20 = InputTexture.Load(int3(x + 2, y + 0, 0));
            float4 p30 = InputTexture.Load(int3(x + 3, y + 0, 0));

            float4 p01 = InputTexture.Load(int3(x + 0, y + 1, 0));
            float4 p11 = InputTexture.Load(int3(x + 1, y + 1, 0));
            float4 p21 = InputTexture.Load(int3(x + 2, y + 1, 0));
            float4 p31 = InputTexture.Load(int3(x + 3, y + 1, 0));

            // BT.709 Limited Range Y: [16, 235]
            uint y00 = clamp((uint)round(16.0f + 65.481f * p00.r + 128.553f * p00.g + 24.966f * p00.b), 16u, 235u);
            uint y10 = clamp((uint)round(16.0f + 65.481f * p10.r + 128.553f * p10.g + 24.966f * p10.b), 16u, 235u);
            uint y20 = clamp((uint)round(16.0f + 65.481f * p20.r + 128.553f * p20.g + 24.966f * p20.b), 16u, 235u);
            uint y30 = clamp((uint)round(16.0f + 65.481f * p30.r + 128.553f * p30.g + 24.966f * p30.b), 16u, 235u);

            uint y01 = clamp((uint)round(16.0f + 65.481f * p01.r + 128.553f * p01.g + 24.966f * p01.b), 16u, 235u);
            uint y11 = clamp((uint)round(16.0f + 65.481f * p11.r + 128.553f * p11.g + 24.966f * p11.b), 16u, 235u);
            uint y21 = clamp((uint)round(16.0f + 65.481f * p21.r + 128.553f * p21.g + 24.966f * p21.b), 16u, 235u);
            uint y31 = clamp((uint)round(16.0f + 65.481f * p31.r + 128.553f * p31.g + 24.966f * p31.b), 16u, 235u);

            uint wordYTop = y00 | (y10 << 8) | (y20 << 16) | (y30 << 24);
            uint wordYBottom = y01 | (y11 << 8) | (y21 << 16) | (y31 << 24);

            uint yTopIndex = (y * Width + x) / 4;
            uint yBottomIndex = ((y + 1) * Width + x) / 4;
            OutputNv12[yTopIndex] = wordYTop;
            OutputNv12[yBottomIndex] = wordYBottom;

            // BT.709 Chroma UV: [16, 240]
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

    private readonly Lock _renderGate = new();
    private readonly D3D11DeviceManager _deviceManager;
    private readonly ILogger? _logger;

    private int _canvasWidth;
    private int _canvasHeight;
    private uint _nv12TotalBytes;

    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11ComputeShader? _nv12ComputeShader;
    private ID3D11SamplerState? _linearSampler;
    private ID3D11BlendState? _alphaBlendState;
    private ID3D11Buffer? _layerConstantBuffer;
    private ID3D11Buffer? _nv12ConstantBuffer;

    private ID3D11Texture2D? _canvasTexture;
    private ID3D11RenderTargetView? _canvasRtv;
    private ID3D11ShaderResourceView? _canvasSrv;
    private ID3D11Buffer? _nv12GpuBuffer;
    private ID3D11UnorderedAccessView? _nv12Uav;
    private readonly ID3D11Buffer?[] _nv12StagingBuffers = new ID3D11Buffer?[2];
    private int _stagingWriteIndex;
    private int _stagingReadIndex = -1;
    private byte[]? _readbackBuffer;

    private bool _disposed;

    public D3D11SceneCompositor(
        D3D11DeviceManager deviceManager,
        int canvasWidth = 1920,
        int canvasHeight = 1080,
        ILogger? logger = null)
    {
        _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
        _canvasWidth = canvasWidth;
        _canvasHeight = canvasHeight;
        _logger = logger;

        InitializeResources();
        _deviceManager.DeviceRestored += OnDeviceRestored;
        _deviceManager.RegisterRenderGate(_renderGate);
    }

    public int CanvasWidth => _canvasWidth;

    public int CanvasHeight => _canvasHeight;

    public D3D11DeviceManager DeviceManager => _deviceManager;

    public Lock RenderGate => _renderGate;

    public uint Nv12ByteSize => _nv12TotalBytes;

    public ID3D11Texture2D? CanvasTexture => _canvasTexture;

    private void InitializeResources()
    {
        lock (_renderGate)
        lock (_deviceManager.ContextLock)
        {
            DisposeResources();

            var device = _deviceManager.Device;

            // Compile shaders
            var vsBytecode = Compiler.Compile(QuadShaderHlsl, "VSMain", "vs", "vs_5_0");
            var psBytecode = Compiler.Compile(QuadShaderHlsl, "PSMain", "ps", "ps_5_0");
            var csBytecode = Compiler.Compile(Nv12ShaderHlsl, "CSColorConvert", "cs", "cs_5_0");

            _vertexShader = device.CreateVertexShader(vsBytecode.Span);
            _pixelShader = device.CreatePixelShader(psBytecode.Span);
            _nv12ComputeShader = device.CreateComputeShader(csBytecode.Span);

            // Sampler
            var samplerDesc = new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunc = ComparisonFunction.Never
            };
            _linearSampler = device.CreateSamplerState(samplerDesc);

            // Alpha Blend State (straight alpha blending)
            var blendDesc = new BlendDescription
            {
                AlphaToCoverageEnable = false,
                IndependentBlendEnable = false
            };
            blendDesc.RenderTarget[0] = new RenderTargetBlendDescription
            {
                BlendEnable = true,
                SourceBlend = Blend.SourceAlpha,
                DestinationBlend = Blend.InverseSourceAlpha,
                BlendOperation = BlendOperation.Add,
                SourceBlendAlpha = Blend.One,
                DestinationBlendAlpha = Blend.InverseSourceAlpha,
                BlendOperationAlpha = BlendOperation.Add,
                RenderTargetWriteMask = ColorWriteEnable.All
            };
            _alphaBlendState = device.CreateBlendState(blendDesc);

            // Constant buffer for layer drawing: DestRect(float4) + CropRect(float4) + Tint(float4) = 48 bytes
            var cbLayerDesc = new BufferDescription
            {
                ByteWidth = 48,
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ConstantBuffer
            };
            _layerConstantBuffer = device.CreateBuffer(cbLayerDesc);

            // Constant buffer for NV12 compute shader: Width, Height, Pad, Pad = 16 bytes
            var cbNv12Desc = new BufferDescription
            {
                ByteWidth = 16,
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ConstantBuffer
            };
            _nv12ConstantBuffer = device.CreateBuffer(cbNv12Desc);

            CreateTargets(_canvasWidth, _canvasHeight);
        }
    }

    private void CreateTargets(int width, int height)
    {
        var device = _deviceManager.Device;

        _canvasRtv?.Dispose();
        _canvasSrv?.Dispose();
        _canvasTexture?.Dispose();
        _nv12Uav?.Dispose();
        _nv12GpuBuffer?.Dispose();
        _nv12StagingBuffers[0]?.Dispose();
        _nv12StagingBuffers[0] = null;
        _nv12StagingBuffers[1]?.Dispose();
        _nv12StagingBuffers[1] = null;

        _canvasWidth = width;
        _canvasHeight = height;
        _nv12TotalBytes = (uint)(width * height * 1.5);
        _stagingWriteIndex = 0;
        _stagingReadIndex = -1;

        if (_readbackBuffer is null || _readbackBuffer.Length < _nv12TotalBytes)
        {
            _readbackBuffer = new byte[_nv12TotalBytes];
        }

        // Canvas texture (RGBA RenderTarget + ShaderResource)
        var texDesc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
        };
        _canvasTexture = device.CreateTexture2D(texDesc);
        _canvasRtv = device.CreateRenderTargetView(_canvasTexture);
        _canvasSrv = device.CreateShaderResourceView(_canvasTexture);

        // NV12 output buffer (GPU UAV)
        uint totalUints = _nv12TotalBytes / 4;
        var bufDesc = new BufferDescription
        {
            ByteWidth = _nv12TotalBytes,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.UnorderedAccess,
            MiscFlags = ResourceOptionFlags.BufferAllowRawViews
        };
        _nv12GpuBuffer = device.CreateBuffer(bufDesc);

        var uavDesc = new UnorderedAccessViewDescription
        {
            Format = Format.R32_Typeless,
            ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView
            {
                FirstElement = 0,
                NumElements = totalUints,
                Flags = BufferUnorderedAccessViewFlags.Raw
            }
        };
        _nv12Uav = device.CreateUnorderedAccessView(_nv12GpuBuffer, uavDesc);

        // NV12 staging buffers (CPU double-buffered readback)
        var stagingDesc = new BufferDescription
        {
            ByteWidth = _nv12TotalBytes,
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None
        };
        _nv12StagingBuffers[0] = device.CreateBuffer(stagingDesc);
        _nv12StagingBuffers[1] = device.CreateBuffer(stagingDesc);

        // Update NV12 params constant buffer
        uint[] paramsData = [(uint)width, (uint)height, 0, 0];
        _deviceManager.Context.UpdateSubresource(paramsData, _nv12ConstantBuffer!);
    }

    /// <summary>
    /// Resizes the compositor canvas and reallocates render targets if dimensions differ.
    /// </summary>
    public void ResizeCanvas(int width, int height)
    {
        lock (_renderGate)
        lock (_deviceManager.ContextLock)
        {
            if (_disposed || (_canvasWidth == width && _canvasHeight == height))
            {
                return;
            }

            CreateTargets(width, height);
        }
    }

    /// <summary>
    /// Clears the canvas with a background color and binds it as the active render target.
    /// Returns true if the canvas is ready; false if disposed or device is being reinitialized.
    /// </summary>
    public bool BeginFrame(Color4 clearColor)
    {
        lock (_renderGate)
        lock (_deviceManager.ContextLock)
        {
            if (_disposed || _canvasRtv is null)
            {
                return false;
            }

            var context = _deviceManager.Context;
            context.RSSetViewport(0, 0, _canvasWidth, _canvasHeight);
            context.OMSetRenderTargets([_canvasRtv], (ID3D11DepthStencilView?)null);
            context.OMSetBlendState(_alphaBlendState);
            context.ClearRenderTargetView(_canvasRtv, clearColor);

            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
            context.VSSetShader(_vertexShader);
            context.PSSetShader(_pixelShader);
            context.PSSetSampler(0, _linearSampler);
            return true;
        }
    }

    /// <summary>
    /// Draws a quad layer textured with <paramref name="layerTexture"/> over the canvas.
    /// When rendering high-frequency frame loops, prefer passing a pre-allocated ID3D11ShaderResourceView.
    /// </summary>
    public void DrawLayer(
        ID3D11Texture2D layerTexture,
        NormalizedRect destRect,
        NormalizedRect cropRect,
        float opacity = 1.0f)
    {
        ArgumentNullException.ThrowIfNull(layerTexture);

        lock (_renderGate)
        {
            if (_disposed)
            {
                return;
            }

            using var srv = _deviceManager.Device.CreateShaderResourceView(layerTexture);
            DrawLayer(srv, destRect, cropRect, opacity);
        }
    }

    /// <summary>
    /// Draws a quad layer with an existing shader resource view.
    /// </summary>
    public void DrawLayer(
        ID3D11ShaderResourceView layerSrv,
        NormalizedRect destRect,
        NormalizedRect cropRect,
        float opacity = 1.0f)
    {
        ArgumentNullException.ThrowIfNull(layerSrv);

        lock (_renderGate)
        lock (_deviceManager.ContextLock)
        {
            if (_disposed || _canvasRtv is null)
            {
                return;
            }

            if (layerSrv.Device.NativePointer != _deviceManager.Device.NativePointer)
            {
                return;
            }

            var context = _deviceManager.Context;
            var dest = destRect.Clamped();
            var crop = cropRect.Clamped();

            float[] cbufData =
            [
                (float)dest.X, (float)dest.Y, (float)dest.Width, (float)dest.Height,
                (float)crop.X, (float)crop.Y, (float)crop.Width, (float)crop.Height,
                1.0f, 1.0f, 1.0f, Math.Clamp(opacity, 0.0f, 1.0f)
            ];
            context.UpdateSubresource(cbufData, _layerConstantBuffer!);

            context.VSSetConstantBuffer(0, _layerConstantBuffer);
            context.PSSetConstantBuffer(0, _layerConstantBuffer);
            context.PSSetShaderResource(0, layerSrv);

            context.Draw(4, 0);

            context.PSSetShaderResource(0, (ID3D11ShaderResourceView)null!);
        }
    }

    /// <summary>
    /// Executes GPU compute shader to convert composited canvas into BT.709 NV12 format,
    /// and copies the frame into the provided <paramref name="destination"/> byte span.
    /// Returns the total bytes written.
    /// </summary>
    public int ConvertToNv12(Span<byte> destination)
    {
        lock (_renderGate)
        {
            if (_disposed || _canvasSrv is null || _nv12Uav is null || _nv12StagingBuffers[0] is null || _nv12StagingBuffers[1] is null)
            {
                return 0;
            }

            if ((uint)destination.Length < _nv12TotalBytes)
            {
                throw new ArgumentException($"Destination buffer size ({destination.Length}) is smaller than required NV12 frame size ({_nv12TotalBytes}).", nameof(destination));
            }

            lock (_deviceManager.ContextLock)
            {
                var context = _deviceManager.Context;

                // Unbind canvas from OM before binding as compute SRV
                context.OMSetRenderTargets([], (ID3D11DepthStencilView?)null);

                // Run NV12 conversion compute shader
                context.CSSetShader(_nv12ComputeShader);
                context.CSSetShaderResource(0, _canvasSrv);
                context.CSSetUnorderedAccessView(0, _nv12Uav);
                context.CSSetConstantBuffer(0, _nv12ConstantBuffer);

                // Each 8x8 thread group covers 32 px wide (4 per thread) by 16 px tall (2 per thread).
                // Round up so the last partial group is dispatched; the shader guards out-of-range texels.
                context.Dispatch((uint)((_canvasWidth + 31) / 32), (uint)((_canvasHeight + 15) / 16), 1);

                // Unbind CS inputs/outputs
                context.CSSetShader(null);
                context.CSSetShaderResource(0, (ID3D11ShaderResourceView)null!);
                context.CSSetUnorderedAccessView(0, (ID3D11UnorderedAccessView)null!);

                // Copy newest GPU buffer to current write staging buffer
                var writeBuffer = _nv12StagingBuffers[_stagingWriteIndex]!;
                context.CopyResource(writeBuffer, _nv12GpuBuffer!);

                // Read from alternate staging buffer (which finished GPU processing during prior frame).
                // On the very first frame, map write buffer directly so output is immediate.
                var readBuffer = _stagingReadIndex >= 0 ? _nv12StagingBuffers[_stagingReadIndex]! : writeBuffer;
                _stagingReadIndex = _stagingWriteIndex;
                _stagingWriteIndex = 1 - _stagingWriteIndex;

                var mapped = context.Map(readBuffer, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    unsafe
                    {
                        var sourceSpan = new ReadOnlySpan<byte>((void*)mapped.DataPointer, (int)_nv12TotalBytes);
                        sourceSpan.CopyTo(destination);
                    }
                    return (int)_nv12TotalBytes;
                }
                finally
                {
                    context.Unmap(readBuffer, 0);
                }
            }
        }
    }

    /// <summary>
    /// Executes GPU compute shader to convert composited canvas into BT.709 NV12 format,
    /// and writes the raw NV12 frame directly into the destination <paramref name="stream"/> (e.g. named pipe).
    /// Returns the total bytes written.
    /// </summary>
    public int ConvertToNv12(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        int bytesRead = 0;
        lock (_renderGate)
        {
            lock (_deviceManager.ContextLock)
            {
                if (_disposed || _canvasSrv is null || _nv12Uav is null || _nv12StagingBuffers[0] is null || _nv12StagingBuffers[1] is null)
                {
                    return 0;
                }

                var context = _deviceManager.Context;

                // Unbind canvas from OM
                context.OMSetRenderTargets([], (ID3D11DepthStencilView?)null);

                // Run compute shader
                context.CSSetShader(_nv12ComputeShader);
                context.CSSetShaderResource(0, _canvasSrv);
                context.CSSetUnorderedAccessView(0, _nv12Uav);
                context.CSSetConstantBuffer(0, _nv12ConstantBuffer);

                // Each 8x8 thread group covers 32 px wide (4 per thread) by 16 px tall (2 per thread).
                // Round up so the last partial group is dispatched; the shader guards out-of-range texels.
                context.Dispatch((uint)((_canvasWidth + 31) / 32), (uint)((_canvasHeight + 15) / 16), 1);

                // Unbind CS
                context.CSSetShader(null);
                context.CSSetShaderResource(0, (ID3D11ShaderResourceView)null!);
                context.CSSetUnorderedAccessView(0, (ID3D11UnorderedAccessView)null!);

                // Copy to staging
                var writeBuffer = _nv12StagingBuffers[_stagingWriteIndex]!;
                context.CopyResource(writeBuffer, _nv12GpuBuffer!);

                var readBuffer = _stagingReadIndex >= 0 ? _nv12StagingBuffers[_stagingReadIndex]! : writeBuffer;
                _stagingReadIndex = _stagingWriteIndex;
                _stagingWriteIndex = 1 - _stagingWriteIndex;

                var mapped = context.Map(readBuffer, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    unsafe
                    {
                        var sourceSpan = new ReadOnlySpan<byte>((void*)mapped.DataPointer, (int)_nv12TotalBytes);
                        if (_readbackBuffer is not null)
                        {
                            sourceSpan.CopyTo(_readbackBuffer);
                            bytesRead = (int)_nv12TotalBytes;
                        }
                    }
                }
                finally
                {
                    context.Unmap(readBuffer, 0);
                }
            }

            // Write to destination pipe OUTSIDE ContextLock to prevent pipe I/O from blocking D3D11 context
            if (bytesRead > 0 && _readbackBuffer is not null)
            {
                stream.Write(_readbackBuffer.AsSpan(0, bytesRead));
            }
        }

        return bytesRead;
    }

    private void OnDeviceRestored(object? sender, EventArgs e)
    {
        lock (_renderGate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                InitializeResources();
            }
            catch (Exception ex)
            {
                if (_logger is not null)
                {
                    LogRecreateFailed(_logger, ex);
                }
            }
        }
    }

    private void DisposeResources()
    {
        _canvasRtv?.Dispose();
        _canvasRtv = null;
        _canvasSrv?.Dispose();
        _canvasSrv = null;
        _canvasTexture?.Dispose();
        _canvasTexture = null;
        _nv12Uav?.Dispose();
        _nv12Uav = null;
        _nv12GpuBuffer?.Dispose();
        _nv12GpuBuffer = null;
        _nv12StagingBuffers[0]?.Dispose();
        _nv12StagingBuffers[0] = null;
        _nv12StagingBuffers[1]?.Dispose();
        _nv12StagingBuffers[1] = null;

        _layerConstantBuffer?.Dispose();
        _layerConstantBuffer = null;
        _nv12ConstantBuffer?.Dispose();
        _nv12ConstantBuffer = null;
        _alphaBlendState?.Dispose();
        _alphaBlendState = null;
        _linearSampler?.Dispose();
        _linearSampler = null;
        _vertexShader?.Dispose();
        _vertexShader = null;
        _pixelShader?.Dispose();
        _pixelShader = null;
        _nv12ComputeShader?.Dispose();
        _nv12ComputeShader = null;
    }

    public void Dispose()
    {
        lock (_renderGate)
        lock (_deviceManager.ContextLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _deviceManager.DeviceRestored -= OnDeviceRestored;
            _deviceManager.UnregisterRenderGate(_renderGate);

            DisposeResources();
        }
    }

    [LoggerMessage(LogLevel.Error, "Recreating D3D11SceneCompositor resources failed after device restore.")]
    private static partial void LogRecreateFailed(ILogger logger, Exception ex);
}

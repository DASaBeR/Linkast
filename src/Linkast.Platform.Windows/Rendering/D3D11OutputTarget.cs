// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Linkast.Core.Camera;
using Microsoft.Extensions.Logging;
using Vortice.Mathematics;

namespace Linkast.Platform.Windows.Rendering;

/// <summary>
/// One encoded output of a canvas (the stream, a recording) at its own size. When the size equals the
/// canvas, the canvas is converted directly; otherwise it is scaled on the GPU first.
/// </summary>
public sealed class D3D11OutputTarget : IDisposable
{
    private static readonly Color4 Black = new(0f, 0f, 0f, 1f);

    private readonly D3D11DeviceManager _devices;
    private readonly Stream _videoPipe;
    private readonly ILogger? _logger;
    private D3D11SceneCompositor? _scaler;
    private byte[]? _blackFrame;
    private int _attached = 1;
    private long _framesWritten;
    private bool _disposed;

    public D3D11OutputTarget(
        D3D11DeviceManager devices,
        string name,
        int width,
        int height,
        Stream videoPipe,
        bool isPrimary,
        ILogger? logger = null)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Output target dimensions must be positive.");
        }

        Name = name;
        Width = width;
        Height = height;
        _videoPipe = videoPipe ?? throw new ArgumentNullException(nameof(videoPipe));
        IsPrimary = isPrimary;
        _logger = logger;
        Nv12ByteSize = (uint)(width * height * 1.5);
    }

    public string Name { get; }

    public int Width { get; }

    public int Height { get; }

    public bool IsPrimary { get; }

    public bool IsAttached => Volatile.Read(ref _attached) == 1 && !_disposed;

    public uint Nv12ByteSize { get; }

    public long FramesWritten => Interlocked.Read(ref _framesWritten);

    /// <summary>Detaches this output so future ticks skip it. Returns true only on the first detach.</summary>
    public bool TryDetach() => Interlocked.Exchange(ref _attached, 0) == 1;

    /// <summary>Writes one NV12 frame of <paramref name="canvas"/> to the pipe. Throws <see cref="IOException"/> when the pipe breaks.</summary>
    public bool WriteFrame(D3D11SceneCompositor canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (!IsAttached)
        {
            return false;
        }

        // Lock order: always take the source canvas's RenderGate before the scaler's RenderGate.
        lock (canvas.RenderGate)
        {
            if (!IsAttached)
            {
                return false;
            }

            if (Width == canvas.CanvasWidth && Height == canvas.CanvasHeight)
            {
                if (canvas.ConvertToNv12(_videoPipe) <= 0)
                {
                    return false;
                }

                Interlocked.Increment(ref _framesWritten);
                return true;
            }

            var canvasSrv = canvas.CanvasSrv;
            if (canvasSrv is null)
            {
                return false;
            }

            _scaler ??= new D3D11SceneCompositor(_devices, Width, Height, _logger);
            if (!_scaler.BeginFrame(Black))
            {
                return false;
            }

            _scaler.DrawLayer(canvasSrv, NormalizedRect.Full, NormalizedRect.Full);
            if (_scaler.ConvertToNv12(_videoPipe) <= 0)
            {
                return false;
            }

            Interlocked.Increment(ref _framesWritten);
            return true;
        }
    }

    /// <summary>Writes a black NV12 fallback frame directly to the pipe. Throws <see cref="IOException"/> when the pipe breaks.</summary>
    public void WriteBlackFrame()
    {
        if (!IsAttached)
        {
            return;
        }

        int requiredSize = (int)Nv12ByteSize;
        if (_blackFrame is null || _blackFrame.Length != requiredSize)
        {
            _blackFrame = new byte[requiredSize];
            int ySize = Width * Height;
            _blackFrame.AsSpan(0, ySize).Fill(16); // BT.709 black Y
            _blackFrame.AsSpan(ySize).Fill(128);   // BT.709 neutral UV
        }

        _videoPipe.Write(_blackFrame);
        Interlocked.Increment(ref _framesWritten);
    }

    public void Dispose()
    {
        _disposed = true;
        TryDetach();
        _scaler?.Dispose();
        _scaler = null;
    }
}

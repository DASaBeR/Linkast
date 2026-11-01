// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Linkast.Platform.Windows.Rendering;

/// <summary>
/// Manages the primary Direct3D 11 device, context ownership, cross-thread protection,
/// and device loss detection/recovery for Linkast's GPU composition pipeline.
/// </summary>
public sealed partial class D3D11DeviceManager : IDisposable
{
    private static readonly FeatureLevel[] FeatureLevels =
    [
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0
    ];

    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Multithread? _multithread;
    private bool _isDeviceLost;
    private bool _disposed;

    public D3D11DeviceManager(ILogger<D3D11DeviceManager>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        InitializeDevice();
    }

    /// <summary>Mutual exclusion lock for coordinating multi-step immediate context operations across threads.</summary>
    public Lock ContextLock { get; } = new();

    /// <summary>The primary Direct3D 11 device for resource allocation.</summary>
    public ID3D11Device Device
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _device ?? throw new InvalidOperationException("D3D11 device is not initialized.");
            }
        }
    }

    /// <summary>
    /// The immediate device context. According to ADR 0010, this context should be owned
    /// and issued commands exclusively by the dedicated render thread.
    /// </summary>
    public ID3D11DeviceContext Context
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _context ?? throw new InvalidOperationException("D3D11 context is not initialized.");
            }
        }
    }

    /// <summary>True if the GPU device has been removed or reset and requires recovery.</summary>
    public bool IsDeviceLost
    {
        get
        {
            lock (_gate)
            {
                return _isDeviceLost;
            }
        }
    }

    /// <summary>Raised when a GPU device loss (DXGI_ERROR_DEVICE_REMOVED/RESET) is detected.</summary>
    public event EventHandler? DeviceLost;

    /// <summary>Raised after the GPU device and context have been successfully recreated.</summary>
    public event EventHandler? DeviceRestored;

    /// <summary>
    /// Creates a WinRT IDirect3DDevice wrapper around the underlying DXGI device,
    /// enabling interoperability with Windows.Graphics.Capture and Direct3D11CaptureFramePool.
    /// </summary>
    public IDirect3DDevice CreateWinRTDevice()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var dxgi = Device.QueryInterface<IDXGIDevice>();
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var inspectable));
            try
            {
                return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            }
            finally
            {
                Marshal.Release(inspectable);
            }
        }
    }

    /// <summary>
    /// Queries the device removal status. Returns true if the device is healthy, false if lost.
    /// If lost, raises <see cref="DeviceLost"/>.
    /// </summary>
    public bool CheckDeviceStatus()
    {
        lock (_gate)
        {
            if (_disposed || _device is null)
            {
                return false;
            }

            if (_isDeviceLost)
            {
                return false;
            }

            var reason = _device.DeviceRemovedReason;
            if (reason.Failure)
            {
                _isDeviceLost = true;
                LogDeviceLost(_logger, reason.Description, reason.Code);
                DeviceLost?.Invoke(this, EventArgs.Empty);
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Recreates the Direct3D 11 device and immediate context following a device loss event.
    /// </summary>
    public bool RecreateDevice()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            LogRecreatingDevice(_logger);
            DisposeDeviceResources();

            try
            {
                InitializeDevice();
                _isDeviceLost = false;
                LogDeviceRestored(_logger);
                DeviceRestored?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (Exception ex)
            {
                LogRecreateFailed(_logger, ex);
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeDeviceResources();
        }
    }

    private void InitializeDevice()
    {
        var creationFlags = DeviceCreationFlags.BgraSupport;
#if DEBUG
        // Only enable Debug layer if available on the host machine to prevent startup crashes.
        if (D3D11.D3D11CreateDevice(null, DriverType.Null, DeviceCreationFlags.Debug, FeatureLevels, out ID3D11Device? _).Success)
        {
            creationFlags |= DeviceCreationFlags.Debug;
        }
#endif

        var result = D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            creationFlags,
            FeatureLevels,
            out ID3D11Device? device);

        if (result.Failure)
        {
            LogHardwareFallback(_logger, result.ToString());
            result = D3D11.D3D11CreateDevice(
                null,
                DriverType.Warp,
                creationFlags,
                FeatureLevels,
                out device);
            result.CheckError();
        }

        _device = device ?? throw new InvalidOperationException("Failed to obtain Direct3D 11 device.");
        _context = _device.ImmediateContext;

        // Enable multithread protection on the immediate context for safe cross-thread texture copy and draw.
        // ID3D11Multithread is implemented by the immediate context, not the device.
        _multithread = _context.QueryInterfaceOrNull<ID3D11Multithread>();
        _multithread?.SetMultithreadProtected(true);
    }

    private void DisposeDeviceResources()
    {
        _multithread?.Dispose();
        _multithread = null;

        _context?.Dispose();
        _context = null;

        _device?.Dispose();
        _device = null;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Direct3D 11 device lost! Removal reason: {Reason} (0x{Code:X8})")]
    private static partial void LogDeviceLost(ILogger logger, string reason, int code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hardware D3D11 device creation failed ({Result}). Falling back to WARP driver.")]
    private static partial void LogHardwareFallback(ILogger logger, string result);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recreating Direct3D 11 device after device loss...")]
    private static partial void LogRecreatingDevice(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Direct3D 11 device successfully restored.")]
    private static partial void LogDeviceRestored(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to recreate Direct3D 11 device.")]
    private static partial void LogRecreateFailed(ILogger logger, Exception ex);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
}

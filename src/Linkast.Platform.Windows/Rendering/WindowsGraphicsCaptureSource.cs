// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Linkast.Core.Rendering;
using Linkast.Platform.Windows.Capture;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Linkast.Platform.Windows.Rendering;

/// <summary>
/// GPU-accelerated capture source capturing a window or display monitor via Windows Graphics Capture (WGC).
/// Frames are ingested directly onto Direct3D 11 textures with zero CPU readback.
/// </summary>
public sealed partial class WindowsGraphicsCaptureSource : ID3D11SceneSource
{
    private readonly Lock _syncLock = new();
    private readonly D3D11DeviceManager _deviceManager;
    private readonly IntPtr _targetHandle;
    private readonly bool _isWindow;
    private readonly bool _captureCursor;
    private readonly ILogger? _logger;

    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _retainedTexture;

    private int _width;
    private int _height;
    private long _frameSequence;
    private long _lastFrameTimestampQpc;
    private bool _hasReceivedFrame;
    private bool _disposed;
    private SceneSourceState _state = SceneSourceState.Available;

    public WindowsGraphicsCaptureSource(
        string id,
        D3D11DeviceManager deviceManager,
        IntPtr targetHandle,
        bool isWindow,
        bool captureCursor = true,
        ILogger? logger = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
        _targetHandle = targetHandle;
        _isWindow = isWindow;
        _captureCursor = captureCursor;
        _logger = logger;

        StartCapture();
    }

    public string Id { get; }

    public SceneSourceState State
    {
        get
        {
            lock (_syncLock)
            {
                return _state;
            }
        }
        private set
        {
            bool changed = false;
            lock (_syncLock)
            {
                if (_state != value)
                {
                    _state = value;
                    changed = true;
                }
            }

            if (changed)
            {
                StateChanged?.Invoke(this, value);
            }
        }
    }

    public int Width
    {
        get
        {
            lock (_syncLock)
            {
                return _width;
            }
        }
    }

    public int Height
    {
        get
        {
            lock (_syncLock)
            {
                return _height;
            }
        }
    }

    public long FrameSequence
    {
        get
        {
            lock (_syncLock)
            {
                return _frameSequence;
            }
        }
    }

    public long LastFrameTimestampQpc
    {
        get
        {
            lock (_syncLock)
            {
                return _lastFrameTimestampQpc;
            }
        }
    }

    public event EventHandler<SceneSourceState>? StateChanged;

    private void StartCapture()
    {
        lock (_syncLock)
        {
            try
            {
                if (_isWindow && !VideoSourceCatalog.WindowExists((long)_targetHandle))
                {
                    State = SceneSourceState.Lost;
                    return;
                }

                _item = _isWindow
                    ? WindowsGraphicsInterop.CreateItemForWindow(_targetHandle)
                    : WindowsGraphicsInterop.CreateItemForMonitor(_targetHandle);

                _width = _item.Size.Width;
                _height = _item.Size.Height;

                _winrtDevice = _deviceManager.CreateWinRTDevice();
                _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    _winrtDevice,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    2,
                    _item.Size);

                _session = _framePool.CreateCaptureSession(_item);
                _session.IsCursorCaptureEnabled = _captureCursor;

                _item.Closed += OnItemClosed;
                _framePool.FrameArrived += OnFrameArrived;

                _session.StartCapture();
                _state = SceneSourceState.Capturing;
                _deviceManager.DeviceLost += OnDeviceLost;
                _deviceManager.DeviceRestored += OnDeviceRestored;
            }
            catch (Exception ex)
            {
                if (_logger is not null)
                {
                    LogCaptureStartFailed(_logger, Id, ex);
                }
                _state = SceneSourceState.Lost;
            }
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_syncLock)
        {
            if (_disposed || _state == SceneSourceState.Lost)
            {
                return;
            }

            using var frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            var newSize = frame.ContentSize;
            if (newSize.Width <= 0 || newSize.Height <= 0)
            {
                return;
            }

            try
            {
                if (_retainedTexture is null || _width != newSize.Width || _height != newSize.Height)
                {
                    _retainedTexture?.Dispose();
                    var desc = new Texture2DDescription
                    {
                        Width = (uint)newSize.Width,
                        Height = (uint)newSize.Height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Format.B8G8R8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.ShaderResource,
                        CPUAccessFlags = CpuAccessFlags.None
                    };
                    _retainedTexture = _deviceManager.Device.CreateTexture2D(desc);
                    _width = newSize.Width;
                    _height = newSize.Height;
                    sender.Recreate(_winrtDevice!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, newSize);
                }

                using var surfaceTexture = WindowsGraphicsInterop.GetTextureFromSurface(frame.Surface);
                lock (_deviceManager.ContextLock)
                {
                    _deviceManager.Context.CopyResource(_retainedTexture, surfaceTexture);
                }

                _lastFrameTimestampQpc = Stopwatch.GetTimestamp();
                _frameSequence++;
                if (!_hasReceivedFrame && _logger is not null)
                {
                    LogFirstFrame(_logger, Id, _width, _height);
                }

                _hasReceivedFrame = true;
            }
            catch (Exception ex)
            {
                if (_logger is not null)
                {
                    LogFrameProcessingFailed(_logger, Id, ex);
                }
            }
        }
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        lock (_syncLock)
        {
            if (_logger is not null)
            {
                LogItemClosed(_logger, Id);
            }
            State = SceneSourceState.Lost;
        }
    }

    private void OnDeviceLost(object? sender, EventArgs e)
    {
        lock (_syncLock)
        {
            _winrtDevice?.Dispose();
            _winrtDevice = null;
            _retainedTexture?.Dispose();
            _retainedTexture = null;
        }
    }

    private void OnDeviceRestored(object? sender, EventArgs e)
    {
        lock (_syncLock)
        {
            if (_disposed || _item is null)
            {
                return;
            }

            try
            {
                _winrtDevice?.Dispose();
                _retainedTexture?.Dispose();
                _retainedTexture = null;

                _winrtDevice = _deviceManager.CreateWinRTDevice();
                _framePool?.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, new SizeInt32 { Width = _width, Height = _height });
            }
            catch (Exception ex)
            {
                if (_logger is not null)
                {
                    LogDeviceRestoredFailed(_logger, Id, ex);
                }
            }
        }
    }

    public ValueTask<ID3D11Texture2D?> AcquireFrameAsync(CancellationToken ct = default)
    {
        lock (_syncLock)
        {
            if (_disposed)
            {
                return ValueTask.FromResult<ID3D11Texture2D?>(null);
            }

            if (_isWindow)
            {
                if (!VideoSourceCatalog.WindowExists((long)_targetHandle))
                {
                    State = SceneSourceState.Lost;
                    return ValueTask.FromResult<ID3D11Texture2D?>(null);
                }

                if (VideoSourceCatalog.IsWindowMinimized((long)_targetHandle))
                {
                    State = SceneSourceState.TemporarilyUnavailable;
                    return ValueTask.FromResult<ID3D11Texture2D?>(null);
                }

                if (_state == SceneSourceState.TemporarilyUnavailable)
                {
                    State = SceneSourceState.Capturing;
                }
            }

            return ValueTask.FromResult(_hasReceivedFrame ? _retainedTexture : null);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_syncLock)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            _deviceManager.DeviceLost -= OnDeviceLost;
            _deviceManager.DeviceRestored -= OnDeviceRestored;

            if (_item is not null)
            {
                _item.Closed -= OnItemClosed;
            }

            if (_framePool is not null)
            {
                _framePool.FrameArrived -= OnFrameArrived;
                _framePool.Dispose();
                _framePool = null;
            }

            _session?.Dispose();
            _session = null;

            _retainedTexture?.Dispose();
            _retainedTexture = null;

            _winrtDevice?.Dispose();
            _winrtDevice = null;

            State = SceneSourceState.Lost;
        }

        return ValueTask.CompletedTask;
    }

    [LoggerMessage(LogLevel.Error, "Failed to start capture for source {SourceId}.")]
    private static partial void LogCaptureStartFailed(ILogger logger, string sourceId, Exception ex);

    [LoggerMessage(LogLevel.Warning, "Frame processing error on source {SourceId}.")]
    private static partial void LogFrameProcessingFailed(ILogger logger, string sourceId, Exception ex);

    [LoggerMessage(LogLevel.Information, "Capture {SourceId} delivered its first picture ({Width}x{Height}).")]
    private static partial void LogFirstFrame(ILogger logger, string sourceId, int width, int height);

    [LoggerMessage(LogLevel.Information, "Capture target closed for source {SourceId}.")]
    private static partial void LogItemClosed(ILogger logger, string sourceId);

    [LoggerMessage(LogLevel.Error, "Recreating frame pool after device restore failed on {SourceId}.")]
    private static partial void LogDeviceRestoredFailed(ILogger logger, string sourceId, Exception ex);
}

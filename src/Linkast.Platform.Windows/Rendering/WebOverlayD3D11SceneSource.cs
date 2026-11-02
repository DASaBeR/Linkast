// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core.Rendering;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Linkast.Platform.Windows.Rendering;

/// <summary>
/// A scene source bridging an off-screen web page or alert overlay (WebView2 composition)
/// into a Direct3D 11 GPU texture for in-app compositing.
/// </summary>
public sealed partial class WebOverlayD3D11SceneSource : ID3D11SceneSource, IAsyncDisposable
{
    private readonly D3D11DeviceManager _deviceManager;
    private readonly Func<byte[], bool> _copyLatest;
    private readonly Func<bool>? _isAlive;
    private readonly ILogger? _logger;
    private readonly Lock _gate = new();

    private readonly byte[] _cpuBuffer;
    private int _width;
    private int _height;
    private ID3D11Texture2D? _texture;
    private bool _hasFrame;
    private bool _disposed;

    public string Key { get; }

    public string Id => Key;

    public SceneSourceState State
    {
        get
        {
            if (_disposed || (_isAlive is not null && !_isAlive()))
            {
                return SceneSourceState.Lost;
            }

            return SceneSourceState.Capturing;
        }
    }

    public event EventHandler<SceneSourceState>? StateChanged;

    public WebOverlayD3D11SceneSource(
        string key,
        D3D11DeviceManager deviceManager,
        int width,
        int height,
        Func<byte[], bool> copyLatest,
        Func<bool>? isAlive = null,
        ILogger? logger = null)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
        _copyLatest = copyLatest ?? throw new ArgumentNullException(nameof(copyLatest));
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Dimensions must be positive.");
        }

        _width = width;
        _height = height;
        _cpuBuffer = new byte[width * height * 4];
        _isAlive = isAlive;
        _logger = logger;

        _deviceManager.DeviceLost += OnDeviceLost;
        _deviceManager.DeviceRestored += OnDeviceRestored;
    }

    public ValueTask<ID3D11Texture2D?> AcquireFrameAsync(CancellationToken ct = default)
    {
        if (_isAlive is not null && !_isAlive())
        {
            StateChanged?.Invoke(this, SceneSourceState.Lost);
            return ValueTask.FromResult<ID3D11Texture2D?>(null);
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.FromResult<ID3D11Texture2D?>(null);
            }

            EnsureTextureLocked();

            bool updated = false;
            try
            {
                updated = _copyLatest(_cpuBuffer);
            }
            catch (Exception ex)
            {
                if (_logger is not null)
                {
                    LogOverlayFetchFailed(_logger, Key, ex);
                }
            }

            if (updated && _texture is not null)
            {
                lock (_deviceManager.ContextLock)
                {
                    _deviceManager.Context.UpdateSubresource(_cpuBuffer, _texture, 0, (uint)(_width * 4), 0);
                }
                _hasFrame = true;
            }

            return ValueTask.FromResult(_hasFrame ? _texture : null);
        }
    }

    private void EnsureTextureLocked()
    {
        if (_texture is not null)
        {
            return;
        }

        lock (_deviceManager.ContextLock)
        {
            var desc = new Texture2DDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
            };

            _texture = _deviceManager.Device.CreateTexture2D(desc);

            if (_hasFrame)
            {
                _deviceManager.Context.UpdateSubresource(_cpuBuffer, _texture, 0, (uint)(_width * 4), 0);
            }
        }
    }

    private void OnDeviceLost(object? sender, EventArgs e)
    {
        lock (_deviceManager.ContextLock)
        {
            _texture?.Dispose();
            _texture = null;
        }
    }

    private void OnDeviceRestored(object? sender, EventArgs e)
    {
        lock (_deviceManager.ContextLock)
        {
            _texture?.Dispose();
            _texture = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            _deviceManager.DeviceLost -= OnDeviceLost;
            _deviceManager.DeviceRestored -= OnDeviceRestored;

            lock (_deviceManager.ContextLock)
            {
                _texture?.Dispose();
                _texture = null;
            }
        }

        StateChanged?.Invoke(this, SceneSourceState.Lost);
        return ValueTask.CompletedTask;
    }

    [LoggerMessage(LogLevel.Warning, "Failed to copy latest frame from web overlay '{Key}'.")]
    private static partial void LogOverlayFetchFailed(ILogger logger, string key, Exception ex);
}

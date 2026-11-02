// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core.Camera;
using Linkast.Core.Rendering;
using Linkast.Platform.Windows.Camera;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Linkast.Platform.Windows.Rendering;

/// <summary>
/// A scene source that uploads a static image, banner, logo or rendered away-card
/// into a Direct3D 11 GPU texture for compositing.
/// </summary>
public sealed partial class ImageD3D11SceneSource : ID3D11SceneSource, IAsyncDisposable
{
    private readonly D3D11DeviceManager _deviceManager;
    private readonly string? _filePath;
    private readonly BackgroundImageCache? _imageCache;
    private readonly ILogger? _logger;
    private readonly Lock _dataLock = new();

    private byte[]? _bgraPixels;
    private int _width;
    private int _height;
    private ID3D11Texture2D? _texture;
    private bool _needsUpload;
    private bool _loadFailed;
    private bool _disposed;

    public string Key { get; }

    public string Id => Key;

    public SceneSourceState State => _disposed ? SceneSourceState.Lost : SceneSourceState.Capturing;

    public event EventHandler<SceneSourceState>? StateChanged;

    public ImageD3D11SceneSource(
        string key,
        D3D11DeviceManager deviceManager,
        byte[] bgraPixels,
        int width,
        int height,
        ILogger? logger = null)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
        ArgumentNullException.ThrowIfNull(bgraPixels);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Dimensions must be positive.");
        }

        _width = width;
        _height = height;
        _bgraPixels = (byte[])bgraPixels.Clone();
        _needsUpload = true;
        _logger = logger;

        _deviceManager.DeviceLost += OnDeviceLost;
        _deviceManager.DeviceRestored += OnDeviceRestored;
    }

    public ImageD3D11SceneSource(
        string key,
        D3D11DeviceManager deviceManager,
        string filePath,
        BackgroundImageCache? imageCache = null,
        ILogger? logger = null)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _imageCache = imageCache;
        _logger = logger;
        _needsUpload = true;

        _deviceManager.DeviceLost += OnDeviceLost;
        _deviceManager.DeviceRestored += OnDeviceRestored;
    }

    public void UpdatePixels(byte[] bgraPixels, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bgraPixels);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Dimensions must be positive.");
        }

        lock (_dataLock)
        {
            _width = width;
            _height = height;
            _bgraPixels = (byte[])bgraPixels.Clone();
            _needsUpload = true;
        }
    }

    public async ValueTask<ID3D11Texture2D?> AcquireFrameAsync(CancellationToken ct = default)
    {
        if (_bgraPixels is null && _filePath is not null && !_loadFailed)
        {
            await TryLoadFromFileAsync().ConfigureAwait(false);
        }

        lock (_dataLock)
        {
            if (_disposed)
            {
                return null;
            }

            if (_bgraPixels is null || _width <= 0 || _height <= 0)
            {
                return _texture;
            }

            if (_needsUpload || _texture is null)
            {
                UploadTextureLocked();
                _needsUpload = false;
            }

            return _texture;
        }
    }

    private async ValueTask TryLoadFromFileAsync()
    {
        if (_filePath is null)
        {
            return;
        }

        try
        {
            BgraFrame? frame = null;
            if (_imageCache is not null)
            {
                frame = _imageCache.Get(_filePath);
            }

            frame ??= await BackgroundImageCache.DecodeAsync(_filePath).ConfigureAwait(false);
            if (frame is not null)
            {
                lock (_dataLock)
                {
                    _width = frame.Width;
                    _height = frame.Height;
                    _bgraPixels = (byte[])frame.Pixels.Clone();
                    _needsUpload = true;
                }
            }
            else
            {
                _loadFailed = true;
            }
        }
        catch (Exception ex)
        {
            _loadFailed = true;
            if (_logger is not null)
            {
                LogImageLoadFailed(_logger, _filePath, ex);
            }
        }
    }

    private void UploadTextureLocked()
    {
        if (_bgraPixels is null || _width <= 0 || _height <= 0)
        {
            return;
        }

        lock (_deviceManager.ContextLock)
        {
            if (_texture is null || _texture.Description.Width != (uint)_width || _texture.Description.Height != (uint)_height)
            {
                _texture?.Dispose();
                _texture = null;

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
            }

            _deviceManager.Context.UpdateSubresource(_bgraPixels, _texture, 0, (uint)(_width * 4), 0);
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

        lock (_dataLock)
        {
            _needsUpload = true;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_dataLock)
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

    [LoggerMessage(LogLevel.Warning, "Failed to load image from '{Path}'.")]
    private static partial void LogImageLoadFailed(ILogger logger, string path, Exception ex);
}

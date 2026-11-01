// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Linkast.Core.Audio;
using Linkast.Core.Camera;
using Linkast.Core.Rendering;
using Linkast.Platform.Windows.Camera;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Linkast.Platform.Windows.Rendering;

/// <summary>
/// A scene source that renders a camera layer with its full effects pipeline
/// (segmentation, background blur/image, auto-framing, shapes, LUT) into a D3D11 GPU texture.
/// </summary>
public sealed partial class CameraD3D11SceneSource : ID3D11SceneSource, IAsyncDisposable
{
    private readonly D3D11DeviceManager _deviceManager;
    private readonly LiveCameraLayer _layer;
    private readonly ICameraService _cameras;
    private readonly CameraLayerRenderer _renderer = new();
    private readonly Lock _feedGate = new();
    private readonly LayerEffectsBinding _effects;
    private readonly ILogger? _logger;

    private ICameraFeed? _feed;
    private CameraFormatRequest? _feedFormat;
    private long _lastSequence;
    private ID3D11Texture2D? _texture;
    private byte[] _cpuBuffer = [];
    private int _width;
    private int _height;
    private bool _disposed;

    public string Key { get; }

    public string Id => Key;

    public SceneSourceState State
    {
        get
        {
            if (_disposed)
            {
                return SceneSourceState.Lost;
            }

            var f = _feed;
            if (f is null)
            {
                return SceneSourceState.Available;
            }

            return f.State switch
            {
                CameraState.Running => SceneSourceState.Capturing,
                CameraState.Starting => SceneSourceState.Available,
                _ => SceneSourceState.TemporarilyUnavailable,
            };
        }
    }

    public event EventHandler<SceneSourceState>? StateChanged;

    public CameraD3D11SceneSource(
        string key,
        D3D11DeviceManager deviceManager,
        LiveCameraLayer layer,
        ICameraService cameras,
        int width,
        int height,
        ISegmentationService? segmentation = null,
        BackgroundImageCache? images = null,
        IFramingService? framing = null,
        AudioLevelBus? audio = null,
        ILogger? logger = null)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
        _layer = layer ?? throw new ArgumentNullException(nameof(layer));
        _cameras = cameras ?? throw new ArgumentNullException(nameof(cameras));
        _width = Math.Max(64, width);
        _height = Math.Max(64, height);
        _logger = logger;
        _effects = new LayerEffectsBinding(segmentation, images, framing, audio);

        _layer.SourceChanged += OnSourceChanged;
        _deviceManager.DeviceRestored += OnDeviceRestored;
        _deviceManager.DeviceLost += OnDeviceLost;
        SyncFeed();
    }

    private void OnSourceChanged(object? sender, EventArgs e) => SyncFeed();

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

    private void SyncFeed()
    {
        ICameraFeed? old = null;
        lock (_feedGate)
        {
            var wanted = _layer.Enabled ? _layer.Device : null;
            var format = _layer.Format;
            if (_feed?.Device.Id == wanted?.Id && (wanted is null || _feedFormat == format))
            {
                return;
            }

            old = _feed;
            _feed = wanted is null ? null : _cameras.Open(wanted, format);
            _feedFormat = format;
            _lastSequence = 0;
            _renderer.Reset();
        }

        if (old is not null)
        {
            _ = old.DisposeAsync().AsTask();
        }

        StateChanged?.Invoke(this, State);
    }

    public ValueTask<ID3D11Texture2D?> AcquireFrameAsync(CancellationToken ct = default)
    {
        if (_disposed)
        {
            return ValueTask.FromResult<ID3D11Texture2D?>(null);
        }

        ICameraFeed? feed;
        lock (_feedGate)
        {
            feed = _feed;
        }

        if (feed is null || feed.State != CameraState.Running)
        {
            return ValueTask.FromResult(_texture);
        }

        var source = feed.Frames.TryBeginRead(_lastSequence);
        if (source is null)
        {
            return ValueTask.FromResult(_texture);
        }

        try
        {
            EnsureResources(_width, _height);
            var fx = _effects.Update(_layer);
            _renderer.Render(source, _layer.Settings, _cpuBuffer, _width, _height, fx.Mask, fx.BackgroundImage, fx.Context, fx.Framing);
            _lastSequence = source.Sequence;

            if (_texture is not null)
            {
                lock (_deviceManager.ContextLock)
                {
                    _deviceManager.Context.UpdateSubresource(_cpuBuffer, _texture, 0, (uint)(_width * 4), 0);
                }
            }
        }
        catch (Exception ex)
        {
            if (_logger is not null)
            {
                LogCameraRenderFailed(_logger, Key, ex);
            }
        }
        finally
        {
            feed.Frames.EndRead(source);
        }

        return ValueTask.FromResult(_texture);
    }

    private void EnsureResources(int width, int height)
    {
        if (_texture is not null && _width == width && _height == height)
        {
            return;
        }

        _width = width;
        _height = height;
        var requiredBytes = width * height * 4;
        if (_cpuBuffer.Length < requiredBytes)
        {
            _cpuBuffer = new byte[requiredBytes];
        }

        lock (_deviceManager.ContextLock)
        {
            _texture?.Dispose();
            _texture = null;

            var desc = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
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
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _layer.SourceChanged -= OnSourceChanged;
        _deviceManager.DeviceRestored -= OnDeviceRestored;
        _deviceManager.DeviceLost -= OnDeviceLost;

        ICameraFeed? feed;
        lock (_feedGate)
        {
            feed = _feed;
            _feed = null;
        }

        if (feed is not null)
        {
            await feed.DisposeAsync().ConfigureAwait(false);
        }

        await _effects.DisposeAsync().ConfigureAwait(false);

        lock (_deviceManager.ContextLock)
        {
            _texture?.Dispose();
            _texture = null;
        }
    }

    [LoggerMessage(LogLevel.Warning, "Failed to render camera frame for source '{Key}'.")]
    private static partial void LogCameraRenderFailed(ILogger logger, string key, Exception ex);
}

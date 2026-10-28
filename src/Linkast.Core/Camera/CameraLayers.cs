// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core.Camera;

/// <summary>
/// A camera layer whose look can change while streaming. The UI edits it; the preview and the
/// stream read it every frame, so changes apply immediately without restarting anything.
/// </summary>
public sealed class LiveCameraLayer
{
    private CameraLayerSettings _settings = CameraLayerSettings.Default;
    private CameraDevice? _device;
    private CameraFormatRequest _format = CameraFormatRequest.Auto;
    private volatile bool _enabled = true;

    public LiveCameraLayer(string layerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerId);
        LayerId = layerId;
    }

    public string LayerId { get; }

    /// <summary>Raised on the setter's thread when device, format or enabled state changes (not for settings).</summary>
    public event EventHandler? SourceChanged;

    public CameraLayerSettings Settings
    {
        get => Volatile.Read(ref _settings);
        set => Volatile.Write(ref _settings, (value ?? CameraLayerSettings.Default).Clamped());
    }

    public CameraDevice? Device
    {
        get => Volatile.Read(ref _device);
        set
        {
            if (Volatile.Read(ref _device) != value)
            {
                Volatile.Write(ref _device, value);
                SourceChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public CameraFormatRequest Format
    {
        get => Volatile.Read(ref _format);
        set
        {
            if (Volatile.Read(ref _format) != value)
            {
                Volatile.Write(ref _format, value ?? CameraFormatRequest.Auto);
                SourceChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Camera-off switch. When false the device is released and the layer is transparent.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled != value)
            {
                _enabled = value;
                SourceChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}

/// <summary>Places a camera layer on the output canvas for one stream.</summary>
/// <param name="Placement">Normalized to the output canvas.</param>
/// <param name="StartHidden">Starts off the canvas: the camera is in another scene, ready to be shown by a switch.</param>
public sealed record CameraLayerBinding(LiveCameraLayer Layer, NormalizedRect Placement, bool StartHidden = false)
{
    /// <summary>The part of the rendered camera that is shown (fractions of it); the rest is trimmed away.</summary>
    public NormalizedRect Crop { get; init; } = NormalizedRect.Full;
}

/// <summary>A rendered layer that FFmpeg reads from a local named pipe as raw BGRA video.</summary>
public interface IVideoLayerInput : IAsyncDisposable
{
    string LayerId { get; }

    /// <summary>Full pipe path, for example <c>\\.\pipe\linkast-cam-a1b2</c>.</summary>
    string PipePath { get; }

    int Width { get; }

    int Height { get; }

    int FrameRate { get; }

    /// <summary>Completes when the pipe closes. Never faults for camera problems; those only make the layer transparent.</summary>
    Task Completion { get; }

    /// <summary>Creates the pipe and starts serving frames once FFmpeg connects. Call before launching FFmpeg.</summary>
    Task StartAsync(CancellationToken cancellationToken);
}

public interface IVideoLayerInputProvider
{
    /// <summary>Creates a pipe input that renders <paramref name="layer"/> at the given pixel size.</summary>
    IVideoLayerInput Open(LiveCameraLayer layer, int width, int height, int frameRate);
}

/// <summary>Changes the composition of a running stream without restarting it.</summary>
public interface ILiveSceneControl
{
    /// <summary>
    /// Moves or resizes a layer in the running stream. Returns false when no stream is live or the
    /// layer is not part of it (the new placement will apply on the next start).
    /// </summary>
    Task<bool> MoveLayerAsync(string layerId, NormalizedRect placement, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hides a layer in the running stream by moving it off the canvas. The camera stays open, so it
    /// can reappear instantly (for example when switching back to a layout that shows it).
    /// </summary>
    Task<bool> HideLayerAsync(string layerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves, refits, trims, shows or hides the captured game or screen in the running stream
    /// (null placement hides it). False when nothing is live or the stream has no captured picture.
    /// </summary>
    Task<bool> SetBaseAsync(NormalizedRect? placement, NormalizedRect crop, BaseFit fit, CancellationToken cancellationToken = default);
}

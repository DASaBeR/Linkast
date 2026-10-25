// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core.Camera;
using Linkast.Platform.Windows.Layers;

namespace Linkast.Platform.Windows.Camera;

/// <summary>
/// Serves a rendered camera layer to FFmpeg. The layer is transparent while the camera is off,
/// unplugged or failing, so camera problems never interrupt the stream. Device switches and
/// camera-off apply live. Pipe and pacing come from <see cref="PipeFrameWriter"/>.
/// </summary>
public sealed class CameraLayerPipeInput : PipeFrameWriter
{
    private readonly LiveCameraLayer _layer;
    private readonly ICameraService _cameras;
    private readonly CameraLayerRenderer _renderer = new();
    private readonly Lock _feedGate = new();
    private readonly LayerEffectsBinding _effects;
    private ICameraFeed? _feed;
    private CameraFormatRequest? _feedFormat;
    private long _lastSequence;
    private bool _transparent = true;

    public CameraLayerPipeInput(LiveCameraLayer layer, ICameraService cameras, int width, int height, int frameRate,
        ISegmentationService? segmentation = null, BackgroundImageCache? images = null,
        IFramingService? framing = null, Linkast.Core.Audio.AudioLevelBus? audio = null)
        : base((layer ?? throw new ArgumentNullException(nameof(layer))).LayerId, "cam", width, height, frameRate)
    {
        ArgumentNullException.ThrowIfNull(cameras);
        _effects = new LayerEffectsBinding(segmentation, images, framing, audio);
        _layer = layer;
        _cameras = cameras;
    }

    /// <summary>Frames that contained camera content (not transparent). Diagnostics and tests.</summary>
    public long CameraFramesRendered { get; private set; }

    protected override void OnStarting()
    {
        _layer.SourceChanged += OnSourceChanged;
        SyncFeed();
    }

    protected override async ValueTask OnDisposeAsync()
    {
        _layer.SourceChanged -= OnSourceChanged;
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
    }

    private void OnSourceChanged(object? sender, EventArgs e) => SyncFeed();

    /// <summary>Opens, switches or releases the camera to match the layer's device and on/off state.</summary>
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
    }

    protected override void ProduceFrame()
    {
        ICameraFeed? feed;
        lock (_feedGate)
        {
            feed = _feed;
        }

        if (feed is null || feed.State != CameraState.Running)
        {
            if (!_transparent)
            {
                CameraLayerRenderer.RenderTransparent(Frame, Width, Height);
                _transparent = true;
            }

            return;
        }

        var fx = _effects.Update(_layer);
        var source = feed.Frames.TryBeginRead(_lastSequence);
        if (source is null)
        {
            return; // no new camera frame: repeat the previous image
        }

        try
        {
            _renderer.Render(source, _layer.Settings, Frame, Width, Height, fx.Mask, fx.BackgroundImage, fx.Context, fx.Framing);
            _lastSequence = source.Sequence;
            _transparent = false;
            CameraFramesRendered++;
        }
        finally
        {
            feed.Frames.EndRead(source);
        }
    }
}

/// <summary>Creates camera layer pipes backed by the shared camera service.</summary>
public sealed class CameraLayerPipeInputProvider(
    ICameraService cameras,
    ISegmentationService? segmentation = null,
    BackgroundImageCache? images = null,
    IFramingService? framing = null,
    Linkast.Core.Audio.AudioLevelBus? audio = null)
    : IVideoLayerInputProvider
{
    public IVideoLayerInput Open(LiveCameraLayer layer, int width, int height, int frameRate) =>
        new CameraLayerPipeInput(layer, cameras, width, height, frameRate, segmentation, images, framing, audio);
}

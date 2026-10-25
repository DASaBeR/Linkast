// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Linkast.Core.Audio;
using Linkast.Core.Camera;

namespace Linkast.Platform.Windows.Camera;

/// <summary>Everything a renderer needs for one frame beyond the camera picture itself.</summary>
public readonly record struct EffectInputs(SegmentationMask? Mask, BgraFrame? BackgroundImage, FrameContext Context, FramingCrop? Framing);

/// <summary>
/// Gives one consumer (preview or stream pipe) the shared per-camera services its layer needs:
/// the person mask, the background image, the auto-framing crop, the audio level, and a clock shared
/// by all consumers so animations look identical in the preview and the stream. Subscribes only to
/// what the layer's settings use, so turning an effect off releases its worker immediately.
/// Call <see cref="Update"/> from the consumer's render thread before each render.
/// </summary>
public sealed class LayerEffectsBinding(
    ISegmentationService? segmentation,
    BackgroundImageCache? images,
    IFramingService? framing = null,
    AudioLevelBus? audio = null) : IAsyncDisposable
{
    private ISegmentationSubscription? _subscription;
    private (string Device, SegmentationQuality Quality, bool Gpu, CameraFormatRequest Format)? _key;
    private IFramingSubscription? _framing;
    private (string Device, string Layer, CameraFormatRequest Format)? _framingKey;

    /// <summary>Status of the subscribed segmentation worker, or Off.</summary>
    public SegmentationStatus Status => _subscription?.Status ?? SegmentationStatus.Off;

    /// <summary>True while auto-framing is following a face.</summary>
    public bool FramingTracking => _framing?.Tracking == true;

    public string? FramingProblem => _framing?.Problem;

    public ISegmentationSubscription? Subscription => _subscription;

    /// <summary>Raised when the segmentation worker's status changes (on a worker thread).</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Monotonic app-wide time, so every consumer animates in phase.</summary>
    public static TimeSpan SharedClock => Stopwatch.GetElapsedTime(0);

    /// <summary>Reconciles subscriptions with the layer and returns this frame's effect inputs.</summary>
    public EffectInputs Update(LiveCameraLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        var settings = layer.Settings;
        var device = layer.Enabled ? layer.Device : null;

        SyncSegmentation(settings.Background, device, layer.Format);
        SyncFraming(settings.Framing, device, layer.LayerId, layer.Format);

        var bg = settings.Background;
        var image = bg.Mode == BackgroundMode.Image ? images?.Get(bg.ImagePath) : null;

        var fx = settings.Effects.Audio;
        if (audio is not null && fx.Enabled)
        {
            audio.Meter.ThresholdDb = fx.ThresholdDb;
        }

        var context = new FrameContext(
            SharedClock,
            audio?.Active == true ? audio.Meter.Level : 0,
            audio?.Active == true && audio.Meter.Speaking);
        return new EffectInputs(_subscription?.Latest, image, context, _framing?.Latest);
    }

    public async ValueTask DisposeAsync()
    {
        var seg = _subscription;
        _subscription = null;
        _key = null;
        if (seg is not null)
        {
            seg.StatusChanged -= OnStatusChanged;
            await seg.DisposeAsync().ConfigureAwait(false);
        }

        var fr = _framing;
        _framing = null;
        _framingKey = null;
        if (fr is not null)
        {
            await fr.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void SyncSegmentation(BackgroundSettings bg, CameraDevice? device, CameraFormatRequest format)
    {
        var wanted = bg.NeedsSegmentation && device is not null && segmentation is not null
            ? (device.Id, bg.Quality, bg.PreferGpu, format)
            : ((string Device, SegmentationQuality Quality, bool Gpu, CameraFormatRequest Format)?)null;
        if (wanted == _key)
        {
            return;
        }

        var old = _subscription;
        _subscription = null;
        if (old is not null)
        {
            old.StatusChanged -= OnStatusChanged;
            _ = old.DisposeAsync().AsTask();
        }

        _key = wanted;
        if (wanted is { } k)
        {
            _subscription = segmentation!.Subscribe(device!, k.Format, k.Quality, k.Gpu);
            _subscription.StatusChanged += OnStatusChanged;
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SyncFraming(FramingSettings settings, CameraDevice? device, string layerId, CameraFormatRequest format)
    {
        var wanted = settings.Enabled && device is not null && framing is not null
            ? (device.Id, layerId, format)
            : ((string Device, string Layer, CameraFormatRequest Format)?)null;
        if (wanted != _framingKey)
        {
            var old = _framing;
            _framing = null;
            if (old is not null)
            {
                _ = old.DisposeAsync().AsTask();
            }

            _framingKey = wanted;
            if (wanted is { } k)
            {
                _framing = framing!.Subscribe(device!, k.Format, k.Layer, settings);
            }
        }

        _framing?.Update(settings);
    }

    private void OnStatusChanged(object? sender, EventArgs e) => StatusChanged?.Invoke(this, e);
}

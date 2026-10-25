// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core.Camera;

/// <summary>Automatic framing settings (camera-level). When enabled they override manual crop and zoom.</summary>
public sealed record FramingSettings
{
    public static FramingSettings Off { get; } = new();

    public bool Enabled { get; init; }

    /// <summary>Space above the head, as a fraction of the crop height (0 to 0.3).</summary>
    public double Headroom { get; init; } = 0.12;

    /// <summary>Target face height as a fraction of the crop height (0.15 = wide, 0.45 = close-up).</summary>
    public double FaceSize { get; init; } = 0.28;

    /// <summary>Maximum zoom (1 to 4). Limits how tight the crop can get.</summary>
    public double MaxZoom { get; init; } = 2.5;

    /// <summary>0 = slow and calm, 1 = fast and responsive.</summary>
    public double Speed { get; init; } = 0.4;

    public FramingSettings Clamped() => this with
    {
        Headroom = CameraAdjustments.Clamp(Headroom, 0, 0.3, 0.12),
        FaceSize = CameraAdjustments.Clamp(FaceSize, 0.15, 0.45, 0.28),
        MaxZoom = CameraAdjustments.Clamp(MaxZoom, 1, 4, 2.5),
        Speed = CameraAdjustments.Clamp(Speed, 0, 1, 0.4),
    };
}

/// <summary>A detected face in normalized source coordinates (0 to 1).</summary>
public readonly record struct FaceBox(double X, double Y, double Width, double Height)
{
    public double CenterX => X + (Width / 2);

    public double CenterY => Y + (Height / 2);
}

/// <summary>
/// A crop in normalized source coordinates: centre, and height as a fraction of the source height.
/// Independent of the output aspect ratio, so the preview and the stream (which may have different
/// layer shapes) frame the same way.
/// </summary>
public readonly record struct FramingCrop(double CenterX, double CenterY, double Height)
{
    public static FramingCrop Full { get; } = new(0.5, 0.5, 1);

    /// <summary>Zoom value for <see cref="CameraLayerSettings.Zoom"/> giving this crop for a layer of <paramref name="layerAspect"/>.</summary>
    public double ToZoom(int sourceWidth, int sourceHeight, double layerAspect, double maxZoom)
    {
        var (_, _, _, fullHeight) = LayerGeometry.SourceCrop(sourceWidth, sourceHeight, layerAspect, 1, 0.5, 0.5);
        var zoom = (fullHeight / sourceHeight) / Math.Max(1e-3, Height);
        return Math.Clamp(zoom, 1, Math.Max(1, maxZoom));
    }
}

/// <summary>One consumer's handle on a shared auto-framing worker.</summary>
public interface IFramingSubscription : IAsyncDisposable
{
    /// <summary>The smoothed crop to use now, or null before the worker starts.</summary>
    FramingCrop? Latest { get; }

    /// <summary>True while a face is being followed.</summary>
    bool Tracking { get; }

    /// <summary>Why framing can't run (for example face detection unsupported), or null.</summary>
    string? Problem { get; }

    /// <summary>Applies changed framing settings (face size, headroom, speed) live.</summary>
    void Update(FramingSettings settings);
}

/// <summary>Runs face detection and framing for a camera layer; shared by its preview and stream.</summary>
public interface IFramingService
{
    IFramingSubscription Subscribe(CameraDevice device, CameraFormatRequest format, string layerId, FramingSettings settings);
}

/// <summary>
/// Turns noisy face detections into calm camera movement. Pure and deterministic (time is passed in),
/// so it is fully unit-tested:
/// - the largest face, or the one nearest the current framing, is followed;
/// - small movements inside a dead zone do not move the crop (no oscillation when sitting still);
/// - zoom only changes when the face size changes noticeably (hysteresis);
/// - a sudden jump must be confirmed by a second detection before the crop follows it;
/// - if the face is lost, the last crop is held, then the view eases back out to the full frame;
/// - movement is a time-based exponential approach, independent of frame rate.
/// </summary>
public sealed class AutoFramer
{
    private static readonly TimeSpan HoldAfterLoss = TimeSpan.FromSeconds(2);
    private const double DeadZone = 0.035;
    private const double ZoomHysteresis = 0.12;
    private const double JumpDistance = 0.25;

    private FramingCrop _current = FramingCrop.Full;
    private FramingCrop _target = FramingCrop.Full;
    private TimeSpan _lastUpdate;
    private TimeSpan _lastSeen = TimeSpan.Zero;
    private FaceBox? _pendingJump;
    private bool _hasUpdated;

    public AutoFramer(FramingSettings settings, double sourceAspect)
    {
        Settings = (settings ?? FramingSettings.Off).Clamped();
        SourceAspect = sourceAspect > 0 ? sourceAspect : 16.0 / 9;
    }

    public FramingSettings Settings { get; set; }

    public double SourceAspect { get; }

    public FramingCrop Current => _current;

    public FramingCrop Target => _target;

    /// <summary>True while a face has been seen recently.</summary>
    public bool Tracking { get; private set; }

    /// <summary>Feeds the faces found in one detection pass.</summary>
    public void OnDetection(IReadOnlyList<FaceBox> faces, TimeSpan time)
    {
        ArgumentNullException.ThrowIfNull(faces);
        if (faces.Count == 0)
        {
            return;
        }

        // Prefer the face nearest the current framing; among similar ones, the largest.
        var face = faces
            .OrderBy(f => Distance(f.CenterX, f.CenterY, _target.CenterX, _target.CenterY) - (f.Height * 0.5))
            .First();

        var desired = DesiredCrop(face);
        var jump = Distance(desired.CenterX, desired.CenterY, _target.CenterX, _target.CenterY);
        if (Tracking && jump > JumpDistance)
        {
            // A big jump may be a false detection: require a second agreeing detection.
            if (_pendingJump is not { } p || Distance(p.CenterX, p.CenterY, face.CenterX, face.CenterY) > 0.1)
            {
                _pendingJump = face;
                return;
            }
        }

        _pendingJump = null;
        _lastSeen = time;
        Tracking = true;

        var centerMoved = Distance(desired.CenterX, desired.CenterY, _target.CenterX, _target.CenterY) > DeadZone * _target.Height;
        var zoomChanged = Math.Abs(desired.Height - _target.Height) / _target.Height > ZoomHysteresis;
        _target = new FramingCrop(
            centerMoved ? desired.CenterX : _target.CenterX,
            centerMoved ? desired.CenterY : _target.CenterY,
            zoomChanged ? desired.Height : _target.Height);
    }

    /// <summary>Advances the smoothing to <paramref name="time"/> and returns the crop to use now.</summary>
    public FramingCrop Update(TimeSpan time)
    {
        if (!_hasUpdated)
        {
            _hasUpdated = true;
            _lastUpdate = time;
        }

        var dt = Math.Max(0, (time - _lastUpdate).TotalSeconds);
        _lastUpdate = time;

        var lost = Tracking && time - _lastSeen > HoldAfterLoss;
        if (lost)
        {
            Tracking = false;
            _target = FramingCrop.Full;
        }

        // Time constant: 1.2 s (calm) to 0.25 s (responsive); easing out after a loss is slower still.
        var tau = 1.2 - (Settings.Speed * 0.95);
        if (!Tracking)
        {
            tau *= 2.5;
        }

        var k = 1 - Math.Exp(-dt / tau);
        _current = new FramingCrop(
            _current.CenterX + ((_target.CenterX - _current.CenterX) * k),
            _current.CenterY + ((_target.CenterY - _current.CenterY) * k),
            _current.Height + ((_target.Height - _current.Height) * k));
        return _current;
    }

    /// <summary>The crop that puts this face at the configured size with the configured headroom.</summary>
    internal FramingCrop DesiredCrop(FaceBox face)
    {
        var s = Settings;
        var minHeight = 1 / s.MaxZoom;
        var height = Math.Clamp(face.Height / s.FaceSize, minHeight, 1);
        var top = face.Y - (s.Headroom * height);
        var centerY = top + (height / 2);
        return new FramingCrop(Math.Clamp(face.CenterX, 0, 1), Math.Clamp(centerY, 0, 1), height);
    }

    private double Distance(double x1, double y1, double x2, double y2)
    {
        // Measure in a square space so horizontal and vertical distances are comparable.
        var dx = (x1 - x2) * SourceAspect;
        var dy = y1 - y2;
        return Math.Sqrt((dx * dx) + (dy * dy)) / SourceAspect;
    }
}

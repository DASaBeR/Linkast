// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core.Camera;

public enum BackgroundMode
{
    /// <summary>No segmentation; the camera image as it is.</summary>
    Original,
    Blur,

    /// <summary>Person cut out on a transparent background (shows the scene behind).</summary>
    Remove,

    /// <summary>Person in front of a still image.</summary>
    Image,
}

/// <summary>Trade-off between speed and edge quality of the person segmentation.</summary>
public enum SegmentationQuality
{
    Performance,
    Balanced,
    Quality,
}

/// <summary>Background replacement settings of a camera layer (camera-level, stored in presets).</summary>
public sealed record BackgroundSettings
{
    public static BackgroundSettings Off { get; } = new();

    public BackgroundMode Mode { get; init; } = BackgroundMode.Original;

    /// <summary>0 to 1. Strength of the background blur.</summary>
    public double BlurStrength { get; init; } = 0.6;

    /// <summary>0 to 1. Softens the person's outline; higher hides small mask errors.</summary>
    public double EdgeSoftness { get; init; } = 0.3;

    public SegmentationQuality Quality { get; init; } = SegmentationQuality.Balanced;

    /// <summary>Use the GPU for segmentation when available. When false, the CPU is used.</summary>
    public bool PreferGpu { get; init; } = true;

    /// <summary>Background image file for <see cref="BackgroundMode.Image"/>. A missing file falls back to blur.</summary>
    public string? ImagePath { get; init; }

    public bool NeedsSegmentation => Mode != BackgroundMode.Original;

    public BackgroundSettings Clamped() => this with
    {
        Mode = Enum.IsDefined(Mode) ? Mode : BackgroundMode.Original,
        Quality = Enum.IsDefined(Quality) ? Quality : SegmentationQuality.Balanced,
        BlurStrength = CameraAdjustments.Clamp(BlurStrength, 0, 1, 0.6),
        EdgeSoftness = CameraAdjustments.Clamp(EdgeSoftness, 0, 1, 0.3),
        ImagePath = string.IsNullOrWhiteSpace(ImagePath) ? null : ImagePath,
    };
}

/// <summary>
/// Person alpha for one camera frame, at segmentation resolution (255 = person). Covers the whole
/// source frame; the renderer samples it in source coordinates, so crop, zoom and mirror just work.
/// </summary>
public sealed class SegmentationMask(int width, int height)
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public byte[] Alpha { get; } = new byte[width * height];

    /// <summary>Sequence number of the camera frame the mask was computed from.</summary>
    public long FrameSequence { get; set; }
}

public enum SegmentationState
{
    Off,
    Loading,
    Running,

    /// <summary>Cannot run (model missing, runtime error). The image is shown unprocessed.</summary>
    Unavailable,
}

/// <summary>Live status of segmentation, for the UI and diagnostics.</summary>
/// <param name="Provider">Where inference runs, for example "GPU (DirectML)" or "CPU".</param>
public sealed record SegmentationStatus(SegmentationState State, string? Provider, double InferenceMs, double MasksPerSecond, string? Message)
{
    public static SegmentationStatus Off { get; } = new(SegmentationState.Off, null, 0, 0, null);
}

/// <summary>One consumer's handle on a shared segmentation worker.</summary>
public interface ISegmentationSubscription : IAsyncDisposable
{
    /// <summary>The newest mask, or null before the first one (or when unavailable).</summary>
    SegmentationMask? Latest { get; }

    SegmentationStatus Status { get; }

    event EventHandler? StatusChanged;
}

/// <summary>
/// Runs person segmentation on a camera's frames. One worker per camera and settings is shared by
/// all consumers (preview, stream), so each frame is processed once.
/// </summary>
public interface ISegmentationService
{
    ISegmentationSubscription Subscribe(CameraDevice device, CameraFormatRequest format, SegmentationQuality quality, bool preferGpu);
}

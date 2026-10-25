// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core.Camera;

public enum LayerShape
{
    Rectangle,
    RoundedRectangle,
    Circle,
}

/// <summary>Aspect ratio of the camera layer. Circle always renders 1:1.</summary>
public enum LayerAspect
{
    Wide16x9,
    Standard4x3,
    Square1x1,
    Portrait9x16,
}

/// <summary>
/// Software image adjustments. Neutral values leave the image unchanged, so they are
/// non-destructive and can always be reset.
/// </summary>
public sealed record CameraAdjustments
{
    public static CameraAdjustments Neutral { get; } = new();

    /// <summary>-1 to 1. 0 = unchanged.</summary>
    public double Brightness { get; init; }

    /// <summary>0 to 2. 1 = unchanged.</summary>
    public double Contrast { get; init; } = 1;

    /// <summary>0 to 2. 1 = unchanged, 0 = greyscale.</summary>
    public double Saturation { get; init; } = 1;

    /// <summary>0 to 1. 0 = off.</summary>
    public double Sharpen { get; init; }

    /// <summary>0 to 1. 0 = off. Light temporal noise reduction for static areas.</summary>
    public double Denoise { get; init; }

    /// <summary>
    /// 0 to 1. 0 = off. Automatically brightens dark pictures toward a normal exposure, more strongly
    /// the darker the scene. Bright scenes are left alone.
    /// </summary>
    public double LowLightBoost { get; init; }

    public bool IsNeutral => this == Neutral;

    public CameraAdjustments Clamped() => this with
    {
        Brightness = Clamp(Brightness, -1, 1),
        Contrast = Clamp(Contrast, 0, 2, 1),
        Saturation = Clamp(Saturation, 0, 2, 1),
        Sharpen = Clamp(Sharpen, 0, 1),
        Denoise = Clamp(Denoise, 0, 1),
        LowLightBoost = Clamp(LowLightBoost, 0, 1),
    };

    internal static double Clamp(double v, double min, double max, double fallback = 0) =>
        double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;
}

/// <summary>Opaque ARGB colour.</summary>
public readonly record struct Argb(byte A, byte R, byte G, byte B)
{
    public static Argb White { get; } = new(255, 255, 255, 255);

    public static Argb Accent { get; } = new(255, 0x91, 0x46, 0xFF);

    public uint ToUInt32() => ((uint)A << 24) | ((uint)R << 16) | ((uint)G << 8) | B;

    public static Argb FromUInt32(uint v) => new((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
}

/// <summary>Everything that defines how one camera looks, independent of where it sits in a scene.</summary>
public sealed record CameraLayerSettings
{
    public static CameraLayerSettings Default { get; } = new();

    public CameraAdjustments Adjustments { get; init; } = CameraAdjustments.Neutral;

    public LayerShape Shape { get; init; } = LayerShape.RoundedRectangle;

    public LayerAspect Aspect { get; init; } = LayerAspect.Wide16x9;

    /// <summary>Corner radius as a fraction of the layer's shorter side (0 to 0.5). Rounded rectangle only.</summary>
    public double CornerRadius { get; init; } = 0.08;

    /// <summary>1 to 4. 1 = the widest crop of the chosen aspect.</summary>
    public double Zoom { get; init; } = 1;

    /// <summary>Crop centre in the source image, 0 to 1.</summary>
    public double CenterX { get; init; } = 0.5;

    public double CenterY { get; init; } = 0.5;

    /// <summary>Mirror horizontally, as most people expect from a webcam.</summary>
    public bool Mirror { get; init; }

    /// <summary>Border width as a fraction of the layer's shorter side (0 to 0.1). 0 = none.</summary>
    public double BorderWidth { get; init; }

    public Argb BorderColor { get; init; } = Argb.White;

    /// <summary>Background blur, removal or replacement.</summary>
    public BackgroundSettings Background { get; init; } = BackgroundSettings.Off;

    /// <summary>Automatic framing; overrides <see cref="Zoom"/>, <see cref="CenterX"/> and <see cref="CenterY"/> while a face is tracked.</summary>
    public FramingSettings Framing { get; init; } = FramingSettings.Off;

    /// <summary>Border animation, glow and audio-reactive effects.</summary>
    public LayerEffectSettings Effects { get; init; } = LayerEffectSettings.None;

    public double AspectRatio => Shape == LayerShape.Circle ? 1 : Aspect switch
    {
        LayerAspect.Wide16x9 => 16.0 / 9,
        LayerAspect.Standard4x3 => 4.0 / 3,
        LayerAspect.Square1x1 => 1,
        LayerAspect.Portrait9x16 => 9.0 / 16,
        _ => 16.0 / 9,
    };

    public CameraLayerSettings Clamped() => this with
    {
        Adjustments = (Adjustments ?? CameraAdjustments.Neutral).Clamped(),
        Background = (Background ?? BackgroundSettings.Off).Clamped(),
        Framing = (Framing ?? FramingSettings.Off).Clamped(),
        Effects = (Effects ?? LayerEffectSettings.None).Clamped(),
        CornerRadius = CameraAdjustments.Clamp(CornerRadius, 0, 0.5),
        Zoom = CameraAdjustments.Clamp(Zoom, 1, 4, 1),
        CenterX = CameraAdjustments.Clamp(CenterX, 0, 1, 0.5),
        CenterY = CameraAdjustments.Clamp(CenterY, 0, 1, 0.5),
        BorderWidth = CameraAdjustments.Clamp(BorderWidth, 0, 0.1),
        Shape = Enum.IsDefined(Shape) ? Shape : LayerShape.RoundedRectangle,
        Aspect = Enum.IsDefined(Aspect) ? Aspect : LayerAspect.Wide16x9,
    };
}

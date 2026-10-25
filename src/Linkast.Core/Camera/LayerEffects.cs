// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core.Camera;

public enum BorderAnimation
{
    None,

    /// <summary>The border gently breathes in width and brightness.</summary>
    Pulse,

    /// <summary>The border colour cycles through the hue wheel.</summary>
    ColorCycle,
}

public enum AudioReactiveStyle
{
    /// <summary>The border grows with your voice.</summary>
    BorderPulse,

    /// <summary>A soft glow ring around the camera lights up with your voice.</summary>
    GlowRing,

    /// <summary>The border switches to the indicator colour while you speak.</summary>
    SpeakingIndicator,
}

public sealed record AudioReactiveSettings
{
    public static AudioReactiveSettings Off { get; } = new();

    public bool Enabled { get; init; }

    public AudioReactiveStyle Style { get; init; } = AudioReactiveStyle.BorderPulse;

    /// <summary>Voice level that counts as speaking, in dBFS (-60 to -10).</summary>
    public double ThresholdDb { get; init; } = -40;

    /// <summary>0 to 1. How strongly the effect reacts.</summary>
    public double Intensity { get; init; } = 0.6;

    public Argb IndicatorColor { get; init; } = new(255, 0x30, 0xD1, 0x58);

    public AudioReactiveSettings Clamped() => this with
    {
        Style = Enum.IsDefined(Style) ? Style : AudioReactiveStyle.BorderPulse,
        ThresholdDb = CameraAdjustments.Clamp(ThresholdDb, -60, -10, -40),
        Intensity = CameraAdjustments.Clamp(Intensity, 0, 1, 0.6),
    };
}

/// <summary>Decorative effects around the camera (camera-level, stored in presets).</summary>
public sealed record LayerEffectSettings
{
    public static LayerEffectSettings None { get; } = new();

    public BorderAnimation Animation { get; init; } = BorderAnimation.None;

    /// <summary>0 to 1. Animation speed (one cycle per ~4 s at 0, ~0.7 s at 1).</summary>
    public double AnimationSpeed { get; init; } = 0.4;

    /// <summary>0 to 1. Size of a soft glow outside the shape (0 = none).</summary>
    public double Glow { get; init; }

    public AudioReactiveSettings Audio { get; init; } = AudioReactiveSettings.Off;

    /// <summary>True when the layer needs space outside its shape for a glow.</summary>
    public bool NeedsGlowSpace => Glow > 0 || (Audio.Enabled && Audio.Style == AudioReactiveStyle.GlowRing);

    public LayerEffectSettings Clamped() => this with
    {
        Animation = Enum.IsDefined(Animation) ? Animation : BorderAnimation.None,
        AnimationSpeed = CameraAdjustments.Clamp(AnimationSpeed, 0, 1, 0.4),
        Glow = CameraAdjustments.Clamp(Glow, 0, 1),
        Audio = (Audio ?? AudioReactiveSettings.Off).Clamped(),
    };
}

/// <summary>Per-frame inputs for time- and audio-driven effects.</summary>
/// <param name="Time">Elapsed time; animations are time-based so they look the same at any frame rate.</param>
/// <param name="AudioLevel">0 to 1 from <see cref="Audio.AudioLevelMeter"/>.</param>
public readonly record struct FrameContext(TimeSpan Time, float AudioLevel, bool Speaking)
{
    public static FrameContext Still { get; } = new(TimeSpan.Zero, 0, false);
}

/// <summary>Pure maths for the border and glow of one frame. Unit-tested.</summary>
public static class LayerEffectMath
{
    /// <summary>Border width multiplier, colour and glow strength for this frame.</summary>
    public static (double WidthScale, Argb Color, double GlowStrength) Evaluate(LayerEffectSettings fx, Argb baseColor, FrameContext ctx)
    {
        ArgumentNullException.ThrowIfNull(fx);
        var widthScale = 1.0;
        var color = baseColor;
        var glow = fx.Glow;
        var period = 4.0 - (fx.AnimationSpeed * 3.3);
        var phase = ctx.Time.TotalSeconds / period;

        switch (fx.Animation)
        {
            case BorderAnimation.Pulse:
                var p = 0.5 + (0.5 * Math.Sin(phase * 2 * Math.PI));
                widthScale *= 0.75 + (0.5 * p);
                color = Scale(color, 0.8 + (0.2 * p));
                glow *= 0.6 + (0.4 * p);
                break;
            case BorderAnimation.ColorCycle:
                color = FromHue(phase % 1.0);
                break;
        }

        var audio = fx.Audio;
        if (audio.Enabled)
        {
            var level = Math.Clamp(ctx.AudioLevel, 0, 1);
            switch (audio.Style)
            {
                case AudioReactiveStyle.BorderPulse:
                    widthScale *= 1 + (audio.Intensity * 2.5 * level);
                    break;
                case AudioReactiveStyle.GlowRing:
                    glow = Math.Max(glow, audio.Intensity * level * 1.5);
                    if (ctx.Speaking)
                    {
                        color = audio.IndicatorColor;
                    }

                    break;
                case AudioReactiveStyle.SpeakingIndicator:
                    if (ctx.Speaking)
                    {
                        color = audio.IndicatorColor;
                        widthScale *= 1 + (audio.Intensity * 0.6);
                    }

                    break;
            }
        }

        return (widthScale, color, Math.Clamp(glow, 0, 1));
    }

    public static Argb FromHue(double hue)
    {
        var h = (hue - Math.Floor(hue)) * 6;
        var x = 1 - Math.Abs((h % 2) - 1);
        var (r, g, b) = (int)h switch
        {
            0 => (1.0, x, 0.0),
            1 => (x, 1.0, 0.0),
            2 => (0.0, 1.0, x),
            3 => (0.0, x, 1.0),
            4 => (x, 0.0, 1.0),
            _ => (1.0, 0.0, x),
        };
        return new Argb(255, (byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    private static Argb Scale(Argb c, double f) =>
        new(c.A, (byte)Math.Clamp(c.R * f, 0, 255), (byte)Math.Clamp(c.G * f, 0, 255), (byte)Math.Clamp(c.B * f, 0, 255));
}

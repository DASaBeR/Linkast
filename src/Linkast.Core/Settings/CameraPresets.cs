// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core.Camera;

namespace Linkast.Core.Settings;

/// <summary>
/// A reusable webcam look. <see cref="Look"/> is camera-level (works with any camera);
/// <see cref="Placement"/> is optional scene-level layout. Presets never store a device, so a
/// preset stays valid when cameras change.
/// </summary>
public sealed record CameraPreset
{
    public const int CurrentVersion = 1;

    /// <summary>Stable identifier. Built-in presets use fixed GUIDs.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    public int Version { get; init; } = CurrentVersion;

    public string Name { get; init; } = "Custom";

    public bool IsBuiltIn { get; init; }

    public CameraLayerSettings Look { get; init; } = CameraLayerSettings.Default;

    public NormalizedRect? Placement { get; init; }

    public override string ToString() => Name;
}

/// <summary>The built-in presets from the Webcam Studio brief.</summary>
public static class CameraPresets
{
    public static readonly Guid GamingId = new("6f1c1a40-0b7e-4d61-9b70-1c0f3a9e0001");
    public static readonly Guid JustChattingId = new("6f1c1a40-0b7e-4d61-9b70-1c0f3a9e0002");
    public static readonly Guid ProfessionalId = new("6f1c1a40-0b7e-4d61-9b70-1c0f3a9e0003");
    public static readonly Guid LowLightId = new("6f1c1a40-0b7e-4d61-9b70-1c0f3a9e0004");
    public static readonly Guid MinimalId = new("6f1c1a40-0b7e-4d61-9b70-1c0f3a9e0005");

    public static IReadOnlyList<CameraPreset> BuiltIn { get; } =
    [
        new()
        {
            Id = GamingId,
            Name = "Gaming",
            IsBuiltIn = true,
            Look = CameraLayerSettings.Default with
            {
                Shape = LayerShape.RoundedRectangle,
                CornerRadius = 0.1,
                Aspect = LayerAspect.Wide16x9,
                Zoom = 1.15,
                Mirror = true,
                BorderWidth = 0.025,
                BorderColor = Argb.Accent,
                Adjustments = new() { Saturation = 1.1, Contrast = 1.05, Sharpen = 0.2 },
            },
            Placement = new NormalizedRect(0.71, 0.66, 0.26, 0.26 * 1920 / (16.0 / 9) / 1080),
        },
        new()
        {
            Id = JustChattingId,
            Name = "Just Chatting",
            IsBuiltIn = true,
            Look = CameraLayerSettings.Default with
            {
                Shape = LayerShape.Rectangle,
                Aspect = LayerAspect.Wide16x9,
                Mirror = true,
                Adjustments = new() { Sharpen = 0.15, Denoise = 0.2 },
            },
            Placement = new NormalizedRect(0, 0, 1, 1),
        },
        new()
        {
            Id = ProfessionalId,
            Name = "Professional",
            IsBuiltIn = true,
            Look = CameraLayerSettings.Default with
            {
                Shape = LayerShape.Rectangle,
                Aspect = LayerAspect.Wide16x9,
                BorderWidth = 0.01,
                BorderColor = Argb.White,
                Adjustments = new() { Contrast = 1.05, Sharpen = 0.15, Denoise = 0.3 },
            },
            Placement = new NormalizedRect(0.695, 0.68, 0.28, 0.28),
        },
        new()
        {
            Id = LowLightId,
            Name = "Low Light",
            IsBuiltIn = true,
            Look = CameraLayerSettings.Default with
            {
                Shape = LayerShape.RoundedRectangle,
                Aspect = LayerAspect.Wide16x9,
                Mirror = true,
                Adjustments = new() { Brightness = 0.05, Contrast = 1.05, Saturation = 1.05, Denoise = 0.65, LowLightBoost = 0.7 },
            },
        },
        new()
        {
            Id = MinimalId,
            Name = "Minimal",
            IsBuiltIn = true,
            Look = CameraLayerSettings.Default with
            {
                Shape = LayerShape.Circle,
                Zoom = 1.3,
                Mirror = true,
                BorderWidth = 0.02,
                BorderColor = Argb.White,
            },
            Placement = new NormalizedRect(0.025, 0.72, 0.14, 0.14 * 1920 / 1080.0),
        },
    ];

    /// <summary>All presets: built-in first, then custom ones in the user's order.</summary>
    public static IReadOnlyList<CameraPreset> All(IEnumerable<CameraPreset> custom) => [.. BuiltIn, .. custom.Where(p => !p.IsBuiltIn)];

    public static CameraPreset? Find(Guid? id, IEnumerable<CameraPreset> custom) =>
        id is null ? null : All(custom).FirstOrDefault(p => p.Id == id);

    /// <summary>A new custom copy with a unique name.</summary>
    public static CameraPreset Duplicate(CameraPreset source, IEnumerable<CameraPreset> existing, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var names = existing.Select(p => p.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var baseName = name ?? $"{source.Name} copy";
        var candidate = baseName;
        for (var i = 2; names.Contains(candidate); i++)
        {
            candidate = $"{baseName} {i}";
        }

        return source with { Id = Guid.NewGuid(), Name = candidate, IsBuiltIn = false, Version = CameraPreset.CurrentVersion };
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core.Camera;

namespace Linkast.Core.Settings;

public enum OverlayKind
{
    Image,
    Web,
}

/// <summary>One overlay. A web overlay's URL is not stored here: it holds account tokens, so it lives in the secret store.</summary>
public sealed record OverlayConfig
{
    public const int MaxOverlays = 3;

    public string LayerId { get; init; } = "overlay-1";

    public string Name { get; init; } = "Overlay";

    public OverlayKind Kind { get; init; }

    /// <summary>Picture file for <see cref="OverlayKind.Image"/>.</summary>
    public string? ImagePath { get; init; }

    /// <summary>Page size a web overlay is laid out at. 1920 × 1080 suits most alert services.</summary>
    public int PageWidth { get; init; } = 1920;

    public int PageHeight { get; init; } = 1080;

    public OverlayPosition Position { get; init; } = OverlayPosition.FullScreen;

    /// <summary>Width as a fraction of the stream's width (0.05–1). Ignored for full screen.</summary>
    public double Size { get; init; } = 0.25;

    /// <summary>Shown on the stream. A hidden overlay stays set up but is not drawn.</summary>
    public bool Visible { get; init; } = true;
}

/// <summary>The "Be right back" screen that covers the stream without stopping it.</summary>
public sealed record AwayScreenSettings
{
    public string Title { get; init; } = "Be right back";

    public string? Subtitle { get; init; } = "Stay tuned.";

    /// <summary>Optional picture instead of the built-in card.</summary>
    public string? ImagePath { get; init; }

    /// <summary>Also silence game and PC sound while away (the microphone is not affected).</summary>
    public bool MuteSystemAudio { get; init; } = true;
}

public sealed record OverlaySettings
{
    public IReadOnlyList<OverlayConfig> Items { get; init; } = [];

    public AwayScreenSettings Away { get; init; } = new();

    /// <summary>Clamps values, removes duplicates and keeps at most <see cref="OverlayConfig.MaxOverlays"/>.</summary>
    public OverlaySettings Sanitized()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<OverlayConfig>();
        foreach (var o in Items ?? [])
        {
            if (o is null || items.Count >= OverlayConfig.MaxOverlays)
            {
                continue;
            }

            var id = string.IsNullOrWhiteSpace(o.LayerId) ? $"overlay-{items.Count + 1}" : o.LayerId.Trim();
            if (!ids.Add(id))
            {
                continue;
            }

            items.Add(o with
            {
                LayerId = id,
                Name = string.IsNullOrWhiteSpace(o.Name) ? "Overlay" : o.Name.Trim()[..Math.Min(o.Name.Trim().Length, 40)],
                Kind = Enum.IsDefined(o.Kind) ? o.Kind : OverlayKind.Image,
                PageWidth = Math.Clamp(o.PageWidth, 100, 3840),
                PageHeight = Math.Clamp(o.PageHeight, 100, 3840),
                Position = Enum.IsDefined(o.Position) ? o.Position : OverlayPosition.FullScreen,
                Size = double.IsFinite(o.Size) ? Math.Clamp(o.Size, 0.05, 1) : 0.25,
            });
        }

        var away = Away ?? new AwayScreenSettings();
        return new OverlaySettings
        {
            Items = items,
            Away = away with
            {
                Title = string.IsNullOrWhiteSpace(away.Title) ? "Be right back" : away.Title.Trim()[..Math.Min(away.Title.Trim().Length, 60)],
                Subtitle = away.Subtitle is { } sub ? sub.Trim()[..Math.Min(sub.Trim().Length, 100)] : null,
            },
        };
    }
}

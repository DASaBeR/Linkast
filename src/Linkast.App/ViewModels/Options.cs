// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core;

namespace Linkast.App.ViewModels;

public enum VideoSourceKind
{
    Display,
    Window,
    CameraOnly,
    TestPattern,
}

public sealed record VideoSourceKindOption(VideoSourceKind Kind, string Label)
{
    public override string ToString() => Label;
}

public sealed record ResolutionOption(int Width, int Height)
{
    public override string ToString() => $"{Width} × {Height}" + (Height switch
    {
        1080 => "  (1080p)",
        720 => "  (720p)",
        1440 => "  (1440p)",
        900 => "  (900p)",
        480 => "  (480p)",
        _ => string.Empty,
    });
}

/// <summary>PRD 4.5: delivery models are distinct and explained, never presented as interchangeable.</summary>
public sealed record DestinationKindOption(DestinationKind Kind, string Label, string Explanation)
{
    public static IReadOnlyList<DestinationKindOption> All { get; } =
    [
        new(DestinationKind.Direct, "Direct to a platform",
            "Linkast sends your stream straight to one platform's ingest server, such as Twitch, YouTube or your own RTMP server."),
        new(DestinationKind.ThirdPartyRestream, "Through a restream service",
            "Linkast sends one stream to a third-party restream service, which forwards it to your platforms. Linkast does not run any relay servers; the service you choose does."),
    ];

    public override string ToString() => Label;
}

/// <summary>An appearance choice and how WPF's Fluent theme expresses it.</summary>
public sealed record ThemeOption(Linkast.Core.Settings.AppTheme Theme, string Label)
{
    public static IReadOnlyList<ThemeOption> All { get; } =
    [
        new(Linkast.Core.Settings.AppTheme.Dark, "Dark"),
        new(Linkast.Core.Settings.AppTheme.Light, "Light"),
        new(Linkast.Core.Settings.AppTheme.System, "Match Windows"),
    ];

    public static ThemeOption For(Linkast.Core.Settings.AppTheme theme) => All.FirstOrDefault(o => o.Theme == theme) ?? All[0];

#pragma warning disable WPF0001 // Fluent ThemeMode is marked experimental.
    public System.Windows.ThemeMode Mode => Theme switch
    {
        Linkast.Core.Settings.AppTheme.Light => System.Windows.ThemeMode.Light,
        Linkast.Core.Settings.AppTheme.System => System.Windows.ThemeMode.System,
        _ => System.Windows.ThemeMode.Dark,
    };
#pragma warning restore WPF0001

    public override string ToString() => Label;
}

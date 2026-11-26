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

/// <summary>An appearance choice. <see cref="Services.ThemeService"/> applies it.</summary>
public sealed record ThemeOption(Linkast.Core.Settings.AppTheme Theme, string Label)
{
    public static IReadOnlyList<ThemeOption> All { get; } =
    [
        new(Linkast.Core.Settings.AppTheme.Dark, "Dark"),
        new(Linkast.Core.Settings.AppTheme.Light, "Light"),
        new(Linkast.Core.Settings.AppTheme.System, "Match Windows"),
    ];

    public static ThemeOption For(Linkast.Core.Settings.AppTheme theme) => All.FirstOrDefault(o => o.Theme == theme) ?? All[0];

    public override string ToString() => Label;
}

/// <summary>A choice of microphone noise removal, with what it does and what it costs.</summary>
public sealed record NoiseRemovalOption(Linkast.Core.Audio.NoiseRemoval Level, string Label, string Explanation)
{
    // The CPU share was measured on the RNNoise port: about 2 to 3 % of one core of a 2022 desktop CPU.
    public static IReadOnlyList<NoiseRemovalOption> All { get; } =
    [
        new(Linkast.Core.Audio.NoiseRemoval.Off, "Off", "Your microphone as it is. Uses no extra CPU."),
        new(Linkast.Core.Audio.NoiseRemoval.Standard, "Remove noise",
            "A speech-trained neural network removes fans, hum, hiss, keyboards and street sound, also while you talk. Costs CPU: about 3 % of one core while live. Adds 10 ms delay to your voice."),
        new(Linkast.Core.Audio.NoiseRemoval.Strong, "Remove noise and breaths",
            "As \"Remove noise\", and what is left between your words (breaths, clicks, faint noise) is lowered further. Costs CPU: about 3 % of one core while live. Adds 10 ms delay to your voice."),
    ];

    public static NoiseRemovalOption For(Linkast.Core.Audio.NoiseRemoval level) => All.FirstOrDefault(o => o.Level == level) ?? All[0];

    public override string ToString() => Label;
}

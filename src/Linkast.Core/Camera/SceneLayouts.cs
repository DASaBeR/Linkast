// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core.Camera;

public enum OutputOrientation
{
    /// <summary>16:9, for Twitch, YouTube and most platforms.</summary>
    Landscape,

    /// <summary>9:16, for Shorts, TikTok and Reels.</summary>
    Vertical,
}

/// <summary>How the gameplay or screen capture fills its area.</summary>
public enum BaseFit
{
    /// <summary>Whole picture visible, with bars if the shapes differ.</summary>
    Fit,

    /// <summary>Area completely filled; edges are cropped.</summary>
    Fill,
}

/// <summary>
/// One arrangement of the stream: output orientation, where the gameplay goes, and where each camera
/// goes. Camera placements are stored per layout, so switching between landscape and vertical never
/// disturbs the other's arrangement. One layout drives one composition per stream.
/// </summary>
public sealed record SceneLayout
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = "Layout";

    public OutputOrientation Orientation { get; init; } = OutputOrientation.Landscape;

    /// <summary>Where the gameplay or screen capture goes, normalized to the canvas.</summary>
    public NormalizedRect BasePlacement { get; init; } = new(0, 0, 1, 1);

    public BaseFit BaseFit { get; init; } = BaseFit.Fit;

    /// <summary>
    /// The part of the captured picture that is shown, as fractions of the capture (the whole picture by
    /// default). Trimmed from the edges in the editor with Alt-drag, like cropping a source in OBS.
    /// </summary>
    public NormalizedRect BaseCrop { get; init; } = new(0, 0, 1, 1);

    /// <summary>Placement per camera layer ID. Cameras without an entry are hidden in this layout.</summary>
    public IReadOnlyDictionary<string, NormalizedRect> CameraPlacements { get; init; } = new Dictionary<string, NormalizedRect>();

    public override string ToString() => Name;

    /// <summary>Canvas size for this layout from the configured landscape resolution (vertical swaps it).</summary>
    public (int Width, int Height) CanvasSize(int landscapeWidth, int landscapeHeight) =>
        Orientation == OutputOrientation.Vertical ? (landscapeHeight, landscapeWidth) : (landscapeWidth, landscapeHeight);

    public SceneLayout WithPlacement(string layerId, NormalizedRect placement)
    {
        var map = new Dictionary<string, NormalizedRect>(CameraPlacements) { [layerId] = placement.Clamped() };
        return this with { CameraPlacements = map };
    }

    public SceneLayout Without(string layerId)
    {
        var map = new Dictionary<string, NormalizedRect>(CameraPlacements);
        map.Remove(layerId);
        return this with { CameraPlacements = map };
    }

    /// <summary>True when switching from <paramref name="other"/> to this layout can be done live (only camera moves).</summary>
    public bool CanSwitchLiveFrom(SceneLayout other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Orientation == other.Orientation && BasePlacement == other.BasePlacement && BaseFit == other.BaseFit && BaseCrop == other.BaseCrop;
    }

    /// <summary>Smallest part of the capture that can be kept on each axis.</summary>
    public const double MinCrop = 0.05;

    public SceneLayout Clamped() => this with
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "Layout" : Name.Trim(),
        Orientation = Enum.IsDefined(Orientation) ? Orientation : OutputOrientation.Landscape,
        BaseFit = Enum.IsDefined(BaseFit) ? BaseFit : BaseFit.Fit,
        BasePlacement = BasePlacement.Width <= 0 || BasePlacement.Height <= 0 ? new NormalizedRect(0, 0, 1, 1) : BasePlacement.Clamped(),
        BaseCrop = BaseCrop.Width <= 0 || BaseCrop.Height <= 0 ? new NormalizedRect(0, 0, 1, 1) : BaseCrop.Clamped(MinCrop),
        CameraPlacements = (CameraPlacements ?? new Dictionary<string, NormalizedRect>())
            .ToDictionary(kv => kv.Key, kv => kv.Value.Clamped()),
    };

    public static readonly Guid LandscapeId = new("6f1c1a40-0b7e-4d61-9b70-1c0f3a9e0101");
    public static readonly Guid VerticalId = new("6f1c1a40-0b7e-4d61-9b70-1c0f3a9e0102");

    /// <summary>Default landscape layout: gameplay fills the screen, first camera bottom-right.</summary>
    public static SceneLayout DefaultLandscape(string? firstLayerId, NormalizedRect? placement = null)
    {
        var map = new Dictionary<string, NormalizedRect>();
        if (firstLayerId is not null)
        {
            map[firstLayerId] = placement ?? new NormalizedRect(0.695, 0.68, 0.28, 0.28);
        }

        return new SceneLayout { Id = LandscapeId, Name = "Landscape (16:9)", CameraPlacements = map };
    }

    /// <summary>Default vertical layout: gameplay fills the top 56 %, first camera underneath.</summary>
    public static SceneLayout DefaultVertical(string? firstLayerId)
    {
        var map = new Dictionary<string, NormalizedRect>();
        if (firstLayerId is not null)
        {
            map[firstLayerId] = new NormalizedRect(0, 0.56, 1, 0.44);
        }

        return new SceneLayout
        {
            Id = VerticalId,
            Name = "Vertical (9:16): game on top, camera below",
            Orientation = OutputOrientation.Vertical,
            BasePlacement = new NormalizedRect(0, 0, 1, 0.56),
            BaseFit = BaseFit.Fill,
            CameraPlacements = map,
        };
    }
}

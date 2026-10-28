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

/// <summary>What a scene is for: picks its icon, and what a new scene of that kind starts with.</summary>
public enum SceneKind
{
    Custom,
    StartingSoon,
    Gaming,
    JustChatting,
    BeRightBack,
    Ending,
}

public enum SceneMediaKind
{
    /// <summary>The Linkast card (navy background, logo) with the scene's title and message.</summary>
    Card,

    /// <summary>A picture file, covering the whole stream.</summary>
    Picture,

    /// <summary>A video file that plays on a loop, covering the whole stream; its sound has its own fader.</summary>
    Video,
}

/// <summary>What a scene captures.</summary>
public enum SceneCaptureKind
{
    /// <summary>No game or screen: cameras, overlays and the background only.</summary>
    None,

    Display,

    Window,

    TestPattern,
}

/// <summary>
/// A scene's own game, window or display. Windows are remembered by title and program, because a window
/// handle changes every time the game starts.
/// </summary>
public sealed record SceneCapture
{
    public SceneCaptureKind Kind { get; init; } = SceneCaptureKind.None;

    public string? MonitorDeviceName { get; init; }

    public string? WindowTitle { get; init; }

    public string? WindowProcess { get; init; }

    public bool DesktopDuplication { get; init; }

    public bool CaptureCursor { get; init; } = true;

    public static SceneCapture Nothing { get; } = new();

    public SceneCapture Clamped() => this with { Kind = Enum.IsDefined(Kind) ? Kind : SceneCaptureKind.None };
}

/// <summary>How one overlay is set up in one scene: where it sits, how big it is and what is trimmed.</summary>
public sealed record SceneOverlay
{
    public OverlayPosition Position { get; init; } = OverlayPosition.FullScreen;

    /// <summary>Width as a fraction of the stream's width, for the nine spots.</summary>
    public double Size { get; init; } = 0.25;

    /// <summary>Where it was dragged to, normalized to the canvas, when <see cref="Position"/> is Custom.</summary>
    public NormalizedRect? CustomPlacement { get; init; }

    public NormalizedRect Crop { get; init; } = NormalizedRect.Full;

    public SceneOverlay Clamped()
    {
        var custom = CustomPlacement is { Width: > 0, Height: > 0 } c ? c.Clamped() : (NormalizedRect?)null;
        var position = Enum.IsDefined(Position) ? Position : OverlayPosition.FullScreen;
        return this with
        {
            Position = position == OverlayPosition.Custom && custom is null ? OverlayPosition.FullScreen : position,
            Size = double.IsFinite(Size) ? Math.Clamp(Size, 0.05, 1) : 0.25,
            CustomPlacement = custom,
            Crop = Crop is { Width: > 0, Height: > 0 } crop ? crop.Clamped(SceneLayout.MinCrop) : NormalizedRect.Full,
        };
    }
}

/// <summary>A scene's background: drawn over the whole canvas, above the game and below cameras and overlays.</summary>
public sealed record SceneMedia
{
    public SceneMediaKind Kind { get; init; } = SceneMediaKind.Card;

    /// <summary>The picture or video for <see cref="SceneMediaKind.Picture"/> and <see cref="SceneMediaKind.Video"/>.</summary>
    public string? Path { get; init; }

    /// <summary>The card's big line. Empty shows only the background.</summary>
    public string Title { get; init; } = string.Empty;

    public string? Subtitle { get; init; }

    public SceneMedia Clamped() => this with
    {
        Kind = Enum.IsDefined(Kind) ? Kind : SceneMediaKind.Card,
        Path = string.IsNullOrWhiteSpace(Path) ? null : Path.Trim(),
        Title = (Title ?? string.Empty).Trim()[..Math.Min((Title ?? string.Empty).Trim().Length, 60)],
        Subtitle = Subtitle is { } s ? s.Trim()[..Math.Min(s.Trim().Length, 100)] : null,
    };
}

/// <summary>
/// One scene (formerly a "layout"): output orientation, whether and where the game shows, where each
/// camera goes, a background, which overlays are hidden and which sounds are muted. Every source is set
/// up once and every scene only arranges them, so all of them are in the running stream and switching
/// between scenes of the same orientation is instant. Camera placements are stored per scene, so a
/// vertical scene never disturbs a landscape one.
/// </summary>
public sealed record SceneLayout
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = "Layout";

    public SceneKind Kind { get; init; } = SceneKind.Custom;

    public OutputOrientation Orientation { get; init; } = OutputOrientation.Landscape;

    /// <summary>False hides the game or screen capture in this scene (it keeps running, ready to come back).</summary>
    public bool ShowCapture { get; init; } = true;

    /// <summary>A picture, video or card over the whole canvas, or null for none.</summary>
    public SceneMedia? Media { get; init; }

    /// <summary>Overlay layer IDs that are not shown in this scene. Settings before scene overlays only (see <see cref="Overlays"/>).</summary>
    public IReadOnlyList<string> HiddenOverlays { get; init; } = [];

    /// <summary>
    /// The overlays this scene shows, each with its own place, size and trim, by overlay layer ID. The
    /// overlay itself (its picture or page) is set up once and can be in several scenes.
    /// </summary>
    public IReadOnlyDictionary<string, SceneOverlay> Overlays { get; init; } = new Dictionary<string, SceneOverlay>();

    /// <summary>The game, window or display this scene shows. Null only in settings from before scenes had their own.</summary>
    public SceneCapture? Capture { get; init; }

    /// <summary>Mixer channels (see <see cref="AudioChannels"/>) that are silent while this scene is on.</summary>
    public IReadOnlyList<string> MutedChannels { get; init; } = [];

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

    /// <summary>
    /// True when switching from <paramref name="other"/> to this scene can be done live. Everything a
    /// scene arranges is in the running stream, so only a change of orientation needs a restart.
    /// </summary>
    public bool CanSwitchLiveFrom(SceneLayout other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Orientation == other.Orientation;
    }

    /// <summary>True when <paramref name="channel"/> is silent while this scene is on.</summary>
    public bool Mutes(string channel) => MutedChannels.Contains(channel, StringComparer.Ordinal);

    public bool Shows(string overlayId) => Overlays.ContainsKey(overlayId);

    public SceneLayout WithOverlay(string overlayId, SceneOverlay setup)
    {
        var map = new Dictionary<string, SceneOverlay>(Overlays) { [overlayId] = setup.Clamped() };
        return this with { Overlays = map };
    }

    public SceneLayout WithoutOverlay(string overlayId)
    {
        var map = new Dictionary<string, SceneOverlay>(Overlays);
        map.Remove(overlayId);
        return this with { Overlays = map };
    }

    /// <summary>Smallest part of the capture that can be kept on each axis.</summary>
    public const double MinCrop = 0.05;

    public SceneLayout Clamped() => this with
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "Scene" : Name.Trim()[..Math.Min(Name.Trim().Length, 40)],
        Kind = Enum.IsDefined(Kind) ? Kind : SceneKind.Custom,
        Orientation = Enum.IsDefined(Orientation) ? Orientation : OutputOrientation.Landscape,
        BaseFit = Enum.IsDefined(BaseFit) ? BaseFit : BaseFit.Fit,
        BasePlacement = BasePlacement.Width <= 0 || BasePlacement.Height <= 0 ? new NormalizedRect(0, 0, 1, 1) : BasePlacement.Clamped(),
        BaseCrop = BaseCrop.Width <= 0 || BaseCrop.Height <= 0 ? new NormalizedRect(0, 0, 1, 1) : BaseCrop.Clamped(MinCrop),
        CameraPlacements = (CameraPlacements ?? new Dictionary<string, NormalizedRect>())
            .ToDictionary(kv => kv.Key, kv => kv.Value.Clamped()),
        Media = Media?.Clamped(),
        HiddenOverlays = [.. (HiddenOverlays ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal)],
        Overlays = (Overlays ?? new Dictionary<string, SceneOverlay>())
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Clamped(), StringComparer.Ordinal),
        Capture = Capture?.Clamped(),
        MutedChannels = [.. (MutedChannels ?? []).Where(IsChannelName).Distinct(StringComparer.Ordinal)],
    };

    private static bool IsChannelName(string? name) =>
        name is { Length: > 0 and <= 40 } && name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');

    public static readonly Guid LandscapeId = new("6f1c1a40-0b7e-4d61-9b70-1c0f3a9e0101");
    public static readonly Guid VerticalId = new("6f1c1a40-0b7e-4d61-9b70-1c0f3a9e0102");
    public static readonly Guid StartingSoonId = new("6f1c1a40-0b7e-4d61-9b70-1c0f3a9e0103");
    public static readonly Guid JustChattingId = new("6f1c1a40-0b7e-4d61-9b70-1c0f3a9e0104");
    public static readonly Guid BeRightBackId = new("6f1c1a40-0b7e-4d61-9b70-1c0f3a9e0105");
    public static readonly Guid EndingId = new("6f1c1a40-0b7e-4d61-9b70-1c0f3a9e0106");

    /// <summary>The Gaming scene: gameplay fills the screen, first camera bottom-right.</summary>
    public static SceneLayout DefaultLandscape(string? firstLayerId, NormalizedRect? placement = null)
    {
        var map = new Dictionary<string, NormalizedRect>();
        if (firstLayerId is not null)
        {
            map[firstLayerId] = placement ?? new NormalizedRect(0.695, 0.68, 0.28, 0.28);
        }

        return new SceneLayout { Id = LandscapeId, Name = "Gaming", Kind = SceneKind.Gaming, CameraPlacements = map };
    }

    /// <summary>The vertical Gaming scene: gameplay fills the top 56 %, first camera underneath.</summary>
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
            Name = "Gaming (vertical)",
            Kind = SceneKind.Gaming,
            Orientation = OutputOrientation.Vertical,
            BasePlacement = new NormalizedRect(0, 0, 1, 0.56),
            BaseFit = BaseFit.Fill,
            CameraPlacements = map,
        };
    }

    /// <summary>
    /// A new scene of <paramref name="kind"/>, ready to use: Starting soon, Be right back and Ending show
    /// the Linkast card with fitting words and keep the game hidden and silent; Just chatting shows the
    /// first camera large on the card's background.
    /// </summary>
    public static SceneLayout Create(SceneKind kind, string? firstLayerId, OutputOrientation orientation = OutputOrientation.Landscape, Guid? id = null)
    {
        var vertical = orientation == OutputOrientation.Vertical;
        var scene = kind switch
        {
            SceneKind.Gaming => vertical ? DefaultVertical(firstLayerId) : DefaultLandscape(firstLayerId),
            SceneKind.StartingSoon => Covering("Starting soon", "Starting soon", "The stream begins in a moment."),
            SceneKind.BeRightBack => Covering("Be right back", "Be right back", "Stay tuned."),
            SceneKind.Ending => Covering("Ending", "Thanks for watching", "See you next time."),
            SceneKind.JustChatting => new SceneLayout
            {
                Name = "Just chatting",
                ShowCapture = false,
                Media = new SceneMedia { Kind = SceneMediaKind.Card },
                MutedChannels = [AudioChannels.Game],
                CameraPlacements = firstLayerId is null
                    ? new Dictionary<string, NormalizedRect>()
                    : new Dictionary<string, NormalizedRect> { [firstLayerId] = vertical ? new NormalizedRect(0.05, 0.25, 0.9, 0.5) : new NormalizedRect(0.2, 0.1, 0.6, 0.8) },
            },
            _ => new SceneLayout { Name = "New scene" },
        };
        return scene with { Id = id ?? Guid.NewGuid(), Kind = kind, Orientation = orientation };

        static SceneLayout Covering(string name, string title, string subtitle) => new()
        {
            Name = name,
            ShowCapture = false,
            Media = new SceneMedia { Kind = SceneMediaKind.Card, Title = title, Subtitle = subtitle },
            MutedChannels = [AudioChannels.Game],
        };
    }

    /// <summary>The scenes a new setup starts with, in the order shown in the scene strip.</summary>
    public static IReadOnlyList<SceneLayout> DefaultScenes(string? firstLayerId) =>
    [
        Create(SceneKind.StartingSoon, firstLayerId, id: StartingSoonId),
        DefaultLandscape(firstLayerId),
        Create(SceneKind.JustChatting, firstLayerId, id: JustChattingId),
        Create(SceneKind.BeRightBack, firstLayerId, id: BeRightBackId),
        Create(SceneKind.Ending, firstLayerId, id: EndingId),
        DefaultVertical(firstLayerId),
    ];
}

// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core.Camera;

namespace Linkast.Core.Settings;

/// <summary>
/// Everything Linkast remembers between runs. Serialized as versioned JSON. Never contains secrets,
/// device handles or other machine-specific runtime objects (device IDs are stable strings).
/// </summary>
public sealed record LinkastSettings
{
    /// <summary>
    /// 3: layouts are scenes (game shown or not, a background, hidden overlays, muted sounds), the Be
    /// right back screen is a scene, and the mixer has a fader per source. 2: multiple camera layers and
    /// layouts (Phase 4). 1: single webcam (Phase 1).
    /// </summary>
    public const int CurrentSchemaVersion = 4;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Light or dark appearance. Dark is the default, including for files saved before this existed.</summary>
    public AppTheme Theme { get; init; } = AppTheme.Dark;

    public StreamSettings Stream { get; init; } = new();

    public WebcamSettings Webcam { get; init; } = new();

    /// <summary>Pictures, web alerts and the "Be right back" screen.</summary>
    public OverlaySettings Overlays { get; init; } = new();

    /// <summary>Global hotkeys (Go live, mute, cameras, Be right back, record).</summary>
    public HotkeySettings Hotkeys { get; init; } = new();

    /// <summary>User-created presets. Built-in presets are not stored; see <see cref="CameraPresets"/>.</summary>
    public IReadOnlyList<CameraPreset> CustomPresets { get; init; } = [];
}

public enum AppTheme
{
    Dark,
    Light,

    /// <summary>Follows Windows' app mode (Settings › Personalization › Colors).</summary>
    System,
}

/// <summary>Stream setup (everything except the stream key, which lives in the secret store).</summary>
public sealed record StreamSettings
{
    /// <summary>"Display", "Window", "CameraOnly" or "TestPattern".</summary>
    public string SourceKind { get; init; } = "Display";

    /// <summary>Monitor device name, for example \\.\DISPLAY1. Matched on load; falls back to the primary display.</summary>
    public string? MonitorDeviceName { get; init; }

    /// <summary>Window title and process, matched on load because window handles change between runs.</summary>
    public string? WindowTitle { get; init; }

    public string? WindowProcess { get; init; }

    public bool UseDesktopDuplication { get; init; }

    public bool CaptureCursor { get; init; } = true;

    public bool SystemAudioEnabled { get; init; } = true;

    public string? SystemAudioDeviceId { get; init; }

    public double SystemAudioVolume { get; init; } = 100;

    public bool MicrophoneEnabled { get; init; }

    public string? MicrophoneDeviceId { get; init; }

    public double MicrophoneVolume { get; init; } = 100;

    /// <summary>Remove steady background noise from the microphone.</summary>
    public bool MicrophoneNoiseSuppression { get; init; }

    public string? EncoderId { get; init; }

    public int Width { get; init; } = 1920;

    public int Height { get; init; } = 1080;

    public int FrameRate { get; init; } = 60;

    public int VideoBitrateKbps { get; init; } = 6000;

    public int KeyframeIntervalSeconds { get; init; } = 2;

    public int AudioBitrateKbps { get; init; } = 160;

    public string? EncoderPreset { get; init; }

    /// <summary>Preset that fills <see cref="ServerUrl"/>. Custom means the user typed the server.</summary>
    public StreamPlatform Platform { get; init; } = StreamPlatform.Custom;

    public DestinationKind DestinationKind { get; init; } = DestinationKind.Direct;

    /// <summary>Folder for recordings, or null for the Linkast folder in Videos.</summary>
    public string? RecordingFolder { get; init; }

    /// <summary>Every stream is also saved to a file.</summary>
    public bool RecordWhenLive { get; init; }

    /// <summary>Recordings are turned into MP4 files when they finish.</summary>
    public bool RemuxRecordingsToMp4 { get; init; }

    public string ServerUrl { get; init; } = string.Empty;

    /// <summary>Network adapter ID for the stream, or null for the Windows default route.</summary>
    public string? NetworkInterfaceId { get; init; }

    /// <summary>Adapter name at the time it was chosen, shown when it is not connected.</summary>
    public string? NetworkInterfaceName { get; init; }

    /// <summary>The scene Go live starts on (for example Starting soon), or null to stay on the current scene.</summary>
    public Guid? StartSceneId { get; init; }

    /// <summary>The scene End stream shows first (for example Ending), or null to end at once.</summary>
    public Guid? EndSceneId { get; init; }

    /// <summary>How long the end scene is on before the stream ends by itself.</summary>
    public int EndSceneSeconds { get; init; } = 10;

    /// <summary>The captured game's own sound gets its own fader (it is also part of desktop audio).</summary>
    public bool SeparateGameAudio { get; init; }

    /// <summary>Fader positions (0–200 %) of the channels other than desktop audio and the microphone, by channel.</summary>
    public IReadOnlyDictionary<string, double> ChannelVolumes { get; init; } = NoVolumes;

    /// <summary>Shared so that default settings compare equal (records compare collections by reference).</summary>
    internal static readonly IReadOnlyDictionary<string, double> NoVolumes = new Dictionary<string, double>();

    /// <summary>Channels muted in the mixer (in every scene).</summary>
    public IReadOnlyList<string> MutedChannels { get; init; } = [];
}

/// <summary>One camera layer: which camera, and how it looks (camera-level). Placement lives in layouts.</summary>
public sealed record CameraLayerConfig
{
    public const int MaxLayers = 4;

    /// <summary>Stable ID, used as the key for placements in every layout.</summary>
    public string LayerId { get; init; } = "camera-1";

    /// <summary>User-visible name, for example "Face cam" or "Desk cam".</summary>
    public string Name { get; init; } = "Camera 1";

    /// <summary>Camera on/off. Off releases the device.</summary>
    public bool Enabled { get; init; }

    public string? DeviceId { get; init; }

    public string? DeviceName { get; init; }

    public CameraFormatRequest Format { get; init; } = CameraFormatRequest.Auto;

    public CameraLayerSettings Look { get; init; } = CameraLayerSettings.Default;

    public Guid? PresetId { get; init; }

    /// <summary>The part of the rendered camera that is shown (fractions of it), in every scene.</summary>
    public NormalizedRect Crop { get; init; } = NormalizedRect.Full;
}

/// <summary>What the Linkast Camera shows. The camera itself is installed separately (one admin prompt).</summary>
public sealed record VirtualCameraSettings
{
    /// <summary>Send pictures to the Linkast Camera while an app uses it.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Camera layer to show; null = the first layer.</summary>
    public string? LayerId { get; init; }
}

/// <summary>Webcam Studio state (schema 2): camera layers plus layouts.</summary>
public sealed record WebcamSettings
{
    public IReadOnlyList<CameraLayerConfig> Layers { get; init; } = [new CameraLayerConfig()];

    /// <summary>The scenes (stored as layouts), in the order of the scene strip.</summary>
    public IReadOnlyList<SceneLayout> Layouts { get; init; } = SceneLayout.DefaultScenes("camera-1");

    public Guid? ActiveLayoutId { get; init; } = SceneLayout.LandscapeId;

    public string? SelectedLayerId { get; init; } = "camera-1";

    /// <summary>Remembered look per camera device ID, restored when a layer switches back to that camera.</summary>
    public IReadOnlyDictionary<string, CameraLayerSettings> LookByDevice { get; init; } = new Dictionary<string, CameraLayerSettings>();

    /// <summary>Remembered hardware control values per device ID. A null value means automatic.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<HardwareControl, double?>> HardwareByDevice { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<HardwareControl, double?>>();

    public bool SnapToGrid { get; init; } = true;

    /// <summary>Linkast Camera (virtual camera for Zoom, Teams, …).</summary>
    public VirtualCameraSettings VirtualCamera { get; init; } = new();

    public SceneLayout ActiveLayout => Layouts.FirstOrDefault(l => l.Id == ActiveLayoutId) ?? Layouts[0];
}

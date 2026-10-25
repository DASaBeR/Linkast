// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core.Camera;

namespace Linkast.Core.Settings;

/// <summary>
/// Everything Linkast remembers between runs. Serialized as versioned JSON. Never contains secrets,
/// device handles or other machine-specific runtime objects (device IDs are stable strings).
/// </summary>
public sealed record LinkastSettings
{
    /// <summary>2: multiple camera layers and layouts (Phase 4). 1: single webcam (Phase 1).</summary>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Light or dark appearance. Dark is the default, including for files saved before this existed.</summary>
    public AppTheme Theme { get; init; } = AppTheme.Dark;

    public StreamSettings Stream { get; init; } = new();

    public WebcamSettings Webcam { get; init; } = new();

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

    public string? EncoderId { get; init; }

    public int Width { get; init; } = 1920;

    public int Height { get; init; } = 1080;

    public int FrameRate { get; init; } = 60;

    public int VideoBitrateKbps { get; init; } = 6000;

    public int KeyframeIntervalSeconds { get; init; } = 2;

    public int AudioBitrateKbps { get; init; } = 160;

    public string? EncoderPreset { get; init; }

    public DestinationKind DestinationKind { get; init; } = DestinationKind.Direct;

    public string ServerUrl { get; init; } = string.Empty;

    /// <summary>Network adapter ID for the stream, or null for the Windows default route.</summary>
    public string? NetworkInterfaceId { get; init; }

    /// <summary>Adapter name at the time it was chosen, shown when it is not connected.</summary>
    public string? NetworkInterfaceName { get; init; }
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

    public IReadOnlyList<SceneLayout> Layouts { get; init; } = [SceneLayout.DefaultLandscape("camera-1"), SceneLayout.DefaultVertical("camera-1")];

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

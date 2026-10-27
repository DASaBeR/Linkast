// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Linkast.Core.Camera;

namespace Linkast.Core.Settings;

/// <summary>Result of reading a settings file.</summary>
public enum SettingsLoadOutcome
{
    Loaded,
    Migrated,
    Defaults,

    /// <summary>The file could not be read; defaults were used and the bad file should be kept aside.</summary>
    Corrupt,

    /// <summary>Written by a newer Linkast. Known fields were loaded; saving would drop unknown ones.</summary>
    NewerVersion,
}

/// <summary>
/// Versioned JSON for <see cref="LinkastSettings"/>. Enum values are written as names so files stay
/// readable and stable when enums gain members; unknown properties are ignored; every loaded
/// value is clamped so a hand-edited or damaged file cannot produce invalid settings.
/// </summary>
public static class SettingsSerializer
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static string Serialize(LinkastSettings settings) =>
        JsonSerializer.Serialize(settings with { SchemaVersion = LinkastSettings.CurrentSchemaVersion }, Options);

    public static (LinkastSettings Settings, SettingsLoadOutcome Outcome) Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (new LinkastSettings(), SettingsLoadOutcome.Defaults);
        }

        try
        {
            var node = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("Root is not an object.");
            var version = node["schemaVersion"]?.GetValue<int>() ?? 0;
            var migrated = Migrate(node, version);
            var settings = node.Deserialize<LinkastSettings>(Options) ?? new LinkastSettings();
            if (version < 3)
            {
                settings = MigrateToScenes(settings);
                migrated = true;
            }
            var outcome = version > LinkastSettings.CurrentSchemaVersion ? SettingsLoadOutcome.NewerVersion
                : migrated ? SettingsLoadOutcome.Migrated
                : SettingsLoadOutcome.Loaded;
            return (Sanitize(settings), outcome);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or NotSupportedException)
        {
            return (new LinkastSettings(), SettingsLoadOutcome.Corrupt);
        }
    }

    /// <summary>Upgrades older documents in place. Returns true if anything changed.</summary>
    internal static bool Migrate(JsonObject node, int fromVersion)
    {
        var changed = false;
        if (fromVersion < 1)
        {
            // Version 0 = pre-release files without a version field. Same shape as version 1.
            node["schemaVersion"] = 1;
            changed = true;
        }

        if (fromVersion < 2)
        {
            MigrateWebcamToLayers(node);
            node["schemaVersion"] = 2;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Version 1 had one webcam with its placement inline. Version 2 has camera layers and layouts.
    /// The old webcam becomes "Camera 1", its placement goes into the landscape layout, and a
    /// default vertical layout is added.
    /// </summary>
    private static void MigrateWebcamToLayers(JsonObject root)
    {
        if (root["webcam"] is not JsonObject webcam || webcam.ContainsKey("layers"))
        {
            return;
        }

        const string layerId = "camera-1";
        var layer = new JsonObject
        {
            ["layerId"] = layerId,
            ["name"] = "Camera 1",
            ["enabled"] = webcam["enabled"]?.DeepClone() ?? false,
            ["deviceId"] = webcam["deviceId"]?.DeepClone(),
            ["deviceName"] = webcam["deviceName"]?.DeepClone(),
            ["format"] = webcam["format"]?.DeepClone(),
            ["look"] = webcam["look"]?.DeepClone(),
            ["presetId"] = webcam["presetId"]?.DeepClone(),
        };
        foreach (var key in new[] { "format", "look", "deviceId", "deviceName", "presetId" })
        {
            if (layer[key] is null)
            {
                layer.Remove(key);
            }
        }

        var landscape = JsonSerializer.SerializeToNode(
            SceneLayout.DefaultLandscape(layerId, webcam["placement"]?.Deserialize<NormalizedRect>(Options)), Options)!;
        var vertical = JsonSerializer.SerializeToNode(SceneLayout.DefaultVertical(layerId), Options)!;

        foreach (var old in new[] { "enabled", "deviceId", "deviceName", "format", "look", "placement", "presetId" })
        {
            webcam.Remove(old);
        }

        webcam["layers"] = new JsonArray(layer);
        webcam["layouts"] = new JsonArray(landscape, vertical);
        webcam["activeLayoutId"] = SceneLayout.LandscapeId.ToString();
        webcam["selectedLayerId"] = layerId;
    }

    /// <summary>
    /// Version 2 had layouts, an away screen and a per-overlay "show" switch. Version 3 has scenes: the
    /// built-in layouts become the Gaming scenes, the away screen becomes the Be right back scene (with
    /// its picture and its "mute PC sound" choice), Starting soon, Just chatting and Ending are added,
    /// and an overlay that was switched off is hidden in every scene.
    /// </summary>
    internal static LinkastSettings MigrateToScenes(LinkastSettings s)
    {
        var webcam = s.Webcam ?? new WebcamSettings();
        var overlays = s.Overlays ?? new OverlaySettings();
        var firstLayer = webcam.Layers is { Count: > 0 } layers ? layers[0]?.LayerId : null;
        var hidden = (overlays.Items ?? []).Where(o => o is { Visible: false }).Select(o => o.LayerId).ToList();

        var scenes = (webcam.Layouts ?? []).Where(l => l is not null).Select(l => l.Id == SceneLayout.LandscapeId || l.Id == SceneLayout.VerticalId
            ? l with
            {
                Kind = SceneKind.Gaming,
                Name = l.Name is "Landscape (16:9)" ? "Gaming" : l.Name is "Vertical (9:16): game on top, camera below" ? "Gaming (vertical)" : l.Name,
            }
            : l).ToList();

        var away = overlays.Away ?? new AwayScreenSettings();
        var brb = SceneLayout.Create(SceneKind.BeRightBack, firstLayer, id: SceneLayout.BeRightBackId) with
        {
            Media = away.ImagePath is { Length: > 0 } picture
                ? new SceneMedia { Kind = SceneMediaKind.Picture, Path = picture, Title = away.Title, Subtitle = away.Subtitle }
                : new SceneMedia { Kind = SceneMediaKind.Card, Title = away.Title, Subtitle = away.Subtitle },
            MutedChannels = away.MuteSystemAudio ? [AudioChannels.System, AudioChannels.Game] : [AudioChannels.Game],
        };

        // New scenes go around the existing ones: Starting soon first, then the rest after the Gaming scenes.
        var gaming = scenes.FindIndex(l => l.Id == SceneLayout.LandscapeId);
        var after = gaming < 0 ? scenes.Count : gaming + 1;
        foreach (var added in new[]
        {
            SceneLayout.Create(SceneKind.Ending, firstLayer, id: SceneLayout.EndingId),
            brb,
            SceneLayout.Create(SceneKind.JustChatting, firstLayer, id: SceneLayout.JustChattingId),
        })
        {
            if (scenes.All(l => l.Id != added.Id))
            {
                scenes.Insert(after, added);
            }
        }

        if (scenes.All(l => l.Id != SceneLayout.StartingSoonId))
        {
            scenes.Insert(0, SceneLayout.Create(SceneKind.StartingSoon, firstLayer, id: SceneLayout.StartingSoonId));
        }

        return s with
        {
            Webcam = webcam with { Layouts = [.. scenes.Select(l => l with { HiddenOverlays = [.. l.HiddenOverlays.Union(hidden)] })] },
            Overlays = overlays with { Items = [.. (overlays.Items ?? []).Where(o => o is not null).Select(o => o with { Visible = true })] },
            Hotkeys = (s.Hotkeys ?? new HotkeySettings()).WithNewDefaults(), // the scene hotkeys
        };
    }

    /// <summary>Clamps every value into its valid range and drops broken entries.</summary>
    public static LinkastSettings Sanitize(LinkastSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var stream = s.Stream ?? new StreamSettings();
        var webcam = s.Webcam ?? new WebcamSettings();
        return s with
        {
            Theme = Enum.IsDefined(s.Theme) ? s.Theme : AppTheme.Dark,
            Stream = stream with
            {
                SystemAudioVolume = Math.Clamp(Finite(stream.SystemAudioVolume, 100), 0, 200),
                MicrophoneVolume = Math.Clamp(Finite(stream.MicrophoneVolume, 100), 0, 200),
                FrameRate = stream.FrameRate is 30 or 60 ? stream.FrameRate : 60,
                VideoBitrateKbps = Math.Clamp(stream.VideoBitrateKbps, 200, 100_000),
                KeyframeIntervalSeconds = Math.Clamp(stream.KeyframeIntervalSeconds, 1, 10),
                AudioBitrateKbps = Math.Clamp(stream.AudioBitrateKbps, 32, 512),
                ServerUrl = stream.ServerUrl ?? string.Empty,
                EndSceneSeconds = Math.Clamp(stream.EndSceneSeconds, 3, 120),
                // Empty collections stay the shared defaults, so untouched settings still compare equal.
                ChannelVolumes = stream.ChannelVolumes is not { Count: > 0 } volumes ? StreamSettings.NoVolumes : volumes
                    .Where(kv => IsChannel(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => Math.Clamp(Finite(kv.Value, 100), 0, 200), StringComparer.Ordinal),
                MutedChannels = stream.MutedChannels is not { Count: > 0 } muted ? [] : [.. muted.Where(IsChannel).Distinct(StringComparer.Ordinal)],
            },
            Webcam = SanitizeWebcam(webcam),
            Overlays = (s.Overlays ?? new OverlaySettings()).Sanitized(),
            Hotkeys = (s.Hotkeys ?? new HotkeySettings()).Sanitized(),
            CustomPresets = [.. (s.CustomPresets ?? []).Where(p => p is not null && !p.IsBuiltIn).Select(p => p with
            {
                Name = string.IsNullOrWhiteSpace(p.Name) ? "Custom" : p.Name.Trim(),
                Look = (p.Look ?? Camera.CameraLayerSettings.Default).Clamped(),
                Placement = p.Placement?.Clamped(),
            })],
        };
    }

    private static WebcamSettings SanitizeWebcam(WebcamSettings webcam)
    {
        // Layers: at most 4, unique IDs, valid looks. Always at least one.
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var layers = new List<CameraLayerConfig>();
        foreach (var l in webcam.Layers ?? [])
        {
            if (l is null || layers.Count >= CameraLayerConfig.MaxLayers)
            {
                continue;
            }

            var id = string.IsNullOrWhiteSpace(l.LayerId) ? $"camera-{layers.Count + 1}" : l.LayerId.Trim();
            if (!ids.Add(id))
            {
                continue;
            }

            layers.Add(l with
            {
                LayerId = id,
                Name = string.IsNullOrWhiteSpace(l.Name) ? $"Camera {layers.Count + 1}" : l.Name.Trim(),
                Format = l.Format ?? Camera.CameraFormatRequest.Auto,
                Look = (l.Look ?? Camera.CameraLayerSettings.Default).Clamped(),
            });
        }

        if (layers.Count == 0)
        {
            layers.Add(new CameraLayerConfig());
            ids.Add(layers[0].LayerId);
        }

        // Layouts: valid, placements only for existing layers. Always at least landscape.
        var layouts = (webcam.Layouts ?? [])
            .Where(l => l is not null)
            .Select(l => l.Clamped())
            .Select(l => l with { CameraPlacements = l.CameraPlacements.Where(kv => ids.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value) })
            .GroupBy(l => l.Id)
            .Select(g => g.First())
            .ToList();
        if (layouts.Count == 0)
        {
            layouts.AddRange(SceneLayout.DefaultScenes(layers[0].LayerId));
        }

        return webcam with
        {
            Layers = layers,
            Layouts = layouts,
            ActiveLayoutId = layouts.Any(l => l.Id == webcam.ActiveLayoutId) ? webcam.ActiveLayoutId : layouts[0].Id,
            SelectedLayerId = ids.Contains(webcam.SelectedLayerId ?? string.Empty) ? webcam.SelectedLayerId : layers[0].LayerId,
            LookByDevice = (webcam.LookByDevice ?? new Dictionary<string, Camera.CameraLayerSettings>())
                .Where(kv => kv.Value is not null)
                .ToDictionary(kv => kv.Key, kv => kv.Value.Clamped()),
            HardwareByDevice = webcam.HardwareByDevice ?? new Dictionary<string, IReadOnlyDictionary<Camera.HardwareControl, double?>>(),
            VirtualCamera = (webcam.VirtualCamera ?? new VirtualCameraSettings()) is var vc && vc.LayerId is { } vcLayer && !ids.Contains(vcLayer)
                ? vc with { LayerId = null }
                : webcam.VirtualCamera ?? new VirtualCameraSettings(),
        };
    }

    private static double Finite(double v, double fallback) => double.IsFinite(v) ? v : fallback;

    /// <summary>A mixer channel name: lower-case letters, digits and '_' (it becomes part of an FFmpeg filter name).</summary>
    private static bool IsChannel(string? name) =>
        name is { Length: > 0 and <= 40 } && name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');
}

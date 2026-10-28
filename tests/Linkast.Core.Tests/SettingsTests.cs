// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core.Camera;
using Linkast.Core.Settings;

namespace Linkast.Core.Tests;

public class SettingsSerializerTests
{
    [Fact]
    public void Version_3_gives_every_scene_its_own_capture_and_overlays()
    {
        var gaming = SceneLayout.DefaultLandscape("camera-1") with { HiddenOverlays = ["overlay-2"] };
        var soon = SceneLayout.Create(SceneKind.StartingSoon, "camera-1", id: SceneLayout.StartingSoonId);
        var old = new LinkastSettings
        {
            Stream = new StreamSettings { SourceKind = "Window", WindowTitle = "Dota 2", WindowProcess = "dota2.exe", CaptureCursor = false },
            Webcam = new WebcamSettings { Layouts = [soon, gaming] },
            Overlays = new OverlaySettings
            {
                Items =
                [
                    new OverlayConfig { LayerId = "overlay-1", Name = "Alerts", Kind = OverlayKind.Web, Position = OverlayPosition.TopRight, Size = 0.3 },
                    new OverlayConfig { LayerId = "overlay-2", Name = "Logo", Kind = OverlayKind.Image, Crop = new NormalizedRect(0.1, 0, 0.8, 1) },
                ],
            },
        };
        var json = SettingsSerializer.Serialize(old).Replace("\"schemaVersion\": 4", "\"schemaVersion\": 3", StringComparison.Ordinal);

        var (back, outcome) = SettingsSerializer.Deserialize(json);

        Assert.Equal(SettingsLoadOutcome.Migrated, outcome);
        var g = back.Webcam.Layouts.Single(l => l.Id == gaming.Id);
        Assert.Equal((SceneCaptureKind.Window, "dota2.exe", false), (g.Capture!.Kind, g.Capture.WindowProcess, g.Capture.CaptureCursor));
        Assert.Equal(["overlay-1"], g.Overlays.Keys);
        Assert.Equal((OverlayPosition.TopRight, 0.3), (g.Overlays["overlay-1"].Position, g.Overlays["overlay-1"].Size));

        // Starting soon hid the game: it captures nothing, and shows both overlays (it hid neither).
        var s = back.Webcam.Layouts.Single(l => l.Id == soon.Id);
        Assert.Equal(SceneCaptureKind.None, s.Capture!.Kind);
        Assert.Equal(new NormalizedRect(0.1, 0, 0.8, 1), s.Overlays["overlay-2"].Crop);
    }

    [Fact]
    public void Round_trip_preserves_everything()
    {
        var custom = CameraPresets.Duplicate(CameraPresets.BuiltIn[0], CameraPresets.BuiltIn, "Mine");
        var face = new CameraLayerConfig
        {
            LayerId = "face",
            Name = "Face cam",
            Enabled = true,
            DeviceId = @"\\?\USB#VID_046D",
            Format = new CameraFormatRequest(1920, 1080, 30),
            Look = CameraLayerSettings.Default with
            {
                Shape = LayerShape.Circle,
                BorderColor = new Argb(255, 1, 2, 3),
                Adjustments = new() { Brightness = 0.2 },
                Framing = new FramingSettings { Enabled = true, FaceSize = 0.35 },
                Effects = new LayerEffectSettings { Animation = BorderAnimation.ColorCycle, Glow = 0.5, Audio = new() { Enabled = true, Style = AudioReactiveStyle.GlowRing } },
            },
            PresetId = custom.Id,
        };
        var desk = new CameraLayerConfig { LayerId = "desk", Name = "Desk cam" };
        var vertical = SceneLayout.DefaultVertical("face").WithPlacement("desk", new NormalizedRect(0.6, 0.05, 0.35, 0.2));
        var s = new LinkastSettings
        {
            Stream = new StreamSettings { SourceKind = "Window", WindowTitle = "Game", ServerUrl = "rtmp://x/app", VideoBitrateKbps = 4500, FrameRate = 30 },
            Webcam = new WebcamSettings
            {
                Layers = [face, desk],
                Layouts = [SceneLayout.DefaultLandscape("face"), vertical],
                ActiveLayoutId = vertical.Id,
                SelectedLayerId = "desk",
                LookByDevice = new Dictionary<string, CameraLayerSettings> { ["dev"] = CameraLayerSettings.Default with { Zoom = 2 } },
                HardwareByDevice = new Dictionary<string, IReadOnlyDictionary<HardwareControl, double?>>
                {
                    ["dev"] = new Dictionary<HardwareControl, double?> { [HardwareControl.Exposure] = -5, [HardwareControl.Focus] = null },
                },
            },
            CustomPresets = [custom],
        };

        var json = SettingsSerializer.Serialize(s);
        var (back, outcome) = SettingsSerializer.Deserialize(json);

        Assert.Equal(SettingsLoadOutcome.Loaded, outcome);
        Assert.Contains("\"schemaVersion\": 4", json, StringComparison.Ordinal);
        Assert.Contains("\"shape\": \"Circle\"", json, StringComparison.Ordinal); // enums as names
        Assert.Equal("Window", back.Stream.SourceKind);
        Assert.Equal(["face", "desk"], back.Webcam.Layers.Select(l => l.LayerId));
        var f = back.Webcam.Layers[0];
        Assert.Equal("Face cam", f.Name);
        Assert.Equal(LayerShape.Circle, f.Look.Shape);
        Assert.Equal(new Argb(255, 1, 2, 3), f.Look.BorderColor);
        Assert.Equal(0.35, f.Look.Framing.FaceSize);
        Assert.Equal(BorderAnimation.ColorCycle, f.Look.Effects.Animation);
        Assert.Equal(AudioReactiveStyle.GlowRing, f.Look.Effects.Audio.Style);
        Assert.Equal(1920, f.Format.Width);
        Assert.Equal(vertical.Id, back.Webcam.ActiveLayoutId);
        Assert.Equal(OutputOrientation.Vertical, back.Webcam.ActiveLayout.Orientation);
        Assert.Equal(BaseFit.Fill, back.Webcam.ActiveLayout.BaseFit);
        Assert.Equal(new NormalizedRect(0.6, 0.05, 0.35, 0.2), back.Webcam.ActiveLayout.CameraPlacements["desk"]);
        Assert.Equal("desk", back.Webcam.SelectedLayerId);
        Assert.Equal(2, back.Webcam.LookByDevice["dev"].Zoom);
        Assert.Equal(-5, back.Webcam.HardwareByDevice["dev"][HardwareControl.Exposure]);
        Assert.Null(back.Webcam.HardwareByDevice["dev"][HardwareControl.Focus]);
        Assert.Equal(custom.Id, Assert.Single(back.CustomPresets).Id);
    }

    /// <summary>Version 2 had layouts, an away screen and an on/off switch per overlay; version 3 has scenes.</summary>
    [Fact]
    public void Version_2_file_migrates_to_scenes()
    {
        const string v2 = """
            {
              "schemaVersion": 2,
              "webcam": {
                "layers": [ { "layerId": "camera-1", "name": "Face" } ],
                "layouts": [
                  { "id": "6f1c1a40-0b7e-4d61-9b70-1c0f3a9e0101", "name": "Landscape (16:9)", "cameraPlacements": { "camera-1": { "x": 0.7, "y": 0.7, "width": 0.25, "height": 0.25 } } },
                  { "id": "6f1c1a40-0b7e-4d61-9b70-1c0f3a9e0102", "name": "My vertical", "orientation": "Vertical" }
                ],
                "activeLayoutId": "6f1c1a40-0b7e-4d61-9b70-1c0f3a9e0101"
              },
              "overlays": {
                "items": [ { "layerId": "overlay-1", "name": "Alerts", "kind": "Web", "visible": false }, { "layerId": "overlay-2", "name": "Logo" } ],
                "away": { "title": "Back soon", "subtitle": "Grab a drink", "imagePath": "C:\\Pictures\\brb.png", "muteSystemAudio": true }
              }
            }
            """;
        var (s, outcome) = SettingsSerializer.Deserialize(v2);
        Assert.Equal(SettingsLoadOutcome.Migrated, outcome);

        var scenes = s.Webcam.Layouts;
        Assert.Equal(["Starting soon", "Gaming", "Just chatting", "Be right back", "Ending", "My vertical"], scenes.Select(l => l.Name));
        Assert.Equal(SceneLayout.LandscapeId, s.Webcam.ActiveLayoutId);
        Assert.Equal(SceneKind.Gaming, scenes[1].Kind);
        Assert.Equal(0.7, scenes[1].CameraPlacements["camera-1"].X, 6); // the old placement is kept
        Assert.Equal("My vertical", scenes[5].Name); // a name the user changed is kept

        // The away screen is the Be right back scene, with its picture, words and "mute PC sound".
        var brb = scenes[3];
        Assert.Equal(SceneLayout.BeRightBackId, brb.Id);
        Assert.Equal((SceneMediaKind.Picture, @"C:\Pictures\brb.png", "Back soon", "Grab a drink"), (brb.Media!.Kind, brb.Media.Path, brb.Media.Title, brb.Media.Subtitle));
        Assert.True(brb.Mutes(AudioChannels.System));
        Assert.False(brb.ShowCapture);

        // The overlay that was off is hidden in every scene and itself switched on; the other shows everywhere.
        Assert.All(scenes, l => Assert.False(l.Shows("overlay-1")));
        Assert.All(scenes, l => Assert.True(l.Shows("overlay-2")));
        Assert.All(s.Overlays.Items, o => Assert.True(o.Visible));

        // Loading the saved result again changes nothing.
        var (again, outcome2) = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(s));
        Assert.Equal(SettingsLoadOutcome.Loaded, outcome2);
        Assert.Equal(scenes.Select(l => l.Id), again.Webcam.Layouts.Select(l => l.Id));
    }

    [Fact]
    public void Version_1_file_migrates_to_layers_and_layouts()
    {
        // The shape written by Linkast 0.1 (Webcam Phase 1).
        var v1 = """
            {
              "schemaVersion": 1,
              "stream": { "serverUrl": "rtmp://127.0.0.1:1935/live", "sourceKind": "Display" },
              "webcam": {
                "enabled": true,
                "deviceId": "\\\\?\\ROOT#CAMERA#0000",
                "deviceName": "Iriun Webcam",
                "format": { "width": null, "height": null, "frameRate": null, "subtype": null },
                "look": { "shape": "Circle", "zoom": 1.3, "mirror": true, "adjustments": { "brightness": 0.4 } },
                "lookByDevice": { "\\\\?\\ROOT#CAMERA#0000": { "shape": "Circle" } },
                "placement": { "x": 0.521, "y": 0.376, "width": 0.232, "height": 0.412 },
                "presetId": "6f1c1a40-0b7e-4d61-9b70-1c0f3a9e0005",
                "snapToGrid": false
              },
              "customPresets": []
            }
            """;
        var (s, outcome) = SettingsSerializer.Deserialize(v1);

        Assert.Equal(SettingsLoadOutcome.Migrated, outcome);
        var layer = Assert.Single(s.Webcam.Layers);
        Assert.Equal("camera-1", layer.LayerId);
        Assert.Equal("Camera 1", layer.Name);
        Assert.True(layer.Enabled);
        Assert.Equal("Iriun Webcam", layer.DeviceName);
        Assert.Equal(LayerShape.Circle, layer.Look.Shape);
        Assert.Equal(0.4, layer.Look.Adjustments.Brightness);
        Assert.Equal(CameraPresets.MinimalId, layer.PresetId);
        // The two layouts of version 2 plus the scenes version 3 adds (Starting soon, Just chatting, Be right back, Ending).
        Assert.Equal(6, s.Webcam.Layouts.Count);
        Assert.Equal(SceneLayout.LandscapeId, s.Webcam.ActiveLayoutId);
        Assert.Equal(new NormalizedRect(0.521, 0.376, 0.232, 0.412), s.Webcam.ActiveLayout.CameraPlacements["camera-1"]);
        Assert.Contains(s.Webcam.Layouts, l => l.Orientation == OutputOrientation.Vertical && l.CameraPlacements.ContainsKey("camera-1"));
        Assert.False(s.Webcam.SnapToGrid);
        Assert.Single(s.Webcam.LookByDevice);
        Assert.Equal("rtmp://127.0.0.1:1935/live", s.Stream.ServerUrl);

        // Saving and loading again is stable (no second migration).
        var (again, outcome2) = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(s));
        Assert.Equal(SettingsLoadOutcome.Loaded, outcome2);
        Assert.Equal(s.Webcam.ActiveLayout.CameraPlacements["camera-1"], again.Webcam.ActiveLayout.CameraPlacements["camera-1"]);
    }

    [Fact]
    public void Layers_and_layouts_are_sanitized()
    {
        var bad = new LinkastSettings
        {
            Webcam = new WebcamSettings
            {
                Layers = [.. Enumerable.Range(0, 6).Select(i => new CameraLayerConfig { LayerId = i == 1 ? "camera-1" : $"cam{i}", Name = "" })],
                Layouts = [new SceneLayout { Name = " ", CameraPlacements = new Dictionary<string, NormalizedRect> { ["ghost"] = new(0, 0, 0.5, 0.5) } }],
                ActiveLayoutId = Guid.NewGuid(),
                SelectedLayerId = "nobody",
            },
        };
        var (s, _) = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(bad));
        Assert.Equal(CameraLayerConfig.MaxLayers, s.Webcam.Layers.Count);
        Assert.Equal(s.Webcam.Layers.Count, s.Webcam.Layers.Select(l => l.LayerId).Distinct().Count());
        Assert.All(s.Webcam.Layers, l => Assert.False(string.IsNullOrWhiteSpace(l.Name)));
        Assert.Empty(s.Webcam.Layouts.First(l => l.Kind == SceneKind.Custom).CameraPlacements); // placement for a missing layer dropped
        Assert.Equal("Scene", s.Webcam.Layouts.First(l => l.Kind == SceneKind.Custom).Name);
        Assert.Contains(s.Webcam.Layouts, l => l.Id == s.Webcam.ActiveLayoutId);
        Assert.Equal(s.Webcam.Layers[0].LayerId, s.Webcam.SelectedLayerId);
    }

    [Fact]
    public void Background_and_effect_settings_round_trip_and_default_off()
    {
        var look = CameraLayerSettings.Default with
        {
            Adjustments = new() { LowLightBoost = 0.7 },
            Background = new BackgroundSettings { Mode = BackgroundMode.Image, BlurStrength = 0.3, EdgeSoftness = 0.5, Quality = SegmentationQuality.Quality, PreferGpu = false, ImagePath = @"C:\bg\a.png" },
        };
        var settings = new LinkastSettings { Webcam = new WebcamSettings { Layers = [new CameraLayerConfig { Look = look }] } };
        var (back, _) = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings));
        Assert.Equal(look.Background, back.Webcam.Layers[0].Look.Background);
        Assert.Equal(0.7, back.Webcam.Layers[0].Look.Adjustments.LowLightBoost);

        // Older looks have no background, framing or effects: all default to off.
        var (old, _) = SettingsSerializer.Deserialize("{\"schemaVersion\":1,\"webcam\":{\"look\":{\"shape\":\"Circle\"}}}");
        var l = old.Webcam.Layers[0].Look;
        Assert.Equal(BackgroundMode.Original, l.Background.Mode);
        Assert.False(l.Framing.Enabled);
        Assert.Equal(BorderAnimation.None, l.Effects.Animation);
        Assert.False(l.Effects.Audio.Enabled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Missing_file_gives_defaults(string? json)
    {
        var (s, outcome) = SettingsSerializer.Deserialize(json);
        Assert.Equal(SettingsLoadOutcome.Defaults, outcome);
        Assert.Equal(new LinkastSettings().Stream, s.Stream);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"stream\":{}}", AppTheme.Dark)]          // saved before themes existed
    [InlineData("{\"schemaVersion\":2,\"theme\":\"light\"}", AppTheme.Light)]
    [InlineData("{\"schemaVersion\":2,\"theme\":\"System\"}", AppTheme.System)]
    [InlineData("{\"schemaVersion\":2,\"theme\":7}", AppTheme.Dark)]           // out of range
    public void Theme_defaults_to_dark_and_round_trips(string json, AppTheme expected)
    {
        var (s, _) = SettingsSerializer.Deserialize(json);
        Assert.Equal(expected, s.Theme);
        Assert.Equal(expected, SettingsSerializer.Deserialize(SettingsSerializer.Serialize(s)).Settings.Theme);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"stream\": 42}")]
    public void Corrupt_file_gives_defaults_and_is_reported(string json) =>
        Assert.Equal(SettingsLoadOutcome.Corrupt, SettingsSerializer.Deserialize(json).Outcome);

    [Fact]
    public void Unversioned_file_is_migrated_and_missing_fields_default()
    {
        var (s, outcome) = SettingsSerializer.Deserialize("{\"stream\":{\"serverUrl\":\"rtmp://a/b\"},\"unknownFutureThing\":true}");
        Assert.Equal(SettingsLoadOutcome.Migrated, outcome);
        Assert.Equal("rtmp://a/b", s.Stream.ServerUrl);
        Assert.Equal(6000, s.Stream.VideoBitrateKbps);
        Assert.Single(s.Webcam.Layers);
    }

    [Fact]
    public void Newer_version_is_loaded_best_effort() =>
        Assert.Equal(SettingsLoadOutcome.NewerVersion, SettingsSerializer.Deserialize("{\"schemaVersion\": 99}").Outcome);

    [Fact]
    public void Out_of_range_values_are_clamped()
    {
        var json = """
            {"schemaVersion":2,
             "stream":{"frameRate":144,"videoBitrateKbps":-5,"systemAudioVolume":"NaN"},
             "webcam":{"layers":[{"layerId":"a","look":{"zoom":50,"borderWidth":3,"shape":"Circle","framing":{"maxZoom":9},"effects":{"glow":5}}}],
                       "layouts":[{"name":"L","cameraPlacements":{"a":{"x":5,"y":-1,"width":9,"height":0}}}]},
             "customPresets":[{"name":"  ","isBuiltIn":false},{"name":"x","isBuiltIn":true}]}
            """;
        var (s, _) = SettingsSerializer.Deserialize(json);
        Assert.Equal(60, s.Stream.FrameRate);
        Assert.Equal(200, s.Stream.VideoBitrateKbps);
        Assert.Equal(100, s.Stream.SystemAudioVolume);
        var look = s.Webcam.Layers[0].Look;
        Assert.Equal(4, look.Zoom);
        Assert.Equal(0.1, look.BorderWidth);
        Assert.Equal(4, look.Framing.MaxZoom);
        Assert.Equal(1, look.Effects.Glow);
        var p = s.Webcam.Layouts.First(l => l.Name == "L").CameraPlacements["a"];
        Assert.True(p.Right <= 1 + 1e-9 && p.Bottom <= 1 + 1e-9);
        var preset = Assert.Single(s.CustomPresets); // the "built-in" impostor is dropped
        Assert.Equal("Custom", preset.Name);
    }

    [Fact]
    public void Unknown_enum_value_marks_file_corrupt_rather_than_crashing() =>
        Assert.Equal(SettingsLoadOutcome.Corrupt, SettingsSerializer.Deserialize("{\"schemaVersion\":2,\"webcam\":{\"layers\":[{\"look\":{\"shape\":\"Hexagon\"}}]}}").Outcome);
}

public class CameraPresetTests
{
    [Fact]
    public void Built_in_presets_cover_the_brief_with_stable_ids()
    {
        Assert.Equal(["Gaming", "Just Chatting", "Professional", "Low Light", "Minimal"], CameraPresets.BuiltIn.Select(p => p.Name));
        Assert.All(CameraPresets.BuiltIn, p => Assert.True(p.IsBuiltIn));
        Assert.Equal(CameraPresets.BuiltIn.Count, CameraPresets.BuiltIn.Select(p => p.Id).Distinct().Count());
        Assert.Equal(CameraPresets.GamingId, CameraPresets.BuiltIn[0].Id);
    }

    [Fact]
    public void Built_in_looks_are_valid()
    {
        foreach (var p in CameraPresets.BuiltIn)
        {
            Assert.Equal(p.Look, p.Look.Clamped());
            if (p.Placement is { } r)
            {
                Assert.Equal(r, r.Clamped());
            }
        }
    }

    [Fact]
    public void Duplicate_creates_unique_custom_copy()
    {
        var a = CameraPresets.Duplicate(CameraPresets.BuiltIn[0], CameraPresets.BuiltIn);
        var b = CameraPresets.Duplicate(CameraPresets.BuiltIn[0], [.. CameraPresets.BuiltIn, a]);
        Assert.False(a.IsBuiltIn);
        Assert.NotEqual(CameraPresets.GamingId, a.Id);
        Assert.Equal("Gaming copy", a.Name);
        Assert.Equal("Gaming copy 2", b.Name);
        Assert.Equal(CameraPresets.BuiltIn[0].Look, a.Look);
    }

    [Fact]
    public void Find_handles_missing_ids()
    {
        Assert.Null(CameraPresets.Find(Guid.NewGuid(), []));
        Assert.Null(CameraPresets.Find(null, []));
        Assert.Equal("Minimal", CameraPresets.Find(CameraPresets.MinimalId, [])?.Name);
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using Linkast.Core.Camera;
using Linkast.Core.Settings;

namespace Linkast.Core.Obs;

/// <summary>What Linkast captures, as found in the OBS scene.</summary>
public enum ObsCaptureKind
{
    Display,
    Window,
}

public sealed record ObsVideo(int Width, int Height, int Fps);

/// <param name="ServiceName">"Twitch", "YouTube - RTMPS" or null for a custom server.</param>
public sealed record ObsStreamTarget(string ServerUrl, Secret Key, string? ServiceName);

/// <param name="MonitorIndex">0-based display number, when OBS stored one.</param>
/// <param name="WindowExe">Program file name, for example "game.exe".</param>
public sealed record ObsCapture(ObsCaptureKind Kind, int? MonitorIndex, string? WindowTitle, string? WindowExe, bool CaptureCursor);

/// <param name="Placement">Where it is on the canvas, or null when OBS doesn't say enough to tell.</param>
public sealed record ObsCamera(string Name, string DeviceName, NormalizedRect? Placement);

/// <param name="Url">The page of a browser source (it often contains an account token).</param>
public sealed record ObsOverlay(string Name, OverlayKind Kind, Secret? Url, string? ImagePath, int PageWidth, int PageHeight, NormalizedRect? Placement);

/// <summary>Everything Linkast can take over from one OBS profile and its current scene.</summary>
public sealed record ObsImport
{
    public required string ProfileName { get; init; }

    public string? SceneCollectionName { get; init; }

    public string? SceneName { get; init; }

    public ObsVideo? Video { get; init; }

    public int? VideoBitrateKbps { get; init; }

    public int? AudioBitrateKbps { get; init; }

    /// <summary>Linkast encoder id matching the OBS encoder ("h264_nvenc", "libx264", …), or null.</summary>
    public string? EncoderId { get; init; }

    /// <summary>The encoder's preset in Linkast's (FFmpeg's) words, for example "p6" or "veryfast", when OBS set one that maps.</summary>
    public string? EncoderPreset { get; init; }

    /// <summary>Seconds between keyframes, when OBS set it explicitly (advanced output).</summary>
    public int? KeyframeIntervalSeconds { get; init; }

    public ObsStreamTarget? Stream { get; init; }

    public ObsCapture? Capture { get; init; }

    public IReadOnlyList<ObsCamera> Cameras { get; init; } = [];

    public IReadOnlyList<ObsOverlay> Overlays { get; init; } = [];

    public bool DesktopAudio { get; init; }

    public bool Microphone { get; init; }

    public string? RecordingFolder { get; init; }

    /// <summary>True when OBS recorded to MP4 (or MOV), false for MKV and the rest, null when unknown.</summary>
    public bool? RecordAsMp4 { get; init; }

    /// <summary>The local IP address OBS bound its stream to, when it wasn't the default.</summary>
    public string? BindIp { get; init; }

    /// <summary>Sources Linkast does not take over, each with the reason (for example "Clock (text): text sources aren't supported").</summary>
    public IReadOnlyList<string> Skipped { get; init; } = [];
}

/// <summary>
/// Reads an OBS Studio settings folder (normally %APPDATA%\obs-studio). Only reads; never changes OBS.
/// Takes the current profile and the current scene of the current scene collection. A single profile
/// folder (one with basic.ini in it: an export, or basic\profiles\X of an install) is accepted too.
/// </summary>
public static class ObsImporter
{
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio");

    /// <summary>The OBS settings in <paramref name="obsFolder"/>, or null when no OBS profile is there.</summary>
    public static ObsImport? Read(string obsFolder)
    {
        ArgumentException.ThrowIfNullOrEmpty(obsFolder);

        string? profileDir, profilePath, obsRoot;
        IniFile? global;
        if (File.Exists(Path.Combine(obsFolder, "basic.ini")))
        {
            // A profile folder. When it sits inside an install (basic\profiles\X), the scenes are found from there.
            profilePath = Path.TrimEndingDirectorySeparator(obsFolder);
            profileDir = Path.GetFileName(profilePath);
            var parent = Path.GetDirectoryName(Path.GetDirectoryName(profilePath));
            obsRoot = parent is not null && string.Equals(Path.GetFileName(parent), "basic", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(parent)
                : null;
            global = obsRoot is null ? null : LoadGlobal(obsRoot);
        }
        else
        {
            obsRoot = obsFolder;
            global = LoadGlobal(obsFolder);
            var profilesRoot = Path.Combine(obsFolder, "basic", "profiles");
            profileDir = global?.Get("Basic", "ProfileDir") ?? global?.Get("Basic", "Profile");
            if (profileDir is null || !File.Exists(Path.Combine(profilesRoot, profileDir, "basic.ini")))
            {
                // Fall back to the only (or first) profile.
                profileDir = Directory.Exists(profilesRoot)
                    ? Directory.GetDirectories(profilesRoot).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault(d => File.Exists(Path.Combine(profilesRoot, d!, "basic.ini")))
                    : null;
            }

            if (profileDir is null)
            {
                return null;
            }

            profilePath = Path.Combine(profilesRoot, profileDir);
        }

        var basic = IniFile.Load(Path.Combine(profilePath, "basic.ini"))!;
        var skipped = new List<string>();

        var advanced = string.Equals(basic.Get("Output", "Mode"), "Advanced", StringComparison.OrdinalIgnoreCase);
        using var streamEncoder = ReadJson(Path.Combine(profilePath, "streamEncoder.json"));
        var se = streamEncoder?.RootElement ?? default;
        var obsEncoder = advanced ? basic.Get("AdvOut", "Encoder") : basic.Get("SimpleOutput", "StreamEncoder");
        var encoder = MapEncoder(obsEncoder);
        int? videoKbps = advanced ? Int(se, "bitrate") ?? 2500 : basic.GetInt("SimpleOutput", "VBitrate") ?? 2500;

        // Advanced output streams one of six audio tracks, each with its own bitrate.
        var track = Math.Clamp(basic.GetInt("AdvOut", "TrackIndex") ?? 1, 1, 6);
        int? audioKbps = advanced
            ? basic.GetInt("AdvOut", $"Track{track.ToString(CultureInfo.InvariantCulture)}Bitrate") ?? 160
            : basic.GetInt("SimpleOutput", "ABitrate") ?? 160;
        var preset = advanced ? AdvancedPreset(encoder, se) : SimplePreset(encoder, basic);
        int? keyint = advanced && Int(se, "keyint_sec") is > 0 and var k ? k : null;

        var recording = CleanPath(advanced
            ? basic.Get("AdvOut", "RecFilePath") ?? basic.Get("AdvOut", "FFFilePath")
            : basic.Get("SimpleOutput", "FilePath"));
        var recFormat = advanced
            ? basic.Get("AdvOut", "RecFormat2") ?? basic.Get("AdvOut", "RecFormat")
            : basic.Get("SimpleOutput", "RecFormat2") ?? basic.Get("SimpleOutput", "RecFormat");
        var bindIp = basic.Get("Output", "BindIP");

        var baseW = basic.GetInt("Video", "BaseCX") ?? 1920;
        var baseH = basic.GetInt("Video", "BaseCY") ?? 1080;
        var outW = basic.GetInt("Video", "OutputCX") ?? baseW;
        var outH = basic.GetInt("Video", "OutputCY") ?? baseH;

        var import = new ObsImport
        {
            ProfileName = basic.Get("General", "Name") ?? profileDir,
            Video = new ObsVideo(outW, outH, ReadFps(basic)),
            VideoBitrateKbps = videoKbps,
            AudioBitrateKbps = audioKbps,
            EncoderId = encoder,
            EncoderPreset = preset,
            KeyframeIntervalSeconds = keyint,
            Stream = ReadService(Path.Combine(profilePath, "service.json"), skipped),
            RecordingFolder = recording,
            RecordAsMp4 = recFormat is null ? null : recFormat.Contains("mp4", StringComparison.OrdinalIgnoreCase) || recFormat.Contains("mov", StringComparison.OrdinalIgnoreCase),
            BindIp = string.IsNullOrWhiteSpace(bindIp) || string.Equals(bindIp, "default", StringComparison.OrdinalIgnoreCase) ? null : bindIp.Trim(),
        };

        if (obsRoot is null)
        {
            return import with { Skipped = skipped };
        }

        var collectionFile = global?.Get("Basic", "SceneCollectionFile");
        var scenesPath = collectionFile is null ? null : Path.Combine(obsRoot, "basic", "scenes", collectionFile + ".json");
        if (scenesPath is null || !File.Exists(scenesPath))
        {
            var scenesDir = Path.Combine(obsRoot, "basic", "scenes");
            scenesPath = Directory.Exists(scenesDir)
                ? Directory.GetFiles(scenesDir, "*.json").Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                : null;
        }

        if (scenesPath is not null && ReadJson(scenesPath) is { } scenes)
        {
            using (scenes)
            {
                import = ReadScene(scenes.RootElement, import, baseW, baseH, skipped)
                    with { SceneCollectionName = global?.Get("Basic", "SceneCollection") ?? Path.GetFileNameWithoutExtension(scenesPath) };
            }
        }

        return import with { Skipped = skipped };
    }

    /// <summary>OBS 31 keeps the current profile in user.ini; older versions in global.ini.</summary>
    private static IniFile? LoadGlobal(string obsFolder) =>
        IniFile.Load(Path.Combine(obsFolder, "user.ini")) is { } user && user.Get("Basic", "ProfileDir") is not null
            ? user
            : IniFile.Load(Path.Combine(obsFolder, "global.ini"));

    /// <summary>OBS writes paths with doubled backslashes. A full, normalized path, or null.</summary>
    internal static string? CleanPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var path = raw.Trim().Replace(@"\\", @"\", StringComparison.Ordinal);
        try
        {
            return Path.IsPathFullyQualified(path) && path.IndexOfAny(Path.GetInvalidPathChars()) < 0 ? Path.GetFullPath(path) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>The stream encoder's preset in simple output mode, where each encoder has its own key.</summary>
    private static string? SimplePreset(string? encoder, IniFile basic) => encoder switch
    {
        "h264_nvenc" => MapPreset(encoder, basic.Get("SimpleOutput", "NVENCPreset2") ?? basic.Get("SimpleOutput", "NVENCPreset")),
        "libx264" => MapPreset(encoder, basic.Get("SimpleOutput", "Preset")),
        "h264_qsv" => MapPreset(encoder, basic.Get("SimpleOutput", "QSVPreset")),
        "h264_amf" => MapPreset(encoder, basic.Get("SimpleOutput", "AMDPreset")),
        _ => null,
    };

    /// <summary>The stream encoder's preset from streamEncoder.json (advanced output).</summary>
    private static string? AdvancedPreset(string? encoder, JsonElement se) => encoder switch
    {
        "h264_nvenc" => MapPreset(encoder, Str(se, "preset2") ?? Str(se, "preset")),
        "libx264" or "h264_amf" => MapPreset(encoder, Str(se, "preset")),
        "h264_qsv" => MapPreset(encoder, Str(se, "target_usage") ?? (Int(se, "target_usage") is { } tu ? tu.ToString(CultureInfo.InvariantCulture) : null)),
        _ => null,
    };

    private static readonly string[] X264Presets = ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"];

    /// <summary>
    /// An OBS preset for the given Linkast encoder in FFmpeg's words, or null when it has no equivalent
    /// (Linkast then keeps its own default). NVENC p1–p7 and x264 names are the same in both; Intel's
    /// quality/balanced/speed become QSV presets; AMD's are the same words.
    /// </summary>
    public static string? MapPreset(string? encoderId, string? obsPreset)
    {
        if (string.IsNullOrWhiteSpace(obsPreset))
        {
            return null;
        }

        var p = obsPreset.Trim().ToLowerInvariant();
        return encoderId switch
        {
            "h264_nvenc" when p.Length == 2 && p[0] == 'p' && p[1] is >= '1' and <= '7' => p,
            "libx264" when X264Presets.Contains(p) => p,
            "h264_amf" when p is "quality" or "balanced" or "speed" => p,
            "h264_qsv" => p switch
            {
                "quality" or "1" or "2" => "slower",
                "balanced" or "3" or "4" => "medium",
                "speed" or "5" or "6" or "7" => "veryfast",
                _ when X264Presets.Contains(p) => p,
                _ => null,
            },
            _ => null,
        };
    }

    private static int ReadFps(IniFile basic)
    {
        double fps = basic.GetInt("Video", "FPSType") switch
        {
            1 => basic.GetInt("Video", "FPSInt") ?? 30,
            2 => (basic.GetDouble("Video", "FPSNum") ?? 30) / Math.Max(1, basic.GetDouble("Video", "FPSDen") ?? 1),
            _ => double.TryParse(basic.Get("Video", "FPSCommon"), NumberStyles.Float, CultureInfo.InvariantCulture, out var c) ? c : 30,
        };
        return (int)Math.Round(fps);
    }

    /// <summary>OBS encoder ids (simple and advanced mode, old and new) to Linkast's.</summary>
    public static string? MapEncoder(string? obs)
    {
        if (string.IsNullOrWhiteSpace(obs))
        {
            return null;
        }

        var e = obs.ToLowerInvariant();
        if (e.Contains("hevc", StringComparison.Ordinal) || e.Contains("av1", StringComparison.Ordinal) || e.Contains("265", StringComparison.Ordinal))
        {
            return null; // Linkast streams H.264 only.
        }

        return e switch
        {
            _ when e.Contains("nvenc", StringComparison.Ordinal) => "h264_nvenc",
            _ when e.Contains("qsv", StringComparison.Ordinal) => "h264_qsv",
            _ when e.Contains("amd", StringComparison.Ordinal) || e.Contains("amf", StringComparison.Ordinal) => "h264_amf",
            _ when e.Contains("x264", StringComparison.Ordinal) => "libx264",
            _ => null,
        };
    }

    private static ObsStreamTarget? ReadService(string path, List<string> skipped)
    {
        using var doc = ReadJson(path);
        if (doc is null || !doc.RootElement.TryGetProperty("settings", out var s))
        {
            return null;
        }

        var service = Str(s, "service");
        var server = Str(s, "server");
        var key = Str(s, "key") ?? string.Empty;
        if (string.Equals(server, "auto", StringComparison.OrdinalIgnoreCase) && service?.StartsWith("Twitch", StringComparison.OrdinalIgnoreCase) == true)
        {
            server = "rtmp://live.twitch.tv/app";
        }

        if (server is null || !Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme is not ("rtmp" or "rtmps"))
        {
            skipped.Add($"Stream server ({server ?? "none"}): Linkast streams over RTMP or RTMPS only.");
            return null;
        }

        if (Bool(s, "use_auth") == true)
        {
            skipped.Add("The server's user name and password (RTMP authentication): Linkast signs in with the stream key only.");
        }

        return new ObsStreamTarget(server, new Secret(key), service);
    }

    private static ObsImport ReadScene(JsonElement root, ObsImport import, int baseW, int baseH, List<string> skipped)
    {
        if (!root.TryGetProperty("sources", out var sourcesEl) || sourcesEl.ValueKind != JsonValueKind.Array)
        {
            return import;
        }

        var sources = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var src in sourcesEl.EnumerateArray())
        {
            if (Str(src, "name") is { } n)
            {
                sources[n] = src;
            }
        }

        var sceneName = Str(root, "current_scene") ?? Str(root, "current_program_scene");
        if (sceneName is null || !sources.TryGetValue(sceneName, out var scene))
        {
            scene = sources.Values.FirstOrDefault(s => Str(s, "id") == "scene");
            sceneName = Str(scene, "name");
        }

        // Audio set up in OBS's global audio settings (Desktop Audio, Mic/Aux).
        var desktopAudio = root.TryGetProperty("DesktopAudioDevice1", out _);
        var microphone = root.TryGetProperty("AuxAudioDevice1", out _);

        ObsCapture? capture = null;
        var cameras = new List<ObsCamera>();
        var overlays = new List<ObsOverlay>();
        foreach (var (item, source) in SceneItems(scene, sources, depth: 0))
        {
            var name = Str(item, "name") ?? "Source";
            var id = Str(source, "id") ?? Str(source, "versioned_id") ?? string.Empty;
            var settings = source.TryGetProperty("settings", out var st) ? st : default;
            switch (id)
            {
                case "monitor_capture" when capture is null:
                    capture = new ObsCapture(ObsCaptureKind.Display, Int(settings, "monitor"), null, null, Bool(settings, "capture_cursor") ?? true);
                    break;
                case "window_capture" or "game_capture" when capture is null:
                    var (title, exe) = ParseWindow(Str(settings, "window"));
                    if (id == "game_capture" && Str(settings, "capture_mode") is { } mode && mode != "window")
                    {
                        skipped.Add($"{name} (game capture, \"{mode}\" mode): choose the game window in Linkast after importing.");
                        break;
                    }

                    capture = new ObsCapture(ObsCaptureKind.Window, null, title, exe, Bool(settings, "cursor") ?? Bool(settings, "capture_cursor") ?? true);
                    break;
                case "monitor_capture" or "window_capture" or "game_capture":
                    skipped.Add($"{name}: Linkast captures one screen or window; the first one in the scene was used.");
                    break;
                case "dshow_input":
                    var device = DecodeObsText(Str(settings, "video_device_id")?.Split(':')[0]);
                    if (string.IsNullOrWhiteSpace(device))
                    {
                        skipped.Add($"{name}: no camera is selected in OBS.");
                    }
                    else if (cameras.Count >= CameraLayerConfig.MaxLayers)
                    {
                        skipped.Add($"{name}: Linkast shows up to {CameraLayerConfig.MaxLayers} cameras.");
                    }
                    else
                    {
                        cameras.Add(new ObsCamera(name, device, Place(item, CameraSize(settings), baseW, baseH)));
                    }

                    break;
                case "browser_source" or "image_source" when overlays.Count >= OverlayConfig.MaxOverlays:
                    skipped.Add($"{name}: Linkast shows up to {OverlayConfig.MaxOverlays} overlays.");
                    break;
                case "browser_source":
                    var url = Bool(settings, "is_local_file") == true ? null : Str(settings, "url");
                    if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
                    {
                        skipped.Add($"{name}: only web addresses (https://…) can be imported, not local files.");
                        break;
                    }

                    var w = Int(settings, "width") ?? 800;
                    var h = Int(settings, "height") ?? 600;
                    overlays.Add(new ObsOverlay(name, OverlayKind.Web, new Secret(url), null, w, h, Place(item, (w, h), baseW, baseH)));
                    break;
                case "image_source":
                    var file = Str(settings, "file");
                    if (file is null)
                    {
                        skipped.Add($"{name}: no picture is selected in OBS.");
                        break;
                    }

                    overlays.Add(new ObsOverlay(name, OverlayKind.Image, null, file, 0, 0, Place(item, null, baseW, baseH)));
                    break;
                case "wasapi_output_capture" or "wasapi_process_output_capture":
                    desktopAudio = true;
                    break;
                case "wasapi_input_capture":
                    microphone = true;
                    break;
                default:
                    skipped.Add($"{name}: {Describe(id)} aren't supported in Linkast.");
                    break;
            }
        }

        return import with
        {
            SceneName = sceneName,
            Capture = capture,
            Cameras = cameras,
            Overlays = overlays,
            DesktopAudio = desktopAudio,
            Microphone = microphone,
        };
    }

    /// <summary>The visible items of a scene, bottom to top, with groups and nested scenes flattened.</summary>
    private static IEnumerable<(JsonElement Item, JsonElement Source)> SceneItems(JsonElement scene, Dictionary<string, JsonElement> sources, int depth)
    {
        if (depth > 4 || scene.ValueKind != JsonValueKind.Object || !scene.TryGetProperty("settings", out var st)
            || !st.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (Bool(item, "visible") == false || Str(item, "name") is not { } name || !sources.TryGetValue(name, out var source))
            {
                continue;
            }

            if (Str(source, "id") is "scene" or "group")
            {
                foreach (var inner in SceneItems(source, sources, depth + 1))
                {
                    yield return inner;
                }

                continue;
            }

            yield return (item, source);
        }
    }

    /// <summary>A webcam's picture size, from its OBS resolution setting (most webcams default to 1080p).</summary>
    private static (int, int) CameraSize(JsonElement settings)
    {
        if (Int(settings, "res_type") == 1 && Str(settings, "resolution") is { } res && res.Split('x') is [var w, var h]
            && int.TryParse(w, CultureInfo.InvariantCulture, out var rw) && int.TryParse(h, CultureInfo.InvariantCulture, out var rh) && rw > 0 && rh > 0)
        {
            return (rw, rh);
        }

        return (1920, 1080);
    }

    /// <summary>
    /// The item's rectangle on the canvas, from OBS's position, alignment and scale (or bounding box).
    /// Null when the source's own size is unknown and it has no bounding box.
    /// </summary>
    public static NormalizedRect? Place(JsonElement item, (int Width, int Height)? sourceSize, int baseW, int baseH)
    {
        double w, h;
        if (Int(item, "bounds_type") is > 0 && Vec(item, "bounds") is { X: > 0, Y: > 0 } bounds)
        {
            (w, h) = (bounds.X, bounds.Y);
        }
        else if (sourceSize is { } size)
        {
            var scale = Vec(item, "scale") ?? (1, 1);
            (w, h) = (size.Width * Math.Abs(scale.X), size.Height * Math.Abs(scale.Y));
        }
        else
        {
            return null;
        }

        var pos = Vec(item, "pos") ?? (0, 0);
        var align = Int(item, "align") ?? 5;
        var x = (align & 1) != 0 ? pos.X : (align & 2) != 0 ? pos.X - w : pos.X - (w / 2);
        var y = (align & 4) != 0 ? pos.Y : (align & 8) != 0 ? pos.Y - h : pos.Y - (h / 2);
        var rect = new NormalizedRect(x / baseW, y / baseH, w / baseW, h / baseH);
        return rect.Width > 0 && rect.Height > 0 ? rect.Clamped() : null;
    }

    /// <summary>"Title:WindowClass:game.exe" with ':' written as "#3A".</summary>
    private static (string? Title, string? Exe) ParseWindow(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return (null, null);
        }

        var parts = value.Split(':');
        var title = DecodeObsText(parts[0]);
        var exe = parts.Length >= 3 ? DecodeObsText(parts[^1]) : null;
        return (string.IsNullOrEmpty(title) ? null : title, string.IsNullOrEmpty(exe) ? null : exe);
    }

    private static string? DecodeObsText(string? s) => s?.Replace("#3A", ":", StringComparison.OrdinalIgnoreCase)
        .Replace("#22", "\"", StringComparison.OrdinalIgnoreCase).Replace("#23", "#", StringComparison.OrdinalIgnoreCase);

    private static string Describe(string id) => id switch
    {
        "text_gdiplus" or "text_gdiplus_v2" or "text_gdiplus_v3" or "text_ft2_source" or "text_ft2_source_v2" => "text sources",
        "color_source" or "color_source_v2" or "color_source_v3" => "color sources",
        "ffmpeg_source" or "vlc_source" => "media files and playlists",
        "slideshow" or "slideshow_v2" => "slide shows",
        "" => "unknown sources",
        _ => $"\"{id}\" sources",
    };

    private static JsonDocument? ReadJson(string path)
    {
        try
        {
            return File.Exists(path) ? JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true }) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
            ? (int)Math.Round(d) : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;

    private static (double X, double Y)? Vec(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
        && v.TryGetProperty("x", out var x) && v.TryGetProperty("y", out var y) && x.TryGetDouble(out var dx) && y.TryGetDouble(out var dy)
            ? (dx, dy) : null;
}

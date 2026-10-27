// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core.Camera;
using Linkast.Core.Obs;
using Linkast.Core.Settings;

namespace Linkast.Core.Tests;

/// <summary>OBS settings folders written the way OBS 30/31 writes them.</summary>
public sealed class ObsImporterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("linkast-obs").FullName;

    public void Dispose() => Directory.Delete(_root, true);

    private void Write(string relative, string content)
    {
        var path = Path.Combine([_root, .. relative.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private const string Scene = """
        {
          "current_scene": "Gaming",
          "DesktopAudioDevice1": { "id": "wasapi_output_capture", "name": "Desktop Audio", "settings": { "device_id": "default" } },
          "sources": [
            { "id": "scene", "name": "Gaming", "settings": { "items": [
              { "name": "Game", "visible": true, "pos": { "x": 0, "y": 0 }, "scale": { "x": 1, "y": 1 }, "align": 5, "bounds_type": 0 },
              { "name": "Webcam", "visible": true, "pos": { "x": 1920, "y": 1080 }, "scale": { "x": 0.25, "y": 0.25 }, "align": 10, "bounds_type": 0 },
              { "name": "Alerts", "visible": true, "pos": { "x": 0, "y": 0 }, "scale": { "x": 1, "y": 1 }, "align": 5, "bounds_type": 0 },
              { "name": "Logo", "visible": true, "pos": { "x": 40, "y": 40 }, "scale": { "x": 1, "y": 1 }, "align": 5, "bounds_type": 2, "bounds": { "x": 192, "y": 108 } },
              { "name": "Clock", "visible": true, "pos": { "x": 0, "y": 0 }, "scale": { "x": 1, "y": 1 }, "align": 5 },
              { "name": "Hidden cam", "visible": false, "pos": { "x": 0, "y": 0 }, "scale": { "x": 1, "y": 1 }, "align": 5 },
              { "name": "Mic group", "visible": true, "pos": { "x": 0, "y": 0 }, "scale": { "x": 1, "y": 1 }, "align": 5 }
            ] } },
            { "id": "game_capture", "name": "Game", "settings": { "capture_mode": "window", "window": "Elden Ring#3A Nightreign:ELDEN RING:nightreign.exe", "capture_cursor": false } },
            { "id": "dshow_input", "name": "Webcam", "settings": { "video_device_id": "Logitech BRIO:#3Fusb#vid_046d&pid_085e", "res_type": 1, "resolution": "1280x720" } },
            { "id": "browser_source", "name": "Alerts", "settings": { "url": "https://reymit.ir/widget/alert?token=SECRET123", "width": 1920, "height": 1080 } },
            { "id": "image_source", "name": "Logo", "settings": { "file": "C:/Users/me/Pictures/logo.png" } },
            { "id": "text_gdiplus_v3", "name": "Clock", "settings": { "text": "12:00" } },
            { "id": "dshow_input", "name": "Hidden cam", "settings": { "video_device_id": "Other:x" } },
            { "id": "group", "name": "Mic group", "settings": { "items": [ { "name": "Mic", "visible": true } ] } },
            { "id": "wasapi_input_capture", "name": "Mic", "settings": { "device_id": "default" } }
          ]
        }
        """;

    private void WriteObs(string basicIni, string service)
    {
        Write("user.ini", "[Basic]\nProfile=My Stream\nProfileDir=My_Stream\nSceneCollection=Main\nSceneCollectionFile=Main\n");
        Write("basic/profiles/My_Stream/basic.ini", basicIni);
        Write("basic/profiles/My_Stream/service.json", service);
        Write("basic/scenes/Main.json", Scene);
    }

    // OBS writes its INI files with a byte order mark.
    private static readonly string SimpleIni = (char)0xFEFF + "[General]\nName=My Stream\n\n[Video]\nBaseCX=1920\nBaseCY=1080\nOutputCX=1280\nOutputCY=720\nFPSType=0\nFPSCommon=60\n\n[Output]\nMode=Simple\n\n[SimpleOutput]\nVBitrate=4500\nABitrate=128\nStreamEncoder=nvenc\nFilePath=C:/Users/me/Videos\n";

    [Fact]
    public void Simple_output_profile_with_twitch_and_a_full_scene()
    {
        WriteObs(SimpleIni, """{ "type": "rtmp_common", "settings": { "service": "Twitch", "server": "auto", "key": "live_123_abc" } }""");
        var import = ObsImporter.Read(_root)!;

        Assert.Equal("My Stream", import.ProfileName);
        Assert.Equal("Main", import.SceneCollectionName);
        Assert.Equal("Gaming", import.SceneName);
        Assert.Equal(new ObsVideo(1280, 720, 60), import.Video);
        Assert.Equal(4500, import.VideoBitrateKbps);
        Assert.Equal(128, import.AudioBitrateKbps);
        Assert.Equal("h264_nvenc", import.EncoderId);
        Assert.Equal(@"C:\Users\me\Videos", import.RecordingFolder); // normalized to a Windows path

        Assert.Equal("rtmp://live.twitch.tv/app", import.Stream!.ServerUrl);
        Assert.Equal("live_123_abc", import.Stream.Key.Reveal());

        Assert.Equal(new ObsCapture(ObsCaptureKind.Window, null, "Elden Ring: Nightreign", "nightreign.exe", false), import.Capture);

        var cam = Assert.Single(import.Cameras);
        Assert.Equal("Logitech BRIO", cam.DeviceName);
        // 1280x720 at 25 % anchored bottom-right: 320x180 in the corner.
        AssertRect(new NormalizedRect(1 - (320 / 1920.0), 1 - (180 / 1080.0), 320 / 1920.0, 180 / 1080.0), cam.Placement!.Value);

        Assert.Equal(2, import.Overlays.Count);
        var alerts = import.Overlays[0];
        Assert.Equal(OverlayKind.Web, alerts.Kind);
        Assert.Equal("https://reymit.ir/widget/alert?token=SECRET123", alerts.Url!.Reveal());
        Assert.Equal((1920, 1080), (alerts.PageWidth, alerts.PageHeight));
        AssertRect(new NormalizedRect(0, 0, 1, 1), alerts.Placement!.Value);
        var logo = import.Overlays[1];
        Assert.Equal("C:/Users/me/Pictures/logo.png", logo.ImagePath);
        AssertRect(new NormalizedRect(40 / 1920.0, 40 / 1080.0, 0.1, 0.1), logo.Placement!.Value);

        Assert.True(import.DesktopAudio);
        Assert.True(import.Microphone);
        Assert.Contains(import.Skipped, s => s.StartsWith("Clock: text sources", StringComparison.Ordinal));
        Assert.DoesNotContain(import.Skipped, s => s.Contains("Hidden cam", StringComparison.Ordinal));
    }

    [Fact]
    public void Advanced_output_profile_with_custom_server()
    {
        WriteObs(
            "[General]\nName=Pro\n[Video]\nBaseCX=1920\nBaseCY=1080\nOutputCX=1920\nOutputCY=1080\nFPSType=2\nFPSNum=30000\nFPSDen=1001\n[Output]\nMode=Advanced\n[AdvOut]\nEncoder=obs_x264\nTrack1Bitrate=192\nRecFilePath=D:/Recordings\n",
            """{ "type": "rtmp_custom", "settings": { "server": "rtmps://live.example.com:443/app", "key": "k-1", "use_auth": false } }""");
        Write("basic/profiles/My_Stream/streamEncoder.json", """{ "bitrate": 8000, "rate_control": "CBR" }""");
        var import = ObsImporter.Read(_root)!;

        Assert.Equal(new ObsVideo(1920, 1080, 30), import.Video);
        Assert.Equal(8000, import.VideoBitrateKbps);
        Assert.Equal(192, import.AudioBitrateKbps);
        Assert.Equal("libx264", import.EncoderId);
        Assert.Equal(@"D:\Recordings", import.RecordingFolder);
        Assert.Equal("rtmps://live.example.com:443/app", import.Stream!.ServerUrl);
        Assert.Null(import.Stream.ServiceName);
    }

    /// <summary>An exported profile folder: just basic.ini, service.json and streamEncoder.json, as OBS's Profile › Export writes them.</summary>
    [Fact]
    public void A_profile_folder_on_its_own_imports_the_profile()
    {
        // OBS escapes backslashes in paths; streams audio track 2; binds to one address.
        Write("basic.ini", (char)0xFEFF + "[General]\nName=Untitled\n\n[AdvOut]\nEncoder=obs_nvenc_h264_tex\nTrackIndex=2\nRecFilePath=C:\\\\Users\\\\MadFury\\\\Videos\nRecFormat2=hybrid_mp4\nTrack1Bitrate=160\nTrack2Bitrate=320\n\n[Output]\nMode=Advanced\nBindIP=172.20.10.10\n\n[Video]\nBaseCX=1920\nBaseCY=1080\nOutputCX=1280\nOutputCY=720\nFPSType=0\nFPSCommon=30\n");
        Write("service.json", """{"type":"rtmp_custom","settings":{"server":"rtmp://de-live2.example.com/live","use_auth":true,"bwtest":false,"key":"k-2"}}""");
        Write("streamEncoder.json", """{"rate_control":"CBR","bitrate":5000,"keyint_sec":2,"preset":"p6","multipass":"disabled"}""");
        var import = ObsImporter.Read(_root)!;

        Assert.Equal("Untitled", import.ProfileName);
        Assert.Equal(new ObsVideo(1280, 720, 30), import.Video);
        Assert.Equal(5000, import.VideoBitrateKbps);
        Assert.Equal(320, import.AudioBitrateKbps);
        Assert.Equal("h264_nvenc", import.EncoderId);
        Assert.Equal("p6", import.EncoderPreset);
        Assert.Equal(2, import.KeyframeIntervalSeconds);
        Assert.Equal(@"C:\Users\MadFury\Videos", import.RecordingFolder);
        Assert.True(import.RecordAsMp4);
        Assert.Equal("172.20.10.10", import.BindIp);
        Assert.Equal("rtmp://de-live2.example.com/live", import.Stream!.ServerUrl);
        Assert.Contains(import.Skipped, s => s.Contains("user name and password", StringComparison.Ordinal));
        Assert.Null(import.SceneName);
        Assert.Null(import.Capture);
    }

    [Fact]
    public void A_profile_folder_inside_an_install_still_finds_the_scene()
    {
        WriteObs(SimpleIni, """{ "type": "rtmp_common", "settings": { "service": "Twitch", "server": "auto", "key": "live_123_abc" } }""");
        var import = ObsImporter.Read(Path.Combine(_root, "basic", "profiles", "My_Stream") + Path.DirectorySeparatorChar)!;
        Assert.Equal("My Stream", import.ProfileName);
        Assert.Equal("Gaming", import.SceneName);
        Assert.NotNull(import.Capture);
    }

    [Fact]
    public void Simple_mode_takes_the_encoders_own_preset_and_recording_format()
    {
        WriteObs(SimpleIni + "NVENCPreset2=p4\nRecFormat2=mkv\n", """{ "type": "rtmp_custom", "settings": { "server": "rtmp://x.example.com/live", "key": "k" } }""");
        var import = ObsImporter.Read(_root)!;
        Assert.Equal("p4", import.EncoderPreset);
        Assert.Null(import.KeyframeIntervalSeconds); // simple mode leaves it automatic
        Assert.False(import.RecordAsMp4);
        Assert.Null(import.BindIp);
        Assert.Equal(@"C:\Users\me\Videos", import.RecordingFolder);
    }

    [Theory]
    [InlineData("h264_nvenc", "p6", "p6")]
    [InlineData("h264_nvenc", "P1", "p1")]
    [InlineData("h264_nvenc", "hq", null)]
    [InlineData("libx264", "veryfast", "veryfast")]
    [InlineData("libx264", "placebo", null)]
    [InlineData("h264_qsv", "balanced", "medium")]
    [InlineData("h264_qsv", "7", "veryfast")]
    [InlineData("h264_amf", "quality", "quality")]
    [InlineData("h264_amf", "p5", null)]
    [InlineData(null, "p5", null)]
    public void Maps_obs_presets(string? encoder, string obs, string? linkast) => Assert.Equal(linkast, ObsImporter.MapPreset(encoder, obs));

    [Fact]
    public void Non_rtmp_services_are_listed_as_not_imported()
    {
        WriteObs(SimpleIni, """{ "type": "whip_custom", "settings": { "server": "https://whip.example.com/", "bearer_token": "x" } }""");
        var import = ObsImporter.Read(_root)!;
        Assert.Null(import.Stream);
        Assert.Contains(import.Skipped, s => s.StartsWith("Stream server", StringComparison.Ordinal));
    }

    [Fact]
    public void Older_obs_with_global_ini_and_no_scenes_still_imports_the_profile()
    {
        Write("global.ini", "[Basic]\nProfile=Old\nProfileDir=Old\n");
        Write("basic/profiles/Old/basic.ini", "[General]\nName=Old\n[Video]\nOutputCX=1600\nOutputCY=900\nFPSType=1\nFPSInt=30\n[SimpleOutput]\nVBitrate=3000\nStreamEncoder=x264\n");
        var import = ObsImporter.Read(_root)!;
        Assert.Equal("Old", import.ProfileName);
        Assert.Equal(new ObsVideo(1600, 900, 30), import.Video);
        Assert.Null(import.Capture);
        Assert.Empty(import.Cameras);
    }

    [Fact]
    public void No_obs_folder_gives_nothing() => Assert.Null(ObsImporter.Read(Path.Combine(_root, "missing")));

    [Theory]
    [InlineData("jim_nvenc", "h264_nvenc")]
    [InlineData("obs_nvenc_h264_tex", "h264_nvenc")]
    [InlineData("obs_qsv11_v2", "h264_qsv")]
    [InlineData("h264_texture_amf", "h264_amf")]
    [InlineData("amd", "h264_amf")]
    [InlineData("x264", "libx264")]
    [InlineData("obs_nvenc_hevc_tex", null)]
    [InlineData("av1_texture_amf", null)]
    public void Maps_obs_encoders(string obs, string? linkast) => Assert.Equal(linkast, ObsImporter.MapEncoder(obs));

    [Fact]
    public void Overlay_rectangles_map_to_the_nearest_spot()
    {
        Assert.Equal((OverlayPosition.FullScreen, 1.0), OverlayGeometry.Nearest(new NormalizedRect(0, 0, 1, 1)));
        Assert.Equal((OverlayPosition.TopLeft, 0.1), OverlayGeometry.Nearest(new NormalizedRect(0.02, 0.04, 0.1, 0.1)));
        Assert.Equal((OverlayPosition.BottomRight, 0.3), OverlayGeometry.Nearest(new NormalizedRect(0.68, 0.7, 0.3, 0.28)));
        Assert.Equal((OverlayPosition.TopCenter, 0.5), OverlayGeometry.Nearest(new NormalizedRect(0.25, 0, 0.5, 0.2)));
    }

    private static void AssertRect(NormalizedRect expected, NormalizedRect actual)
    {
        Assert.Equal(expected.X, actual.X, 3);
        Assert.Equal(expected.Y, actual.Y, 3);
        Assert.Equal(expected.Width, actual.Width, 3);
        Assert.Equal(expected.Height, actual.Height, 3);
    }
}

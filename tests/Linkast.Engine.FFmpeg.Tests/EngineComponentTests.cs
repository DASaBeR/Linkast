// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core;
using Linkast.Core.Audio;
using Linkast.Engine.FFmpeg;

namespace Linkast.Engine.FFmpeg.Tests;

public class ArgumentBuilderTests
{
    private const string Url = "rtmp://127.0.0.1:50000/live/secret_key";

    private static FfmpegJob Job(VideoSourceSpec? video = null, IReadOnlyList<AudioJobInput>? audio = null, EncoderSettings? enc = null) =>
        new(video ?? new DisplaySource(1), audio ?? [], enc ?? EncoderSettings.Default1080p60("h264_nvenc"), Url, "rtmp://live.example.com/live");

    private static string ValueAfter(IReadOnlyList<string> args, string flag, int occurrence = 0)
    {
        var idx = -1;
        for (var i = 0; i <= occurrence; i++)
        {
            idx = args.ToList().IndexOf(flag, idx + 1);
        }

        Assert.True(idx >= 0, $"{flag} not found");
        return args[idx + 1];
    }

    [Fact]
    public void Publish_url_is_last_and_single_argument()
    {
        var args = FfmpegArgumentBuilder.Build(Job());
        Assert.Equal(Url, args[^1]);
        Assert.Equal("flv", ValueAfter(args, "-f", 2));
        Assert.Equal("rtmp://live.example.com/live", ValueAfter(args, "-rtmp_tcurl"));
    }

    [Fact]
    public void Progress_goes_to_stdout() => Assert.Equal("pipe:1", ValueAfter(FfmpegArgumentBuilder.Build(Job()), "-progress"));

    [Theory]
    [InlineData(CaptureApi.WindowsGraphicsCapture, null, "gfxcapture=monitor_idx=1:max_framerate=60:capture_cursor=1")]
    [InlineData(CaptureApi.WindowsGraphicsCapture, 65537L, "gfxcapture=hmonitor=65537:max_framerate=60:capture_cursor=1")]
    [InlineData(CaptureApi.DesktopDuplication, 65537L, "ddagrab=output_idx=1:framerate=60:draw_mouse=1")]
    public void Display_source_graphs(CaptureApi api, long? handle, string expected) =>
        Assert.Equal(expected, FfmpegArgumentBuilder.VideoSourceGraph(new DisplaySource(1, api, true, handle), 60));

    [Fact]
    public void Window_source_uses_hwnd() =>
        Assert.Equal("gfxcapture=hwnd=1234:max_framerate=30:capture_cursor=0:resize_mode=scale_aspect",
            FfmpegArgumentBuilder.VideoSourceGraph(new WindowSource(1234, "Some \"Game\"; title", "game.exe", false), 30));

    [Fact]
    public void Window_capture_carries_the_black_probe_and_continues_from_its_split()
    {
        var graph = ValueAfter(FfmpegArgumentBuilder.Build(Job(new WindowSource(1234, "Game", "game.exe"))), "-filter_complex");
        Assert.StartsWith("[0:v]hwdownload,format=bgra,fps=60," + BlackProbe.Graph + ";[cap]scale=1920:1080:", graph, StringComparison.Ordinal);
        Assert.Contains("nullsink;", graph, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(DisplaySource))]
    [InlineData(typeof(TestPatternSource))]
    [InlineData(typeof(BlankCanvasSource))]
    public void Other_sources_have_no_black_probe(Type source)
    {
        VideoSourceSpec video = source == typeof(DisplaySource) ? new DisplaySource(0) : (VideoSourceSpec)Activator.CreateInstance(source)!;
        Assert.DoesNotContain("blackdetect", ValueAfter(FfmpegArgumentBuilder.Build(Job(video)), "-filter_complex"), StringComparison.Ordinal);
    }

    [Fact]
    public void Window_title_never_reaches_arguments()
    {
        var args = FfmpegArgumentBuilder.Build(Job(new WindowSource(42, "evil:title;[x]", "evil.exe")));
        Assert.DoesNotContain(args, a => a.Contains("evil", StringComparison.Ordinal));
    }

    [Fact]
    public void Capture_filter_downloads_makes_cfr_and_letterboxes()
    {
        var graph = ValueAfter(FfmpegArgumentBuilder.Build(Job()), "-filter_complex");
        Assert.StartsWith("[0:v]hwdownload,format=bgra,fps=60,scale=1920:1080:flags=bicubic:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2,format=nv12[vout]", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void No_audio_adds_silent_track()
    {
        var args = FfmpegArgumentBuilder.Build(Job());
        Assert.Contains("anullsrc=r=48000:cl=stereo", args);
        Assert.EndsWith(";[a0]anull[aout]", ValueAfter(args, "-filter_complex"), StringComparison.Ordinal);
    }

    [Fact]
    public void Two_pipes_are_mixed_without_wallclock_timestamps()
    {
        var fmt = new PcmFormat(44100, 2, PcmSampleFormat.F32);
        var args = FfmpegArgumentBuilder.Build(Job(audio:
        [
            new PipeAudioJobInput(@"\\.\pipe\linkast-sys", fmt, 1),
            new PipeAudioJobInput(@"\\.\pipe\linkast-mic", new PcmFormat(48000, 1, PcmSampleFormat.S16), 0.5),
        ]));
        Assert.DoesNotContain("-use_wallclock_as_timestamps", args);
        Assert.Equal("f32le", ValueAfter(args, "-f", 1));
        Assert.Equal("44100", ValueAfter(args, "-ar", 0));
        Assert.Equal("s16le", ValueAfter(args, "-f", 2));
        Assert.Equal(@"\\.\pipe\linkast-mic", ValueAfter(args, "-i", 2));
        var graph = ValueAfter(args, "-filter_complex");
        Assert.Contains("[2:a]aresample=48000:async=1000,aformat=sample_fmts=fltp:channel_layouts=stereo,volume=0.5[a1]", graph, StringComparison.Ordinal);
        Assert.EndsWith("[a0][a1]amix=inputs=2:duration=longest:normalize=0[aout]", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void Camera_layer_is_overlaid_with_named_filters_and_zmq_and_audio_indices_shift()
    {
        var layer = new VideoLayerJobInput(@"\\.\pipe\linkast-cam-1", 480, 270, 60, new Linkast.Core.Camera.PixelRect(1400, 780, 480, 270));
        var job = Job(audio: [new PipeAudioJobInput(@"\\.\pipe\linkast-sys", new PcmFormat(48000, 2, PcmSampleFormat.F32), 1)])
            with { Layers = [layer], CommandPort = 5555 };
        var args = FfmpegArgumentBuilder.Build(job);

        Assert.Equal("rawvideo", ValueAfter(args, "-f", 1));
        Assert.Equal("bgra", ValueAfter(args, "-pix_fmt"));
        Assert.Equal("480x270", ValueAfter(args, "-video_size"));
        Assert.Equal(@"\\.\pipe\linkast-cam-1", ValueAfter(args, "-i", 1));
        Assert.Equal(@"\\.\pipe\linkast-sys", ValueAfter(args, "-i", 2));

        var graph = ValueAfter(args, "-filter_complex");
        Assert.Contains("pad=1920:1080:(ow-iw)/2:(oh-ih)/2,format=yuv420p[base]", graph, StringComparison.Ordinal);

        // Camera pipes are timestamped by the wall clock, like the live capture (regression: unbounded
        // overlay buffering when FFmpeg read the pipe slower than real time).
        var list = args.ToList();
        var wallclock = list.IndexOf("-use_wallclock_as_timestamps");
        Assert.True(wallclock >= 0 && wallclock < list.IndexOf(@"\\.\pipe\linkast-cam-1"), "Wall-clock timestamps must apply to the camera input.");
        Assert.True(wallclock > list.IndexOf(@"\\.\pipe\linkast-sys") || list.IndexOf(@"\\.\pipe\linkast-sys") > list.IndexOf(@"\\.\pipe\linkast-cam-1"), "Audio input stays unchanged.");
        Assert.Contains("[1:v]scale@cam0=w=480:h=270:flags=bicubic,format=yuva420p[cam0]", graph, StringComparison.Ordinal);
        Assert.Contains("[base][cam0]overlay@cam0=x=1400:y=780:eval=frame:format=yuv420:eof_action=pass[comp0]", graph, StringComparison.Ordinal);
        Assert.Contains(@"[comp0]zmq=bind_address=tcp\\://127.0.0.1\\:5555,format=nv12[vout]", graph, StringComparison.Ordinal);
        Assert.Contains("[2:a]aresample", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void Vertical_layout_fills_the_top_of_the_canvas_with_gameplay()
    {
        var job = Job(enc: new EncoderSettings("h264_nvenc", 1080, 1920, 60, 6000)) with
        {
            BasePlacement = new Linkast.Core.Camera.NormalizedRect(0, 0, 1, 0.56),
            BaseFit = Linkast.Core.Camera.BaseFit.Fill,
        };
        var graph = ValueAfter(FfmpegArgumentBuilder.Build(job), "-filter_complex");
        Assert.Contains("scale=1080:1074:flags=bicubic:force_original_aspect_ratio=increase,crop=1080:1074,pad=1080:1920:0:0", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void A_video_file_layer_is_looped_and_paced_by_ffmpeg()
    {
        var file = new VideoLayerJobInput(string.Empty, 1920, 1080, 0, new Linkast.Core.Camera.PixelRect(0, 0, 1920, 1080)) { FilePath = @"C:\Videos\ending.mp4" };
        var job = Job() with { Layers = [file], CommandPort = 5555 };
        var args = FfmpegArgumentBuilder.Build(job);
        var at = args.ToList().IndexOf(@"C:\Videos\ending.mp4");
        Assert.Equal(["-stream_loop", "-1", "-re", "-i"], args.Skip(at - 4).Take(4).ToArray());

        // Same place in the graph as a piped layer, and the audio index moves past it.
        var graph = ValueAfter(args, "-filter_complex");
        Assert.Contains("[1:v]scale@cam0=w=1920:h=1080", graph, StringComparison.Ordinal);
        Assert.Contains("[2:a]", graph, StringComparison.Ordinal);

        Assert.Throws<StreamFailureException>(() => FfmpegArgumentBuilder.Build(job with { Layers = [file with { FilePath = "ending.mp4" }] }));
    }

    [Fact]
    public void Trimmed_edges_crop_the_capture_before_it_is_scaled()
    {
        var job = Job() with { BaseCrop = new Linkast.Core.Camera.NormalizedRect(0.1, 0.05, 0.8, 0.9) };
        var graph = ValueAfter(FfmpegArgumentBuilder.Build(job), "-filter_complex");
        Assert.Contains("crop=w=iw*0.8:h=ih*0.9:x=iw*0.1:y=ih*0.05,scale=1920:1080", graph, StringComparison.Ordinal);

        // The whole picture needs no crop filter at all, and a crop that keeps nothing is not sent.
        Assert.DoesNotContain("crop=w=iw", ValueAfter(FfmpegArgumentBuilder.Build(Job()), "-filter_complex"), StringComparison.Ordinal);
        var tiny = Job() with { BaseCrop = new Linkast.Core.Camera.NormalizedRect(0.5, 0.5, 0.001, 0.001) };
        Assert.Contains("crop=w=iw*0.05:h=ih*0.05", ValueAfter(FfmpegArgumentBuilder.Build(tiny), "-filter_complex"), StringComparison.Ordinal);
    }

    [Fact]
    public void Fit_in_a_region_centres_inside_it()
    {
        var graph = FfmpegArgumentBuilder.FilterGraph(new DisplaySource(0), [new ToneAudioJobInput(0)], EncoderSettings.Default1080p60("libx264"), [], null,
            new Linkast.Core.Camera.NormalizedRect(0.5, 0, 0.5, 0.5), Linkast.Core.Camera.BaseFit.Fit);
        Assert.Contains("scale=960:540:flags=bicubic:force_original_aspect_ratio=decrease,pad=1920:1080:960+(960-iw)/2:0+(540-ih)/2", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void Blank_canvas_needs_no_hwdownload()
    {
        var args = FfmpegArgumentBuilder.Build(Job(new BlankCanvasSource()));
        Assert.Equal("color=c=black:s=1920x1080:r=60,realtime", ValueAfter(args, "-i"));
        Assert.DoesNotContain("hwdownload", ValueAfter(args, "-filter_complex"), StringComparison.Ordinal);
    }

    [Fact]
    public void Too_many_layers_rejected()
    {
        var l = new VideoLayerJobInput(@"\\.\pipe\linkast-cam-1", 2, 2, 30, new Linkast.Core.Camera.PixelRect(0, 0, 2, 2));
        Assert.Throws<StreamFailureException>(() => FfmpegArgumentBuilder.Build(Job() with { Layers = Enumerable.Repeat(l, FfmpegArgumentBuilder.MaxLayers + 1).ToList() }));
    }

    [Theory]
    [InlineData("h264_nvenc", "-rc", "cbr")]
    [InlineData("libx264", "-x264-params", "nal-hrd=cbr:force-cfr=1")]
    [InlineData("h264_mf", "-rate_control", "cbr")]
    public void Encoder_specific_cbr_args(string encoder, string flag, string value)
    {
        var args = FfmpegArgumentBuilder.VideoEncoderArgs(EncoderSettings.Default720p60(encoder));
        Assert.Equal(value, ValueAfter(args, flag));
        Assert.Equal("4500k", ValueAfter(args, "-b:v"));
        Assert.Equal("120", ValueAfter(args, "-g"));
    }

    [Theory]
    [InlineData("rtmp://host/app/key; rm -rf")]
    [InlineData("-y")]
    [InlineData("file:///c:/x")]
    [InlineData("http://host/app/key")]
    public void Rejects_unsafe_publish_urls(string url) =>
        Assert.Throws<StreamFailureException>(() => FfmpegArgumentBuilder.Build(Job() with { PublishUrl = url }));

    [Fact]
    public void Rejects_foreign_pipe_paths() =>
        Assert.Throws<StreamFailureException>(() => FfmpegArgumentBuilder.Build(Job(audio:
            [new PipeAudioJobInput(@"C:\Windows\win.ini", new PcmFormat(48000, 2, PcmSampleFormat.F32), 1)])));
}

public class ProgressParserTests
{
    [Fact]
    public void Parses_a_complete_block()
    {
        var p = new ProgressParser();
        string[] lines =
        [
            "frame=600", "fps=59.94", "stream_0_0_q=23.0", "bitrate=6012.3kbits/s", "total_size=7516192",
            "out_time_us=10000000", "out_time_ms=10000000", "out_time=00:00:10.000000", "dup_frames=3", "drop_frames=1", "speed=1.01x",
        ];
        foreach (var l in lines)
        {
            Assert.Null(p.Feed(l));
        }

        var s = p.Feed("progress=continue");
        Assert.NotNull(s);
        Assert.Equal(600, s.Frame);
        Assert.Equal(59.94, s.Fps, 3);
        Assert.Equal(6012.3, s.BitrateKbps, 3);
        Assert.Equal(TimeSpan.FromSeconds(10), s.OutTime);
        Assert.Equal(3, s.DuplicatedFrames);
        Assert.Equal(1, s.DroppedFrames);
        Assert.Equal(1.01, s.Speed, 3);
        Assert.False(s.IsEnd);
    }

    [Fact]
    public void Handles_na_values_and_end()
    {
        var p = new ProgressParser();
        p.Feed("bitrate=N/A");
        p.Feed("speed=N/A");
        p.Feed("out_time_us=N/A");
        var s = p.Feed("progress=end");
        Assert.NotNull(s);
        Assert.Equal(0, s.BitrateKbps);
        Assert.Equal(TimeSpan.Zero, s.OutTime);
        Assert.True(s.IsEnd);
    }

    [Fact]
    public void Ignores_garbage() => Assert.Null(new ProgressParser().Feed("not a key value"));
}

public class BlackProbeTests
{
    [Theory]
    [InlineData("lavfi.black_start=0", true)]
    [InlineData("lavfi.black_start=12.5\r", true)]
    [InlineData("lavfi.black_end=3", false)]
    [InlineData("frame:6    pts:6       pts_time:3", null)]
    [InlineData("[flv @ 01] [warning] lavfi.black_start=1", null)]
    public void Parses_the_marks(string line, bool? expected) => Assert.Equal(expected, BlackProbe.Parse(line));

    [Theory]
    [InlineData("frame:0    pts:0       pts_time:0", true)]
    [InlineData("lavfi.black_end=3", true)]
    [InlineData("[h264_nvenc @ 01] [error] frame: something failed", false)]
    [InlineData("frame=  120 fps= 60", false)]
    public void Knows_its_own_lines(string line, bool expected) => Assert.Equal(expected, BlackProbe.IsProbeLine(line));
}

public class ErrorClassifierTests
{
    [Theory]
    [InlineData("[h264_nvenc @ 0000020] [error] No NVENC capable devices found", ErrorCategory.Encoder, "encoder.nvenc.unavailable")]
    [InlineData("[tcp @ 000001] [error] Connection to tcp://127.0.0.1:1935 failed: Connection refused", ErrorCategory.Network, "network.refused")]
    [InlineData("[rtmp @ 000001] [error] Server error: Invalid stream key", ErrorCategory.Network, "network.rejected")]
    [InlineData("[out#0/flv @ 0001] [error] Error muxing a packet", ErrorCategory.Network, "network.reset")]
    [InlineData("[Parsed_ddagrab_0 @ 01] [error] DuplicateOutput failed", ErrorCategory.Capture, "capture.dda")]
    [InlineData("[Parsed_ddagrab_0 @ 01] [error] AcquireNextFrame failed: 887a0026", ErrorCategory.Capture, "capture.dda.lost")]
    [InlineData("[Parsed_ddagrab_0 @ 01] [error] Output parameters changed!", ErrorCategory.Capture, "capture.dda.lost")]
    [InlineData("[Parsed_ddagrab_0 @ 01] [error] Desktop duplication access denied", ErrorCategory.Capture, "capture.dda.lost")]
    [InlineData("[Parsed_ddagrab_0 @ 01] [error] Selected output not supported", ErrorCategory.Capture, "capture.dda.unsupported")]
    [InlineData("[Parsed_ddagrab_0 @ 01] [error] AcquireNextFrame failed: 887a0005", ErrorCategory.Capture, "capture.dda")]
    [InlineData("[AVFilterGraph @ 01] [error] No such filter: 'foo'", ErrorCategory.Configuration, "config.ffmpeg")]
    [InlineData(@"[in#1 @ 01] [error] Error opening input \\.\pipe\linkast-abc: Broken pipe", ErrorCategory.Audio, "audio.pipe")]
    public void Classifies_known_errors(string line, ErrorCategory category, string code)
    {
        var e = ErrorClassifier.Classify(line);
        Assert.NotNull(e);
        Assert.Equal(category, e.Category);
        Assert.Equal(code, e.Code);
        Assert.Equal(line, e.Detail);
    }

    [Fact]
    public void Ignores_warnings() =>
        Assert.Null(ErrorClassifier.Classify("[flv @ 01] [warning] Connection refused (not really)"));

    [Fact]
    public void Exit_prefers_specific_root_cause_over_generic_io()
    {
        var e = ErrorClassifier.ClassifyExit(1,
        [
            "[out#0 @ 1] [error] Error muxing a packet",
            "[h264_nvenc @ 2] [error] OpenEncodeSessionEx failed: out of memory",
        ]);
        Assert.Equal("encoder.nvenc.session", e.Code);
    }

    [Fact]
    public void Lost_display_is_found_behind_the_demuxer_error_that_follows_it()
    {
        var e = ErrorClassifier.ClassifyExit(1,
        [
            "[Parsed_ddagrab_0 @ 1] [error] AcquireNextFrame failed: 887a0026",
            "[in#0/lavfi @ 2] [error] Error during demuxing: Generic error in an external library",
            "[out#0/flv @ 3] [error] Error muxing a packet",
        ]);
        Assert.Equal(ErrorClassifier.CaptureLostCode, e.Code);
        Assert.True(e.IsRetryable);
    }

    [Theory]
    [InlineData("capture.dda.lost", true)]
    [InlineData("capture.dda", false)]
    [InlineData("capture.dda.unsupported", false)]
    [InlineData("capture.wgc", false)]
    public void Only_a_lost_display_is_retried_among_capture_errors(string code, bool retried) =>
        Assert.Equal(retried, new StreamError(ErrorCategory.Capture, code, "x").IsRetryable);

    [Fact]
    public void Capture_restarts_start_fast_and_wait_out_a_uac_prompt()
    {
        var b = new ReconnectBackoff(new EngineOptions().CaptureRestart);
        var delays = new List<TimeSpan>();
        while (b.NextDelay() is { } d)
        {
            delays.Add(d);
        }

        Assert.Equal(TimeSpan.FromMilliseconds(250), delays[0]);
        Assert.InRange(delays.Sum(d => d.TotalSeconds), 30, 60);
    }

    [Fact]
    public void Exit_without_match_is_process_error()
    {
        var e = ErrorClassifier.ClassifyExit(-5, ["[x @ 1] [warning] something"]);
        Assert.Equal(ErrorCategory.Process, e.Category);
        Assert.Contains("-5", e.UserMessage, StringComparison.Ordinal);
    }
}

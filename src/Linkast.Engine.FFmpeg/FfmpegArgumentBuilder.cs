// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using Linkast.Core;
using Linkast.Core.Audio;

namespace Linkast.Engine.FFmpeg;

/// <summary>
/// Turns a <see cref="FfmpegJob"/> into an argument vector for <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>.
/// No shell is involved and no user-entered text is placed inside a filter graph: every value that
/// reaches a filter string is a number or an enum mapped to a constant.
/// </summary>
public static class FfmpegArgumentBuilder
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static IReadOnlyList<string> Build(FfmpegJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var errors = new List<StreamError>();
        SessionValidator.ValidateEncoder(job.Encoder, errors);
        if (errors.Count > 0)
        {
            throw new StreamFailureException(errors[0]);
        }

        if (job.PublishUrl is null && job.RecordingPath is null)
        {
            throw new StreamFailureException(new StreamError(ErrorCategory.Configuration, "output.none", "There is no output: neither a stream nor a recording."));
        }

        if (job.PublishUrl is not null)
        {
            ValidatePublishUrl(job.PublishUrl);
        }

        if (job.TcUrl is not null)
        {
            ValidatePublishUrl(job.TcUrl);
        }

        if (job.RecordingPath is not null)
        {
            ValidateRecordingPath(job.RecordingPath);
        }

        var e = job.Encoder;
        var args = new List<string>
        {
            "-hide_banner",
            "-nostats",
            "-loglevel", "level+warning",
            "-progress", "pipe:1",
            "-stats_period", "1",
        };

        // Input 0: base video.
        args.AddRange(["-thread_queue_size", "64", "-f", "lavfi", "-i", VideoSourceGraph(job.Video, e.Fps, e.Width, e.Height)]);

        // Inputs 1..L: rendered camera layers (raw BGRA with alpha), timestamped by frame count.
        if (job.Layers.Count > MaxLayers)
        {
            throw new StreamFailureException(new StreamError(ErrorCategory.Configuration, "layers.count", $"At most {MaxLayers} layers (cameras and overlays) are supported."));
        }

        foreach (var layer in job.Layers)
        {
            ValidatePipePath(layer.PipePath);

            // Wall-clock timestamps put the camera on the same clock as the live capture. With
            // frame-count timestamps, any moment FFmpeg read the pipe slower than real time made the
            // camera's timeline lag, and the overlay buffered gameplay frames without limit while
            // waiting for it (FFmpeg grew to 9.6 GB in a 30-minute soak).
            args.AddRange([
                "-thread_queue_size", "64",
                "-use_wallclock_as_timestamps", "1",
                "-f", "rawvideo",
                "-pix_fmt", "bgra",
                "-video_size", $"{layer.Width.ToString(Inv)}x{layer.Height.ToString(Inv)}",
                "-framerate", layer.FrameRate.ToString(Inv),
                "-i", layer.PipePath,
            ]);
        }

        // Inputs L+1..N: audio. With no audio we still send a silent track; most ingests require one.
        var audio = job.Audio.Count > 0 ? job.Audio : [new ToneAudioJobInput(0)];
        foreach (var a in audio)
        {
            switch (a)
            {
                case PipeAudioJobInput p:
                    ValidatePipePath(p.PipePath);

                    // Timestamps come from the sample count. The pipe writer keeps the stream locked to
                    // wall-clock rate (pads silence when behind, drops when ahead), so no wall-clock
                    // timestamps are needed; they would also not be rebased to zero (epoch-sized PTS).
                    args.AddRange([
                        "-thread_queue_size", "1024",
                        "-f", PcmDemuxer(p.Format.SampleFormat),
                        "-ar", p.Format.SampleRate.ToString(Inv),
                        "-ac", p.Format.Channels.ToString(Inv),
                        "-i", p.PipePath,
                    ]);
                    break;
                case ToneAudioJobInput t:
                    var src = t.Gain == 0
                        ? $"anullsrc=r={e.AudioSampleRate.ToString(Inv)}:cl=stereo"
                        : $"sine=frequency=440:sample_rate={e.AudioSampleRate.ToString(Inv)}";
                    args.AddRange(["-f", "lavfi", "-i", src]);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported audio input {a.GetType().Name}.");
            }
        }

        args.AddRange(["-filter_complex", FilterGraph(job.Video, audio, e, job.Layers, job.CommandPort, job.BasePlacement, job.BaseFit)]);
        args.AddRange(["-map", "[vout]", "-map", "[aout]"]);
        args.AddRange(VideoEncoderArgs(e));
        args.AddRange([
            "-c:a", "aac",
            "-b:a", Kbps(e.AudioKbps),
            "-ar", e.AudioSampleRate.ToString(Inv),
            "-ac", "2",
        ]);
        args.AddRange(OutputArgs(job.PublishUrl, job.TcUrl, job.RecordingPath));
        return args;
    }

    public const int MaxLayers = 8;

    /// <summary>
    /// The output: FLV to the relay, a Matroska file, or both through the <c>tee</c> muxer so the
    /// picture is encoded once. A failing recording (disk full) never stops the stream; a failing
    /// stream ends the attempt as before, and the reconnect starts a new file.
    /// </summary>
    private static List<string> OutputArgs(string? publishUrl, string? tcUrl, string? recordingPath)
    {
        if (recordingPath is null)
        {
            List<string> flv = ["-f", "flv", "-flvflags", "no_duration_filesize"];
            if (tcUrl is not null)
            {
                flv.AddRange(["-rtmp_tcurl", tcUrl]);
            }

            flv.Add(publishUrl!);
            return flv;
        }

        if (publishUrl is null)
        {
            return ["-f", "matroska", recordingPath];
        }

        var stream = new StringBuilder("[f=flv:flvflags=no_duration_filesize");
        if (tcUrl is not null)
        {
            stream.Append(":rtmp_tcurl=").Append(TeeEscape(TeeOptionEscape(tcUrl)));
        }

        stream.Append(']').Append(TeeEscape(publishUrl));
        var file = "[f=matroska:onfail=ignore]" + TeeEscape(recordingPath);

        // Both formats need the codec headers up front, which the tee muxer can't ask for per output.
        return ["-flags", "+global_header", "-f", "tee", stream + "|" + file];
    }

    private const char Backslash = (char)0x5C;
    private const char Quote = (char)0x27;

    /// <summary>Escapes a tee slave option value (the second parsing level: ':' ']' '=' end a value).</summary>
    internal static string TeeOptionEscape(string value) => EscapeChars(value, ":]=");

    /// <summary>Escapes a whole tee slave (the first parsing level: '|' separates slaves).</summary>
    internal static string TeeEscape(string value) => EscapeChars(value, "|");

    private static string EscapeChars(string value, string specials)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (c == Backslash || c == Quote || specials.Contains(c, StringComparison.Ordinal))
            {
                sb.Append(Backslash);
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>The lavfi source graph for input 0.</summary>
    public static string VideoSourceGraph(VideoSourceSpec video, int fps, int width = 1280, int height = 720)
    {
        var f = fps.ToString(Inv);
        return video switch
        {
            // Generated pictures are paced to real time; otherwise FFmpeg makes them as fast as it can and
            // sends a platform several times the bitrate (captures are paced by the screen already).
            BlankCanvasSource => $"color=c=black:s={width.ToString(Inv)}x{height.ToString(Inv)}:r={f},realtime",
            DisplaySource { Api: CaptureApi.DesktopDuplication } d =>
                $"ddagrab=output_idx={d.MonitorIndex.ToString(Inv)}:framerate={f}:draw_mouse={Bool(d.CaptureCursor)}",
            DisplaySource d when d.MonitorHandle is { } h =>
                $"gfxcapture=hmonitor={h.ToString(Inv)}:max_framerate={f}:capture_cursor={Bool(d.CaptureCursor)}",
            DisplaySource d =>
                $"gfxcapture=monitor_idx={d.MonitorIndex.ToString(Inv)}:max_framerate={f}:capture_cursor={Bool(d.CaptureCursor)}",
            WindowSource w =>
                $"gfxcapture=hwnd={w.WindowHandle.ToString(Inv)}:max_framerate={f}:capture_cursor={Bool(w.CaptureCursor)}:resize_mode=scale_aspect",
            TestPatternSource => $"testsrc2=size=1280x720:rate={f},realtime",
            _ => throw new NotSupportedException($"Unsupported video source {video.GetType().Name}."),
        };
    }

    public static string FilterGraph(VideoSourceSpec video, IReadOnlyList<AudioJobInput> audio, EncoderSettings e) =>
        FilterGraph(video, audio, e, [], null);

    public static string FilterGraph(
        VideoSourceSpec video,
        IReadOnlyList<AudioJobInput> audio,
        EncoderSettings e,
        IReadOnlyList<VideoLayerJobInput> layers,
        int? commandPort,
        Linkast.Core.Camera.NormalizedRect? basePlacement = null,
        Linkast.Core.Camera.BaseFit baseFit = Linkast.Core.Camera.BaseFit.Fit)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(layers);
        var w = e.Width.ToString(Inv);
        var h = e.Height.ToString(Inv);
        var sb = new StringBuilder();

        // Capture sources produce D3D11 frames; download once, then make constant frame rate
        // (WGC only emits frames on change) and letterbox into the target size.
        sb.Append("[0:v]");
        if (video is not (TestPatternSource or BlankCanvasSource))
        {
            sb.Append("hwdownload,format=bgra,");
        }

        sb.Append(Inv, $"fps={e.Fps},");
        if (video is WindowSource)
        {
            sb.Append(BlackProbe.Graph).Append(";[cap]");
        }

        var place =basePlacement ?? new Linkast.Core.Camera.NormalizedRect(0, 0, 1, 1);
        var full = place is { X: 0, Y: 0, Width: 1, Height: 1 };
        if (full && baseFit == Linkast.Core.Camera.BaseFit.Fit)
        {
            sb.Append(Inv, $"scale={w}:{h}:flags=bicubic:force_original_aspect_ratio=decrease,");
            sb.Append(Inv, $"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2");
        }
        else
        {
            // Gameplay in part of the canvas (for example the top of a vertical layout).
            var r = Linkast.Core.Camera.LayerGeometry.ToPixels(place, e.Width, e.Height);
            if (baseFit == Linkast.Core.Camera.BaseFit.Fill)
            {
                sb.Append(Inv, $"scale={r.Width}:{r.Height}:flags=bicubic:force_original_aspect_ratio=increase,");
                sb.Append(Inv, $"crop={r.Width}:{r.Height},");
                sb.Append(Inv, $"pad={w}:{h}:{r.X}:{r.Y}");
            }
            else
            {
                sb.Append(Inv, $"scale={r.Width}:{r.Height}:flags=bicubic:force_original_aspect_ratio=decrease,");
                sb.Append(Inv, $"pad={w}:{h}:{r.X}+({r.Width}-iw)/2:{r.Y}+({r.Height}-ih)/2");
            }
        }

        if (layers.Count == 0 && commandPort is null)
        {
            sb.Append(Inv, $",format={PixelFormat(e.EncoderId)}[vout]");
        }
        else
        {
            // Each layer: scale@camI (resizable live) then overlay@camI (movable live). Names are what
            // FfmpegCommandChannel addresses through the zmq filter. Blending happens in YUV 4:2:0:
            // a BGRA alpha blend of a large camera (for example 844 × 844 in a vertical layout) costs
            // about three times the CPU and could not keep 1080 × 1920 at 60 fps.
            sb.Append(",format=yuv420p[base]");
            var below = "base";
            for (var i = 0; i < layers.Count; i++)
            {
                var l = layers[i];
                var p = l.Placement;
                sb.Append(Inv, $";[{i + 1}:v]scale@cam{i}=w={p.Width}:h={p.Height}:flags=bicubic,format=yuva420p[cam{i}]");
                sb.Append(Inv, $";[{below}][cam{i}]overlay@cam{i}=x={p.X}:y={p.Y}:eval=frame:format=yuv420:eof_action=pass[comp{i}]");
                below = $"comp{i}";
            }

            sb.Append(Inv, $";[{below}]");
            if (commandPort is { } port)
            {
                // Two escaping levels: '\:' for the option parser, doubled for the graph parser.
                sb.Append(Inv, $@"zmq=bind_address=tcp\\://127.0.0.1\\:{port},");
            }

            sb.Append(Inv, $"format={PixelFormat(e.EncoderId)}[vout]");
        }

        var sr = e.AudioSampleRate.ToString(Inv);
        var firstAudio = 1 + layers.Count;
        for (var i = 0; i < audio.Count; i++)
        {
            var a = audio[i];
            var gain = Math.Clamp(a.Gain, 0, 4).ToString("0.###", Inv);
            var at = a.Name is { } name ? "@" + name : string.Empty;
            sb.Append(Inv, $";[{firstAudio + i}:a]aresample={sr}:async=1000,aformat=sample_fmts=fltp:channel_layouts=stereo,");
            if (a.NoiseSuppression is { } denoise)
            {
                sb.Append(Inv, $"afftdn{at}=nr={NoiseReduction(denoise)}:nf=-40,");
            }

            sb.Append(Inv, $"volume{at}={gain}[a{i}]");
        }

        if (audio.Count == 1)
        {
            sb.Append(";[a0]anull[aout]");
        }
        else
        {
            sb.Append(';');
            for (var i = 0; i < audio.Count; i++)
            {
                sb.Append(Inv, $"[a{i}]");
            }

            sb.Append(Inv, $"amix=inputs={audio.Count}:duration=longest:normalize=0[aout]");
        }

        return sb.ToString();
    }

    /// <summary>
    /// afftdn noise reduction in dB: 12 when on (clearly removes fan and hum without making speech
    /// sound underwater); 0.01, the filter's minimum, when off so it can be switched on live.
    /// </summary>
    public static string NoiseReduction(bool enabled) => enabled ? "12" : "0.01";

    public static IReadOnlyList<string> VideoEncoderArgs(EncoderSettings e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var gop = (e.Fps * e.KeyframeIntervalSeconds).ToString(Inv);
        var rate = Kbps(e.VideoKbps);
        var cbr = e.RateControl == RateControl.ConstantBitrate;
        var max = cbr ? rate : Kbps(e.VideoKbps * 3 / 2);
        var buf = cbr ? rate : Kbps(e.VideoKbps * 2);

        var args = new List<string> { "-c:v", e.EncoderId };
        switch (e.EncoderId)
        {
            case "h264_nvenc":
                args.AddRange(["-preset", e.Preset ?? "p5", "-tune", "hq", "-rc", cbr ? "cbr" : "vbr", "-profile:v", "high", "-bf", "2"]);
                break;
            case "h264_amf":
                args.AddRange(["-usage", "transcoding", "-quality", e.Preset ?? "balanced", "-rc", cbr ? "cbr" : "vbr_peak", "-profile:v", "high"]);
                break;
            case "h264_qsv":
                args.AddRange(["-preset", e.Preset ?? "veryfast", "-profile:v", "high", "-look_ahead", "0"]);
                break;
            case "h264_mf":
                args.AddRange(["-rate_control", cbr ? "cbr" : "u_vbr", "-scenario", "live_streaming"]);
                break;
            case "libx264":
                args.AddRange(["-preset", e.Preset ?? "veryfast", "-profile:v", "high", "-sc_threshold", "0"]);
                if (cbr)
                {
                    args.AddRange(["-x264-params", "nal-hrd=cbr:force-cfr=1", "-minrate", rate]);
                }

                break;
            default:
                if (e.Preset is not null)
                {
                    args.AddRange(["-preset", e.Preset]);
                }

                break;
        }

        args.AddRange(["-b:v", rate, "-maxrate", max, "-bufsize", buf, "-g", gop, "-keyint_min", gop]);
        return args;
    }

    public static string PixelFormat(string encoderId) => encoderId == "libx264" ? "yuv420p" : "nv12";

    public static string PcmDemuxer(PcmSampleFormat format) => format switch
    {
        PcmSampleFormat.F32 => "f32le",
        PcmSampleFormat.S16 => "s16le",
        PcmSampleFormat.S24 => "s24le",
        PcmSampleFormat.S32 => "s32le",
        _ => throw new NotSupportedException(),
    };

    private static void ValidateRecordingPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)
            || path.Any(char.IsControl) || path.Contains('|', StringComparison.Ordinal) || path.StartsWith('-'))
        {
            throw new StreamFailureException(new StreamError(ErrorCategory.Configuration, "recording.path", "The recording file name is not valid."));
        }
    }

    private static void ValidatePublishUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "rtmp" && u.Scheme != "rtmps")
            || url.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || url.StartsWith('-'))
        {
            throw new StreamFailureException(new StreamError(ErrorCategory.Configuration, "destination.url", "The output URL is not a valid RTMP URL."));
        }
    }

    private static void ValidatePipePath(string path)
    {
        if (!path.StartsWith(@"\\.\pipe\linkast-", StringComparison.Ordinal) || path.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            throw new StreamFailureException(new StreamError(ErrorCategory.Audio, "audio.pipe", "Internal audio pipe name is invalid."));
        }
    }

    private static string Kbps(int kbps) => kbps.ToString(Inv) + "k";

    private static string Bool(bool b) => b ? "1" : "0";
}

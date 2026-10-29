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

        // Input 0: the canvas, paced to real time. Everything, including the captured game, is drawn
        // on it as a layer, so every part of a scene can be moved, refitted or hidden live.
        args.AddRange(["-thread_queue_size", "64", "-f", "lavfi", "-i", VideoSourceGraph(new BlankCanvasSource(), e.Fps, e.Width, e.Height)]);

        // Inputs 1..C: the captured games, windows or displays (one per scene that has its own).
        var captures = job.AllCaptures;
        if (captures.Count > MaxCaptures)
        {
            throw new StreamFailureException(new StreamError(ErrorCategory.Configuration, "captures.count", $"At most {MaxCaptures} different games, windows or displays can be in one stream. Let some scenes share one."));
        }

        foreach (var capture in captures)
        {
            args.AddRange(["-thread_queue_size", "64", "-f", "lavfi", "-i", VideoSourceGraph(capture.Source, e.Fps, e.Width, e.Height, capture.SourceSize)]);
        }

        // Then the layers: scene media files, and rendered cameras and overlays (raw BGRA with alpha).
        if (job.Layers.Count > MaxLayers)
        {
            throw new StreamFailureException(new StreamError(ErrorCategory.Configuration, "layers.count", $"At most {MaxLayers} layers (cameras, overlays and scene pictures) are supported."));
        }

        foreach (var layer in job.Layers)
        {
            if (layer.AudioName is { } audioName)
            {
                ValidateChannelName(audioName);
            }

            switch (layer.Kind)
            {
                case LayerInputKind.LoopingVideo:
                    ValidateMediaFilePath(layer.FilePath);
                    args.AddRange(["-thread_queue_size", "64", "-stream_loop", "-1", "-re", "-i", layer.FilePath!]);
                    continue;
                case LayerInputKind.Still:
                    // Read once; the overlay repeats the last picture for ever (eof_action=repeat).
                    ValidateMediaFilePath(layer.FilePath);
                    args.AddRange(["-i", layer.FilePath!]);
                    continue;
            }

            ValidatePipePath(layer.PipePath);

            // Frame-count timestamps: the pipe writer sends exactly one frame per frame interval of real
            // time from when FFmpeg connects (catching up with repeats, never skipping), so frame n is
            // the picture at n / rate seconds. Not -use_wallclock_as_timestamps: that stamps a frame when
            // FFmpeg reads it, and FFmpeg pauses reading an input that is ahead of the output, so as soon
            // as encoding ran slightly slower than real time the reads bunched up, most frames shared a
            // time, and the camera showed 1–2 fps on stream (tester report, 1.0.0).
            args.AddRange([
                "-thread_queue_size", "64",
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

        args.AddRange(["-filter_complex", FilterGraph(captures, audio, e, job.Layers, job.CommandPort, job.Stack)]);
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

    public const int MaxLayers = 16;

    /// <summary>Each capture runs all the time (a hidden one too), so their number is kept small.</summary>
    public const int MaxCaptures = 4;

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

    /// <summary>
    /// The size a captured window of <paramref name="sourceWidth"/> × <paramref name="sourceHeight"/> is scaled
    /// to on the GPU before FFmpeg copies it to the CPU, or null to keep it. Every frame is copied, so a
    /// 1440p game into a 1080p stream copied 78 % more than the stream can show, and that copy competes
    /// with the game for the GPU. The aspect ratio is kept (trims stay right) and the result still covers
    /// the canvas, so the picture filling the stream loses nothing.
    /// </summary>
    public static (int Width, int Height)? GpuScaledSize(int sourceWidth, int sourceHeight, int canvasWidth, int canvasHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || canvasWidth <= 0 || canvasHeight <= 0)
        {
            return null;
        }

        var f = Math.Max((double)canvasWidth / sourceWidth, (double)canvasHeight / sourceHeight);
        if (f >= 0.95)
        {
            return null; // not worth a scale (and never upscale)
        }

        static int Even(double v) => Math.Max(2, (int)Math.Round(v / 2) * 2);
        return (Even(sourceWidth * f), Even(sourceHeight * f));
    }

    /// <summary>The lavfi source graph for input 0.</summary>
    /// <param name="sourceSize">The captured window's size, when known: a larger one is scaled down on the GPU.</param>
    public static string VideoSourceGraph(VideoSourceSpec video, int fps, int width = 1280, int height = 720, (int Width, int Height)? sourceSize = null)
    {
        var f = fps.ToString(Inv);
        var gpuScale = video is WindowSource && sourceSize is { } s && GpuScaledSize(s.Width, s.Height, width, height) is { } t
            ? $":width={t.Width.ToString(Inv)}:height={t.Height.ToString(Inv)}"
            : string.Empty;
        return video switch
        {
            // Generated pictures are paced to real time; otherwise FFmpeg makes them as fast as it can and
            // sends a platform several times the bitrate (captures are paced by the screen already).
            // The canvas is the clock every layer is synced to, so after a stall (FFmpeg waits several
            // seconds for the server while opening the output) it must catch up, not restart its timer:
            // a reset left it seconds behind the wall-clock camera and web pipes, and the overlay then
            // held each camera frame back by that much (camera seconds late and at a few fps).
            BlankCanvasSource => $"color=c=black:s={width.ToString(Inv)}x{height.ToString(Inv)}:r={f},realtime=limit={RealtimeCatchUpSeconds}",
            DisplaySource { Api: CaptureApi.DesktopDuplication } d =>
                $"ddagrab=output_idx={d.MonitorIndex.ToString(Inv)}:framerate={f}:draw_mouse={Bool(d.CaptureCursor)}",
            DisplaySource d when d.MonitorHandle is { } h =>
                $"gfxcapture=hmonitor={h.ToString(Inv)}:max_framerate={f}:capture_cursor={Bool(d.CaptureCursor)}",
            DisplaySource d =>
                $"gfxcapture=monitor_idx={d.MonitorIndex.ToString(Inv)}:max_framerate={f}:capture_cursor={Bool(d.CaptureCursor)}",
            WindowSource w =>
                $"gfxcapture=hwnd={w.WindowHandle.ToString(Inv)}:max_framerate={f}:capture_cursor={Bool(w.CaptureCursor)}:resize_mode=scale_aspect{gpuScale}",
            TestPatternSource => $"testsrc2=size=1280x720:rate={f},realtime=limit={RealtimeCatchUpSeconds}",
            _ => throw new NotSupportedException($"Unsupported video source {video.GetType().Name}."),
        };
    }

    /// <summary>
    /// The realtime filter's discontinuity limit. FFmpeg's default (2 s) resets the timer after a longer
    /// stall instead of catching up. A minute covers any startup wait; a longer stall ends the attempt
    /// (stall timeout) long before it matters.
    /// </summary>
    private const int RealtimeCatchUpSeconds = 60;

    /// <summary>True when the video source has a captured picture; a blank canvas has none.</summary>
    public static bool HasCapture(VideoSourceSpec video) => video is not BlankCanvasSource;

    public static string FilterGraph(VideoSourceSpec video, IReadOnlyList<AudioJobInput> audio, EncoderSettings e) =>
        FilterGraph(video, audio, e, [], null);

    /// <summary>
    /// The whole graph. Input 0 is the canvas, input 1 the capture (if any), then one input per layer,
    /// then the audio inputs. The capture and every layer are overlaid with named filters
    /// (<c>overlay@base</c>, <c>scale@camI</c> / <c>overlay@camI</c>) that the command channel moves live.
    /// </summary>
    public static string FilterGraph(
        VideoSourceSpec video,
        IReadOnlyList<AudioJobInput> audio,
        EncoderSettings e,
        IReadOnlyList<VideoLayerJobInput> layers,
        int? commandPort,
        Linkast.Core.Camera.NormalizedRect? basePlacement = null,
        Linkast.Core.Camera.BaseFit baseFit = Linkast.Core.Camera.BaseFit.Fit,
        Linkast.Core.Camera.NormalizedRect? baseCrop = null,
        bool showCapture = true)
    {
        ArgumentNullException.ThrowIfNull(video);
        IReadOnlyList<CaptureJobInput> captures = HasCapture(video)
            ? [new CaptureJobInput(video, basePlacement ?? Linkast.Core.Camera.NormalizedRect.Full, baseCrop ?? Linkast.Core.Camera.NormalizedRect.Full, baseFit, !showCapture)]
            : [];
        return FilterGraph(captures, audio, e, layers, commandPort);
    }

    /// <summary>
    /// The whole graph for any number of captures: input 0 is the canvas, inputs 1..C the captures, then
    /// the layers, then the audio. Capture k is placed by <c>crop@base{k}</c>, <c>scale@base{k}</c> and
    /// <c>overlay@base{k}</c> (k = 0 is plain <c>base</c>), so a scene switch moves one in and the others out.
    /// Scene media lie on the canvas first; captures and the other layers follow in <paramref name="stack"/>
    /// order (default: captures, then layers). Filter names don't depend on the order.
    /// </summary>
    public static string FilterGraph(
        IReadOnlyList<CaptureJobInput> captures,
        IReadOnlyList<AudioJobInput> audio,
        EncoderSettings e,
        IReadOnlyList<VideoLayerJobInput> layers,
        int? commandPort,
        IReadOnlyList<StackEntry>? stack = null)
    {
        ArgumentNullException.ThrowIfNull(captures);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(layers);
        var sb = new StringBuilder();

        // Blending happens in YUV 4:2:0: a BGRA alpha blend of a large layer (for example 844 × 844 in
        // a vertical layout) costs about three times the CPU and could not keep 1080 × 1920 at 60 fps.
        sb.Append("[0:v]format=yuv420p[canvas];");
        var below = "canvas";
        var nextInput = 1 + captures.Count;

        // The black probe watches the first captured window, before its trim.
        var probeAt = -1;
        for (var k = 0; k < captures.Count && probeAt < 0; k++)
        {
            probeAt = captures[k].Source is WindowSource ? k : -1;
        }

        foreach (var entry in StackOrder(captures.Count, layers, stack))
        {
            if (entry.IsCapture)
            {
                AppendCapture(entry.Index);
            }
            else
            {
                AppendLayer(entry.Index);
            }
        }

        void AppendCapture(int k)
        {
            var capture = captures[k];
            var n = BaseLayerArgs.Name(k);
            var suffix = k == 0 ? string.Empty : k.ToString(Inv);

            // Capture sources produce D3D11 frames; download once, then make constant frame rate (WGC
            // only emits frames on change).
            sb.Append(Inv, $"[{1 + k}:v]");
            if (capture.Source is not TestPatternSource)
            {
                sb.Append("hwdownload,format=bgra,");
            }

            // The probe branches off before the constant frame rate, so it sees the frames Windows delivered.
            if (k == probeAt)
            {
                sb.Append(BlackProbe.Graph).Append(";[cap]");
            }

            sb.Append(Inv, $"fps={e.Fps},");

            var b = BaseLayerArgs.For(capture.Placement, capture.Crop, capture.Fit, e.Width, e.Height, hidden: capture.Hidden);

            // Quoted because the expressions contain commas (min(a,b)), which separate filters otherwise.
            sb.Append(Inv, $"crop@{n}=w='{b.CropW}':h='{b.CropH}':x='{b.CropX}':y='{b.CropY}',");
            sb.Append(Inv, $"scale@{n}=w='{b.ScaleW}':h='{b.ScaleH}':flags=bicubic,format=yuva420p[game{suffix}];");
            sb.Append(Inv, $"[{below}][game{suffix}]overlay@{n}=x='{b.OverlayX}':y='{b.OverlayY}':eval=frame:format=yuv420[{n}];");
            below = n;
        }

        // Each layer: scale@camI (resizable live) then overlay@camI (movable live). Names are what
        // FfmpegCommandChannel addresses through the zmq filter. Scene media cover the canvas at a
        // fixed size and are only shown and hidden; a still picture is read once and repeated.
        void AppendLayer(int i)
        {
            var w = e.Width.ToString(Inv);
            var h = e.Height.ToString(Inv);
            var l = layers[i];
            var p = l.Placement;
            var input = nextInput + i;
            if (l.Cover)
            {
                sb.Append(Inv, $"[{input}:v]scale@cam{i}=w={w}:h={h}:flags=bicubic:force_original_aspect_ratio=increase,crop={w}:{h},format=yuva420p[cam{i}];");
            }
            else
            {
                // The trim comes before the scale, so the scale's live size never makes it read outside the picture.
                var c = LayerCrop(l.Crop);
                sb.Append(Inv, $"[{input}:v]crop@cam{i}=w='{c.W}':h='{c.H}':x='{c.X}':y='{c.Y}',");
                sb.Append(Inv, $"scale@cam{i}=w={p.Width}:h={p.Height}:flags=bicubic,format=yuva420p[cam{i}];");
            }

            var eof = l.Kind == LayerInputKind.Still ? "repeat" : "pass";
            sb.Append(Inv, $"[{below}][cam{i}]overlay@cam{i}=x={p.X}:y={p.Y}:eval=frame:format=yuv420:eof_action={eof}[comp{i}];");
            below = $"comp{i}";
        }

        sb.Append(Inv, $"[{below}]");
        if (commandPort is { } port)
        {
            // Two escaping levels: '\:' for the option parser, doubled for the graph parser.
            sb.Append(Inv, $@"zmq=bind_address=tcp\\://127.0.0.1\\:{port},");
        }

        sb.Append(Inv, $"format={PixelFormat(e.EncoderId)}[vout]");

        // Audio: the pipes and tones, then the sound of scene videos (from their own inputs).
        var sr = e.AudioSampleRate.ToString(Inv);
        var firstAudio = nextInput + layers.Count;
        var branches = new List<string>();
        for (var i = 0; i < audio.Count; i++)
        {
            var a = audio[i];
            if (a.Name is { } channel)
            {
                ValidateChannelName(channel);
            }

            var at = a.Name is { } name ? "@" + name : string.Empty;
            sb.Append(Inv, $";[{firstAudio + i}:a]aresample={sr}:async=1000,aformat=sample_fmts=fltp:channel_layouts=stereo,");
            if (a.NoiseSuppression is { } denoise)
            {
                sb.Append(Inv, $"afftdn{at}=nr={NoiseReduction(denoise)}:nf=-40,");
            }

            sb.Append(Inv, $"volume{at}={Gain(a.Gain)}[a{i}]");
            branches.Add($"a{i}");
        }

        for (var i = 0; i < layers.Count; i++)
        {
            if (layers[i].AudioName is { } media)
            {
                ValidateChannelName(media);
                sb.Append(Inv, $";[{nextInput + i}:a]aresample={sr}:async=1000,aformat=sample_fmts=fltp:channel_layouts=stereo,volume@{media}={Gain(layers[i].AudioGain)}[m{i}]");
                branches.Add($"m{i}");
            }
        }

        if (branches.Count == 1)
        {
            sb.Append(Inv, $";[{branches[0]}]anull[aout]");
        }
        else
        {
            sb.Append(';');
            foreach (var branch in branches)
            {
                sb.Append(Inv, $"[{branch}]");
            }

            sb.Append(Inv, $"amix=inputs={branches.Count}:duration=longest:normalize=0[aout]");
        }

        return sb.ToString();
    }

    /// <summary>
    /// <c>crop@camI</c>'s options for showing the part <paramref name="crop"/> of a layer, as expressions of
    /// its own size, so the same values start the graph and are sent live.
    /// </summary>
    public static (string W, string H, string X, string Y) LayerCrop(Linkast.Core.Camera.NormalizedRect crop)
    {
        var c = crop.Clamped(Linkast.Core.Camera.SceneLayout.MinCrop);
        static string F(double v) => v.ToString("0.######", Inv);
        return ($"iw*{F(c.Width)}", $"ih*{F(c.Height)}", $"iw*{F(c.X)}", $"ih*{F(c.Y)}");
    }

    private static string Gain(double gain) => Math.Clamp(gain, 0, 4).ToString("0.###", Inv);

    /// <summary>
    /// Every capture and layer once, bottom first: scene media (on the canvas, under everything), then
    /// <paramref name="stack"/>, then anything it left out in the default order (captures, then layers).
    /// </summary>
    internal static List<StackEntry> StackOrder(int captureCount, IReadOnlyList<VideoLayerJobInput> layers, IReadOnlyList<StackEntry>? stack)
    {
        var order = new List<StackEntry>();
        for (var i = 0; i < layers.Count; i++)
        {
            if (layers[i].Cover)
            {
                order.Add(StackEntry.Layer(i));
            }
        }

        bool Valid(StackEntry s) => s.Index >= 0 && (s.IsCapture ? s.Index < captureCount : s.Index < layers.Count);
        foreach (var entry in stack ?? [])
        {
            if (Valid(entry) && !order.Contains(entry))
            {
                order.Add(entry);
            }
        }

        for (var k = 0; k < captureCount; k++)
        {
            if (!order.Contains(StackEntry.Capture(k)))
            {
                order.Add(StackEntry.Capture(k));
            }
        }

        for (var i = 0; i < layers.Count; i++)
        {
            if (!order.Contains(StackEntry.Layer(i)))
            {
                order.Add(StackEntry.Layer(i));
            }
        }

        return order;
    }

    /// <summary>
    /// A live audio channel's name becomes part of a filter name (<c>volume@name</c>) and of zmq
    /// commands, so only lower-case letters, digits and '_' are allowed.
    /// </summary>
    public static bool IsValidChannelName(string? name) =>
        name is { Length: > 0 and <= 40 } && name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');

    private static void ValidateChannelName(string name)
    {
        if (!IsValidChannelName(name))
        {
            throw new StreamFailureException(new StreamError(ErrorCategory.Configuration, "audio.channel", "An internal audio channel name is not valid. This is a Linkast bug; please report it."));
        }
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
        // tcp:// only to this PC: the output buffer in front of the server (BufferedOutputLease).
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)
            || (u.Scheme != "rtmp" && u.Scheme != "rtmps" && !(u.Scheme == "tcp" && u.IsLoopback))
            || url.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || url.StartsWith('-'))
        {
            throw new StreamFailureException(new StreamError(ErrorCategory.Configuration, "destination.url", "The output URL is not a valid RTMP URL."));
        }
    }

    private static void ValidateMediaFilePath(string? path)
    {
        if (path is null || !Path.IsPathFullyQualified(path) || path.Any(char.IsControl) || path.StartsWith('-'))
        {
            throw new StreamFailureException(new StreamError(ErrorCategory.Configuration, "scene.media", "A scene's picture or video file name is not valid."));
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

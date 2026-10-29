// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using Linkast.Core;
using Linkast.Core.Camera;
using Linkast.Engine.FFmpeg;
using Linkast.Networking;
using Linkast.Platform.Windows.Audio;
using Linkast.Platform.Windows.Capture;
using Linkast.Platform.Windows.Layers;
using Xunit.Abstractions;

namespace Linkast.IntegrationTests;

/// <summary>
/// How many new pictures of the captured window and of a live layer reach the stream. The fake camera
/// changes picture 30 times a second on its own clock (like a webcam), its frames go through the real
/// pipe writer, and the recording is measured frame by frame. LINKAST_PACING_HWND measures a real game
/// window instead of the test's own.
/// </summary>
public sealed class PacingDiagnosticsTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task Camera_layer_keeps_its_frame_rate_over_a_captured_game()
    {
        var seconds = int.Parse(Environment.GetEnvironmentVariable("LINKAST_PACING_SECONDS") ?? "20", CultureInfo.InvariantCulture);
        var ffmpeg = await FfmpegLocator.LocateAsync();
        var probes = await new EncoderCatalog(ffmpeg).ProbeAsync();
        var encoder = probes.FirstOrDefault(p => p.Works && p.Encoder.Kind == EncoderKind.Hardware)?.Encoder.Id;
        Skip.If(encoder is null, "Needs a hardware encoder: 1080p60 in software would measure the CPU, not the pacing.");

        // A window that repaints with a new brightness about every 16 ms stands in for the game, unless
        // LINKAST_PACING_HWND names a real one.
        var capture = Environment.GetEnvironmentVariable("LINKAST_PACING_CAPTURE");
        var hwndText = Environment.GetEnvironmentVariable("LINKAST_PACING_HWND");
        using var window = string.IsNullOrEmpty(hwndText) && capture is null ? MovingWindow.Start() : null;
        if (window is not null)
        {
            hwndText = (await window.HandleAsync()).ToString(CultureInfo.InvariantCulture);
        }
        var folder = Path.Combine(Path.GetTempPath(), "linkast-pacing-" + Guid.NewGuid().ToString("N")[..6]);

        var provider = new TickingLayerProvider();
        await using var engine = new FfmpegStreamEngine(ffmpeg, new RelayOutputTransport(), new WasapiAudioInputProvider(),
            logger: new OutputLogger(output), options: new EngineOptions { ConnectTimeout = TimeSpan.FromSeconds(20) }, layerProvider: provider);
        var saved = new TaskCompletionSource<string>();
        engine.RecordingSaved += (_, path) => saved.TrySetResult(path);
        engine.StateChanged += (_, e) => output.WriteLine($"{DateTime.Now:HH:mm:ss.fff} state {e.Previous} -> {e.Current} {e.Error}");
        engine.DiagnosticLine += (_, l) => output.WriteLine("ffmpeg: " + l);

        // LINKAST_PACING_CAPTURE=wgc-display or dda-display captures the main monitor instead of the window.
        VideoSourceSpec video = capture switch
        {
            "wgc-display" => VideoSourceCatalog.ListMonitors().First(m => m.IsPrimary).ToSource(CaptureApi.WindowsGraphicsCapture),
            "dda-display" => VideoSourceCatalog.ListMonitors().First(m => m.IsPrimary).ToSource(CaptureApi.DesktopDuplication),
            _ => new WindowSource(long.Parse(hwndText!, CultureInfo.InvariantCulture), "game", null),
        };
        output.WriteLine($"capturing {video}");
        // LINKAST_PACING_BROADCAST=1 also streams to LINKAST_LIVE_URL / LINKAST_LIVE_KEY: a real server
        // makes FFmpeg wait seconds while it opens the output, which the stream must catch up on.
        var liveUrl = Environment.GetEnvironmentVariable("LINKAST_LIVE_URL");
        var liveKey = Environment.GetEnvironmentVariable("LINKAST_LIVE_KEY");
        var broadcast = Environment.GetEnvironmentVariable("LINKAST_PACING_BROADCAST") == "1" && !string.IsNullOrEmpty(liveUrl) && !string.IsNullOrEmpty(liveKey);
        StreamTelemetry? latest = null;
        engine.TelemetryUpdated += (_, t) => latest = t;
        var session = new StreamSession(
            video,
            [
                // Desktop, microphone and one program's sound, like a typical game scene.
                new SystemAudioSource(null, "Default output"),
                new MicrophoneSource(null, "Default microphone"),
                new ProcessAudioSource((uint)Environment.ProcessId, "Test process"),
            ],
            EncoderSettings.Default1080p60(encoder) with
            {
                VideoKbps = int.Parse(Environment.GetEnvironmentVariable("LINKAST_PACING_KBPS") ?? "6000", CultureInfo.InvariantCulture),
            },
            broadcast ? new Destination("Live", new Uri(liveUrl!), new Secret(liveKey!)) : new Destination("None", new Uri("rtmp://invalid/"), new Secret("")),
            NetworkSelection.SystemDefault,
            ReconnectPolicy.Default)
        {
            Broadcast = broadcast,
            Recording = new RecordingSpec(folder),
            CameraLayers = [new CameraLayerBinding(new LiveCameraLayer("cam"), new NormalizedRect(0.71, 0.67, 0.27, 0.27))],
        };

        var gc = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        await engine.StartAsync(session);
        await Task.Delay(TimeSpan.FromSeconds(5));
        var behindEarly = (latest?.Uptime ?? TimeSpan.Zero) - (latest?.Encoder?.OutTime ?? TimeSpan.Zero);
        await Task.Delay(TimeSpan.FromSeconds(seconds - 5));
        output.WriteLine($"garbage collections: gen0 {GC.CollectionCount(0) - gc.Item1}, gen1 {GC.CollectionCount(1) - gc.Item2}, gen2 {GC.CollectionCount(2) - gc.Item3}");
        var speed = latest?.Encoder?.Speed ?? 0;
        var behind = (latest?.Uptime ?? TimeSpan.Zero) - (latest?.Encoder?.OutTime ?? TimeSpan.Zero);
        await engine.StopAsync();
        output.WriteLine($"broadcast {broadcast}: encoder speed {speed:0.000}x, stream {behindEarly.TotalSeconds:0.0} s behind the clock after 5 s, {behind.TotalSeconds:0.0} s at the end");
        var file = await saved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var layer = provider.Last!;
        output.WriteLine($"camera pictures drawn {layer.PicturesDrawn}, frames written {layer.FramesWritten} ({layer.FramesRepeated} repeats, {layer.PicturesDropped} dropped), " +
            $"render {layer.RenderTime.TotalMilliseconds / Math.Max(1, layer.PicturesDrawn):0.0} ms each");

        // The camera sits at x 0.71..0.98, y 0.67..0.94 of 1920x1080; measure its middle.
        var cam = await MeasureAsync(ffmpeg, file, "crop=300:160:1450:780", "YAVG");
        var game = await MeasureAsync(ffmpeg, file, "crop=1200:500:100:200", "YDIF");
        var camNew = cam.Zip(cam.Skip(1), (a, b) => Math.Abs(b - a) > 3).Count(x => x);
        var gameNew = game.Count(d => d > 0.4);
        var duration = cam.Count / 60.0;
        output.WriteLine($"recording {cam.Count} frames ({duration:0.0} s): camera {camNew / duration:0.0} new pictures/s, game {gameNew / duration:0.0} changed frames/s");
        output.WriteLine("camera new pictures per second: " + string.Join(' ', PerSecond(cam)));
        Directory.Delete(folder, true);

        // Before the queued pipe writer: 7.8 of 30 (the tester's camera: about 8). The window's rate is
        // only reported: it is the test window's own repaint rate, which drops when the PC is busy.
        Assert.True(camNew / duration >= 27, $"Camera reached the stream at {camNew / duration:0.0} new pictures/s of 30.");

        // Recording only, nothing may make the picture fall further behind real time (a layer once held it
        // ~10 s behind the sound). The ~1 s from the start is FFmpeg starting. Streaming, the upload
        // decides how fast it catches up, so it is only reported.
        if (!broadcast)
        {
            Assert.True(behind - behindEarly < TimeSpan.FromSeconds(0.3), $"The picture fell behind real time: {behindEarly.TotalSeconds:0.0} s, then {behind.TotalSeconds:0.0} s.");
        }
    }

    /// <summary>The engine's own log lines (the per-minute stats) in the test output.</summary>
    private sealed class OutputLogger(ITestOutputHelper output) : Microsoft.Extensions.Logging.ILogger<FfmpegStreamEngine>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Information;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                output.WriteLine($"{DateTime.Now:HH:mm:ss} log: {formatter(state, exception)}");
            }
        }
    }

    /// <summary>A plain window in another process whose whole picture changes about 60 times a second.</summary>
    private sealed class MovingWindow : IDisposable
    {
        private const string Script = """
            Add-Type -AssemblyName System.Windows.Forms, System.Drawing
            $f = New-Object System.Windows.Forms.Form
            $f.Text = 'Linkast pacing test'; $f.Width = 1280; $f.Height = 720; $f.ShowInTaskbar = $false
            $f.StartPosition = 'Manual'; $f.Left = 40; $f.Top = 40
            $f.GetType().GetProperty('DoubleBuffered', [Reflection.BindingFlags]'NonPublic,Instance').SetValue($f, $true)
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $f.Add_Paint({ param($s, $e)
                $n = [int]($sw.Elapsed.TotalSeconds * 60); $v = 30 + ($n * 37) % 200
                $e.Graphics.Clear([Drawing.Color]::FromArgb($v, $v, $v)); $s.Invalidate() })
            $f.Add_Shown({ [Console]::Out.WriteLine($f.Handle.ToInt64()); [Console]::Out.Flush() })
            [System.Windows.Forms.Application]::Run($f)
            """;

        private readonly Process _process;

        private MovingWindow(Process process) => _process = process;

        public static MovingWindow Start()
        {
            var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", Script })
            {
                psi.ArgumentList.Add(a);
            }

            return new MovingWindow(Process.Start(psi)!);
        }

        public async Task<long> HandleAsync()
        {
            var line = await _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await Task.Delay(500);
            return long.Parse(line ?? throw new InvalidOperationException("The test window did not open."), CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            if (!_process.HasExited)
            {
                _process.Kill();
            }

            _process.Dispose();
        }
    }

    private static IEnumerable<int> PerSecond(List<double> avg)
    {
        for (var s = 0; s + 60 <= avg.Count; s += 60)
        {
            var n = 0;
            for (var i = s + 1; i < s + 60; i++)
            {
                if (Math.Abs(avg[i] - avg[i - 1]) > 3)
                {
                    n++;
                }
            }

            yield return n;
        }
    }

    private static async Task<List<double>> MeasureAsync(FfmpegInstallation ffmpeg, string file, string crop, string key)
    {
        var psi = new ProcessStartInfo(ffmpeg.Path) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", "-i", file, "-an", "-vf", $"{crop},signalstats,metadata=mode=print:key=lavfi.signalstats.{key}:file=-", "-f", "null", "-" })
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        _ = p.StandardError.ReadToEndAsync();
        var values = new List<double>();
        while (await p.StandardOutput.ReadLineAsync() is { } line)
        {
            var at = line.IndexOf('=', StringComparison.Ordinal);
            if (line.StartsWith("lavfi.", StringComparison.Ordinal) && at > 0)
            {
                values.Add(double.Parse(line[(at + 1)..], CultureInfo.InvariantCulture));
            }
        }

        await p.WaitForExitAsync();
        return values;
    }

    private sealed class TickingLayerProvider : IVideoLayerInputProvider
    {
        public TickingLayerInput? Last { get; private set; }

        public IVideoLayerInput Open(LiveCameraLayer layer, int width, int height, int frameRate) =>
            Last = new TickingLayerInput(layer.LayerId, width, height, frameRate);
    }

    /// <summary>
    /// A webcam stand-in: a new flat 1280 × 720 picture 30 times a second, whatever the writer does, drawn
    /// into the layer by the real camera renderer (so each frame costs what a real camera's does).
    /// </summary>
    private sealed class TickingLayerInput(string id, int width, int height, int frameRate)
        : PipeFrameWriter(id, "cam", width, height, frameRate)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly BgraFrame _source = new(1280, 720);
        private readonly CameraLayerRenderer _renderer = new();
        private long _shown = -1;

        public long PicturesDrawn { get; private set; }

        protected override void ProduceFrame()
        {
            var picture = (long)(_clock.Elapsed.TotalSeconds * 30);
            if (picture == _shown)
            {
                return;
            }

            _shown = picture;
            PicturesDrawn++;
            var level = (byte)(30 + (picture * 37 % 200));
            var pixels = _source.Pixels;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = level;
                pixels[i + 1] = level;
                pixels[i + 2] = level;
                pixels[i + 3] = 255;
            }

            var t = Stopwatch.GetTimestamp();
            _renderer.Render(_source, CameraLayerSettings.Default, Frame, Width, Height);
            RenderTime += Stopwatch.GetElapsedTime(t);
        }

        public TimeSpan RenderTime { get; private set; }
    }
}

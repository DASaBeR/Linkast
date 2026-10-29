// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using Linkast.Core;
using Linkast.Core.Audio;
using Linkast.Core.Camera;
using Linkast.Engine.FFmpeg;
using Linkast.Networking;
using Linkast.Platform.Windows.Layers;
using Xunit.Abstractions;

namespace Linkast.IntegrationTests;

/// <summary>
/// Sound against picture through the real engine: a camera layer flashes white and an audio pipe beeps
/// at the same wall-clock moments (each second), and the recording is measured for how far apart they
/// land. LINKAST_PACING_BROADCAST=1 with LINKAST_LIVE_URL / LINKAST_LIVE_KEY also streams to a real
/// server, whose slow start (FFmpeg waits seconds while it opens the output) is what can shift them.
/// </summary>
public sealed class AvSyncDiagnosticsTests(ITestOutputHelper output)
{
    private static readonly Stopwatch Epoch = Stopwatch.StartNew();

    /// <summary>True during the first 100 ms of each second of the shared clock.</summary>
    private static bool Marked(TimeSpan at) => at.TotalSeconds % 1.0 < 0.1;

    [SkippableFact]
    public async Task Camera_and_sound_stay_in_sync()
    {
        var seconds = int.Parse(Environment.GetEnvironmentVariable("LINKAST_PACING_SECONDS") ?? "20", CultureInfo.InvariantCulture);
        var ffmpeg = await FfmpegLocator.LocateAsync();
        var probes = await new EncoderCatalog(ffmpeg).ProbeAsync();
        var encoder = probes.FirstOrDefault(p => p.Works && p.Encoder.Kind == EncoderKind.Hardware)?.Encoder.Id;
        Skip.If(encoder is null, "Needs a hardware encoder.");

        var liveUrl = Environment.GetEnvironmentVariable("LINKAST_LIVE_URL");
        var liveKey = Environment.GetEnvironmentVariable("LINKAST_LIVE_KEY");
        var broadcast = Environment.GetEnvironmentVariable("LINKAST_PACING_BROADCAST") == "1" && !string.IsNullOrEmpty(liveUrl) && !string.IsNullOrEmpty(liveKey);
        var folder = Path.Combine(Path.GetTempPath(), "linkast-avsync-" + Guid.NewGuid().ToString("N")[..6]);

        await using var engine = new FfmpegStreamEngine(ffmpeg, new RelayOutputTransport(), new BeepAudioProvider(),
            options: new EngineOptions { ConnectTimeout = TimeSpan.FromSeconds(20) }, layerProvider: new FlashLayerProvider());
        var saved = new TaskCompletionSource<string>();
        engine.RecordingSaved += (_, path) => saved.TrySetResult(path);
        engine.StateChanged += (_, e) => output.WriteLine($"{Epoch.Elapsed.TotalSeconds:0.00} s: {e.Previous} -> {e.Current} {e.Error}");

        var session = new StreamSession(
            new TestPatternSource(),
            [new MicrophoneSource("beep", "Beep")],
            EncoderSettings.Default1080p60(encoder!),
            broadcast ? new Destination("Live", new Uri(liveUrl!), new Secret(liveKey!)) : new Destination("None", new Uri("rtmp://invalid/"), new Secret("")),
            NetworkSelection.SystemDefault,
            ReconnectPolicy.Default)
        {
            Broadcast = broadcast,
            Recording = new RecordingSpec(folder),
            CameraLayers = [new CameraLayerBinding(new LiveCameraLayer("cam"), new NormalizedRect(0.71, 0.67, 0.27, 0.27))],
        };

        await engine.StartAsync(session);
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        await engine.StopAsync();
        var file = await saved.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var flashes = Onsets(await VideoLevelsAsync(ffmpeg, file), 1.0 / 60, 128);
        var beeps = Onsets(await AudioLevelsAsync(ffmpeg, file), 0.005, 0.1);
        Directory.Delete(folder, true);

        // Pair each flash with the nearest beep; the median difference is the offset.
        var offsets = flashes.Select(f => beeps.Count == 0 ? double.NaN : beeps.MinBy(b => Math.Abs(b - f)) - f)
            .Where(d => !double.IsNaN(d) && Math.Abs(d) < 0.5).Order().ToList();
        output.WriteLine($"broadcast {broadcast}: {flashes.Count} flashes, {beeps.Count} beeps");
        output.WriteLine("sound minus picture (ms): " + string.Join(' ', offsets.Select(d => (d * 1000).ToString("0", CultureInfo.InvariantCulture))));
        Assert.NotEmpty(offsets);
        var median = offsets[offsets.Count / 2];
        output.WriteLine($"median: sound {median * 1000:0} ms after the picture");
        Assert.InRange(median, -0.1, 0.1);
    }

    /// <summary>Times (seconds into the file) where the level rises above <paramref name="threshold"/>.</summary>
    private static List<double> Onsets(List<double> levels, double step, double threshold)
    {
        var onsets = new List<double>();
        for (var i = 1; i < levels.Count; i++)
        {
            if (levels[i] >= threshold && levels[i - 1] < threshold)
            {
                onsets.Add(i * step);
            }
        }

        return onsets;
    }

    private static async Task<List<double>> VideoLevelsAsync(FfmpegInstallation ffmpeg, string file)
    {
        var values = new List<double>();
        await RunAsync(ffmpeg, ["-v", "error", "-i", file, "-an", "-vf", "crop=300:160:1450:780,signalstats,metadata=mode=print:key=lavfi.signalstats.YAVG:file=-", "-f", "null", "-"], async stdout =>
        {
            while (await stdout.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("lavfi.", StringComparison.Ordinal))
                {
                    values.Add(double.Parse(line[(line.IndexOf('=', StringComparison.Ordinal) + 1)..], CultureInfo.InvariantCulture));
                }
            }
        });
        return values;
    }

    /// <summary>The peak level of each 5 ms of the sound (mono, 48 kHz).</summary>
    private static async Task<List<double>> AudioLevelsAsync(FfmpegInstallation ffmpeg, string file)
    {
        var levels = new List<double>();
        await RunAsync(ffmpeg, ["-v", "error", "-i", file, "-vn", "-ac", "1", "-ar", "48000", "-f", "f32le", "-"], async stdout =>
        {
            var buffer = new byte[240 * 4];
            int read;
            while ((read = await stdout.BaseStream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false)) > 0)
            {
                var peak = 0.0;
                for (var i = 0; i + 4 <= read; i += 4)
                {
                    peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(buffer, i)));
                }

                levels.Add(peak);
            }
        });
        return levels;
    }

    private static async Task RunAsync(FfmpegInstallation ffmpeg, string[] args, Func<StreamReader, Task> read)
    {
        var psi = new ProcessStartInfo(ffmpeg.Path) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        _ = p.StandardError.ReadToEndAsync();
        await read(p.StandardOutput);
        await p.WaitForExitAsync();
    }

    private sealed class FlashLayerProvider : IVideoLayerInputProvider
    {
        public IVideoLayerInput Open(LiveCameraLayer layer, int width, int height, int frameRate) =>
            new FlashLayerInput(layer.LayerId, width, height, frameRate);
    }

    /// <summary>White while the shared clock is marked, black otherwise: what a camera sees of a flash.</summary>
    private sealed class FlashLayerInput(string id, int width, int height, int frameRate)
        : PipeFrameWriter(id, "cam", width, height, frameRate)
    {
        protected override void ProduceFrame()
        {
            var level = Marked(Epoch.Elapsed) ? (byte)255 : (byte)0;
            for (var i = 0; i < Frame.Length; i += 4)
            {
                Frame[i] = level;
                Frame[i + 1] = level;
                Frame[i + 2] = level;
                Frame[i + 3] = 255;
            }
        }
    }

    private sealed class BeepAudioProvider : IAudioInputProvider
    {
        public bool CanOpen(AudioSourceSpec source) => true;

        public IAudioInput Open(AudioSourceSpec source) => new BeepAudioInput(source);
    }

    /// <summary>
    /// A microphone stand-in paced like the real one (audio from when FFmpeg connects, locked to the wall
    /// clock): a 1 kHz tone while the shared clock is marked, silence otherwise.
    /// </summary>
    private sealed class BeepAudioInput(AudioSourceSpec source) : IAudioInput
    {
        private readonly string _name = "linkast-beep-" + Guid.NewGuid().ToString("N");
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private NamedPipeServerStream? _pipe;
        private Task? _writer;

        public AudioSourceSpec Source { get; } = source;

        public string PipePath => @"\\.\pipe\" + _name;

        public PcmFormat Format { get; } = new(48000, 2, PcmSampleFormat.F32);

        public Task Completion => _completion.Task;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _pipe = new NamedPipeServerStream(_name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _writer = Task.Run(() => WriteAsync(_cts.Token), CancellationToken.None);
            return Task.CompletedTask;
        }

        private async Task WriteAsync(CancellationToken ct)
        {
            try
            {
                await _pipe!.WaitForConnectionAsync(ct);
                var connectedAt = Epoch.Elapsed;
                long written = 0;
                var buffer = new byte[48000 * 8];
                while (!ct.IsCancellationRequested)
                {
                    var due = (long)((Epoch.Elapsed - connectedAt).TotalSeconds * 48000);
                    var count = (int)Math.Min(due - written, 48000);
                    if (count <= 0)
                    {
                        await Task.Delay(5, ct);
                        continue;
                    }

                    for (var i = 0; i < count; i++)
                    {
                        var at = connectedAt + TimeSpan.FromSeconds((written + i) / 48000.0);
                        var v = Marked(at) ? (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * (written + i) / 48000.0)) : 0f;
                        BitConverter.TryWriteBytes(buffer.AsSpan(i * 8), v);
                        BitConverter.TryWriteBytes(buffer.AsSpan(i * 8 + 4), v);
                    }

                    await _pipe.WriteAsync(buffer.AsMemory(0, count * 8), ct);
                    written += count;
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // FFmpeg closed the pipe, or the test stopped.
            }

            _completion.TrySetResult();
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            if (_writer is not null)
            {
                await _writer.WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(_ => { }, TaskScheduler.Default);
            }

            if (_pipe is not null)
            {
                await _pipe.DisposeAsync();
            }

            _cts.Dispose();
        }
    }
}

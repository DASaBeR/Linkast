// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using Linkast.Core;
using Linkast.Engine.FFmpeg;
using Linkast.Networking;
using Xunit.Abstractions;

namespace Linkast.IntegrationTests;

/// <summary>
/// End-to-end tests against a real ffmpeg.exe and the MediaMTX container in tools/test-ingest.
/// Skipped automatically when either is unavailable.
/// </summary>
[Collection("ingest")]
public sealed class StreamingIntegrationTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const string IngestHost = "127.0.0.1";
    private const int IngestPort = 1935;
    private const string ContainerName = "linkast-mediamtx";

    private FfmpegInstallation? _ffmpeg;

    public async Task InitializeAsync()
    {
        try
        {
            _ffmpeg = await FfmpegLocator.LocateAsync();
        }
        catch (StreamFailureException)
        {
            _ffmpeg = null;
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Streams_test_pattern_live_and_stops_cleanly()
    {
        SkipUnlessReady();
        var key = "it-" + Guid.NewGuid().ToString("N")[..8];
        await using var engine = CreateEngine(new EngineOptions { ConnectTimeout = TimeSpan.FromSeconds(15) }, out var states, out _);

        await engine.StartAsync(Session(key));
        await WaitForStateAsync(engine, StreamState.Live, TimeSpan.FromSeconds(20));

        var probe = await ProbeAsync($"rtmp://{IngestHost}:{IngestPort}/live/{key}");
        output.WriteLine("ffprobe: " + probe);
        Assert.Contains("h264", probe, StringComparison.Ordinal);
        Assert.Contains("aac", probe, StringComparison.Ordinal);

        var sw = Stopwatch.StartNew();
        await engine.StopAsync();
        sw.Stop();

        Assert.Equal(StreamState.Stopped, engine.State);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6), $"Stop took {sw.Elapsed}.");
        Assert.Equal([StreamState.Preparing, StreamState.Connecting, StreamState.Live, StreamState.Stopping, StreamState.Stopped], states.ToArray());
    }

    [SkippableFact]
    public async Task Recovers_after_ingest_restart()
    {
        SkipUnlessReady();
        Skip.IfNot(await DockerAsync("inspect", ContainerName) == 0, "Docker container not available for restart test.");

        var key = "it-" + Guid.NewGuid().ToString("N")[..8];
        var options = new EngineOptions { StallTimeout = TimeSpan.FromSeconds(4), ConnectTimeout = TimeSpan.FromSeconds(15) };
        await using var engine = CreateEngine(options, out var states, out var telemetry);
        var policy = new ReconnectPolicy(8, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(60));

        await engine.StartAsync(Session(key) with { Reconnect = policy });
        await WaitForStateAsync(engine, StreamState.Live, TimeSpan.FromSeconds(20));

        output.WriteLine("Restarting ingest container...");
        Assert.Equal(0, await DockerAsync("restart", "-t", "0", ContainerName));

        // Reconnect may complete while `docker restart` is still returning, so check the recorded history.
        await WaitUntilAsync(() => states.SkipWhile(s => s != StreamState.Reconnecting).Contains(StreamState.Live), TimeSpan.FromSeconds(45));
        Assert.Equal(StreamState.Live, engine.State);

        var last = telemetry.Last();
        output.WriteLine($"Reconnects: {last.ReconnectCount}, last error: {last.LastError}");
        Assert.True(last.ReconnectCount >= 1);
        Assert.Equal(ErrorCategory.Network, last.LastError?.Category);

        var probe = await ProbeAsync($"rtmp://{IngestHost}:{IngestPort}/live/{key}");
        Assert.Contains("h264", probe, StringComparison.Ordinal);

        await engine.StopAsync();
        Assert.Equal(StreamState.Stopped, engine.State);
    }

    [SkippableFact]
    public async Task Streams_real_system_audio_and_microphone_through_pipes()
    {
        SkipUnlessReady();
        var outputs = Linkast.Platform.Windows.Audio.AudioDeviceCatalog.List(Linkast.Platform.Windows.Audio.AudioDeviceKind.Output);
        var inputs = Linkast.Platform.Windows.Audio.AudioDeviceCatalog.List(Linkast.Platform.Windows.Audio.AudioDeviceKind.Input);
        Skip.If(outputs.Count == 0, "No audio output device.");

        var audio = new List<AudioSourceSpec> { new SystemAudioSource(null, "Default output") };
        if (inputs.Count > 0)
        {
            audio.Add(new MicrophoneSource(inputs[0].Id, inputs[0].Name));
        }

        var key = "it-" + Guid.NewGuid().ToString("N")[..8];
        var engine = new FfmpegStreamEngine(_ffmpeg!, new RelayOutputTransport(), new Linkast.Platform.Windows.Audio.WasapiAudioInputProvider(),
            options: new EngineOptions { ConnectTimeout = TimeSpan.FromSeconds(15) });
        await using var _ = engine;
        engine.StateChanged += (_, e) => output.WriteLine($"state {e.Previous} -> {e.Current} {e.Error}");
        engine.DiagnosticLine += (_, l) => output.WriteLine("ffmpeg: " + l);
        StreamTelemetry? latest = null;
        engine.TelemetryUpdated += (_, t) => latest = t;

        await engine.StartAsync(Session(key) with { Audio = audio });
        await WaitForStateAsync(engine, StreamState.Live, TimeSpan.FromSeconds(20));
        await Task.Delay(TimeSpan.FromSeconds(6)); // stays live while loopback is silent
        Assert.Equal(StreamState.Live, engine.State);

        // Regression: pipe audio once carried epoch wall-clock timestamps, making out_time enormous.
        var t = latest!;
        output.WriteLine($"uptime {t.Uptime}, out_time {t.Encoder?.OutTime}, speed {t.Encoder?.Speed}");
        Assert.NotNull(t.Encoder);
        Assert.True((t.Encoder.OutTime - t.Uptime).Duration() < TimeSpan.FromSeconds(5), "Output time should track wall-clock time.");
        // Speed is FFmpeg's average since its own start and the test pattern is not a real-time source,
        // so a start-up burst can lift it above 1. The bug this guards against produced ~98,000,000.
        Assert.InRange(t.Encoder.Speed, 0.5, 2.0);

        var probe = await ProbeAsync($"rtmp://{IngestHost}:{IngestPort}/live/{key}");
        Assert.Contains("aac", probe, StringComparison.Ordinal);
        await engine.StopAsync();
        Assert.Equal(StreamState.Stopped, engine.State);
    }

    [SkippableFact]
    public async Task Refused_connection_fails_as_network_error_after_bounded_retries()
    {
        SkipUnlessReady();
        await using var engine = CreateEngine(new EngineOptions(), out _, out _);
        var session = Session("x1234") with
        {
            Destination = new Destination("dead", new Uri("rtmp://127.0.0.1:1/live"), new Secret("x1234")),
            Reconnect = new ReconnectPolicy(2, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(60)),
        };

        StreamError? failure = null;
        engine.StateChanged += (_, e) =>
        {
            if (e.Current == StreamState.Failed)
            {
                failure = e.Error;
            }
        };

        await engine.StartAsync(session);
        await WaitForStateAsync(engine, StreamState.Failed, TimeSpan.FromSeconds(30));
        Assert.Equal("network.refused", failure?.Code);
    }

    [SkippableFact]
    public async Task Stream_key_never_appears_in_diagnostics()
    {
        SkipUnlessReady();
        var key = "SECRETKEY" + Guid.NewGuid().ToString("N")[..8];
        await using var engine = CreateEngine(new EngineOptions(), out _, out _);
        var lines = new ConcurrentBag<string>();
        engine.DiagnosticLine += (_, l) => lines.Add(l);

        // Force FFmpeg to print errors mentioning the URL by pointing at a closed port.
        var session = Session(key) with
        {
            Destination = new Destination("dead", new Uri("rtmp://127.0.0.1:1/live"), new Secret(key)),
            Reconnect = ReconnectPolicy.Disabled,
        };
        await engine.StartAsync(session);
        await WaitForStateAsync(engine, StreamState.Failed, TimeSpan.FromSeconds(30));
        Assert.DoesNotContain(lines, l => l.Contains(key, StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task Stream_key_is_not_on_ffmpegs_command_line_and_still_reaches_the_server()
    {
        SkipUnlessReady();
        var key = "SECRETKEY" + Guid.NewGuid().ToString("N")[..12];
        await using var engine = CreateEngine(new EngineOptions { ConnectTimeout = TimeSpan.FromSeconds(15) }, out _, out _);
        await engine.StartAsync(Session(key));
        await WaitForStateAsync(engine, StreamState.Live, TimeSpan.FromSeconds(20));

        // What any program (or a Task Manager window shown on stream) can read about FFmpeg.
        var commandLines = await FfmpegCommandLinesAsync();
        output.WriteLine($"{commandLines.Count} ffmpeg process(es) inspected");
        Assert.NotEmpty(commandLines);
        Assert.DoesNotContain(commandLines, c => c.Contains(key, StringComparison.Ordinal));

        // The server received the real key: the stream is playable under it.
        var probe = await ProbeAsync($"rtmp://{IngestHost}:{IngestPort}/live/{key}");
        output.WriteLine("ffprobe: " + probe);
        Assert.Contains("h264", probe, StringComparison.Ordinal);
        await engine.StopAsync();
    }

    private static async Task<IReadOnlyList<string>> FfmpegCommandLinesAsync()
    {
        var psi = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-NoProfile", "-Command", "Get-CimInstance Win32_Process -Filter \"Name='ffmpeg.exe'\" | ForEach-Object { $_.CommandLine }" })
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var text = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l.Contains("-filter_complex", StringComparison.Ordinal)).ToList();
    }

    private FfmpegStreamEngine CreateEngine(EngineOptions options, out ConcurrentQueue<StreamState> states, out ConcurrentQueue<StreamTelemetry> telemetry)
    {
        var engine = new FfmpegStreamEngine(_ffmpeg!, new RelayOutputTransport(), audioProvider: null, options: options);
        var s = new ConcurrentQueue<StreamState>();
        var t = new ConcurrentQueue<StreamTelemetry>();
        engine.StateChanged += (_, e) =>
        {
            s.Enqueue(e.Current);
            output.WriteLine($"state {e.Previous} -> {e.Current} {e.Error}");
        };
        engine.TelemetryUpdated += (_, e) => t.Enqueue(e);
        engine.DiagnosticLine += (_, l) => output.WriteLine("ffmpeg: " + l);
        states = s;
        telemetry = t;
        return engine;
    }

    private static StreamSession Session(string key) => new(
        new TestPatternSource(),
        [new TestToneSource()],
        new EncoderSettings("libx264", 640, 360, 30, 1500, Preset: "ultrafast"),
        new Destination("MediaMTX", new Uri($"rtmp://{IngestHost}:{IngestPort}/live"), new Secret(key)),
        NetworkSelection.SystemDefault,
        ReconnectPolicy.Default);

    private static async Task WaitForStateAsync(IStreamEngine engine, StreamState target, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? _, StreamStateChangedEventArgs e)
        {
            if (e.Current == target)
            {
                tcs.TrySetResult();
            }
        }

        engine.StateChanged += Handler;
        try
        {
            if (engine.State == target)
            {
                return;
            }

            await tcs.Task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            throw new Xunit.Sdk.XunitException($"Timed out waiting for {target}; state is {engine.State}.");
        }
        finally
        {
            engine.StateChanged -= Handler;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout)
            {
                throw new Xunit.Sdk.XunitException("Timed out waiting for condition.");
            }

            await Task.Delay(100);
        }
    }

    private void SkipUnlessReady()
    {
        Skip.If(_ffmpeg is null, "ffmpeg.exe not found.");
        Skip.IfNot(IngestReachable(), "MediaMTX not running. Start it with: docker compose -f tools/test-ingest/docker-compose.yml up -d");
    }

    private static bool IngestReachable()
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync(IngestHost, IngestPort).Wait(TimeSpan.FromSeconds(1)) && c.Connected;
        }
        catch (AggregateException)
        {
            return false;
        }
    }

    private async Task<string> ProbeAsync(string url)
    {
        var ffprobe = Path.Combine(Path.GetDirectoryName(_ffmpeg!.Path)!, "ffprobe.exe");
        var psi = new ProcessStartInfo(ffprobe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", "-rw_timeout", "5000000", "-show_entries", "stream=codec_name", "-of", "csv=p=0", url })
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        return stdout.Replace("\r", string.Empty, StringComparison.Ordinal).Replace('\n', ' ').Trim();
    }

    private static async Task<int> DockerAsync(params string[] args)
    {
        var psi = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using var p = Process.Start(psi)!;
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return -1;
        }
    }

}

[CollectionDefinition("ingest", DisableParallelization = true)]
public sealed class IngestTestGroup;

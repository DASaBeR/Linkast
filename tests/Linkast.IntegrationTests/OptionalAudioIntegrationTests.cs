// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Pipes;
using Linkast.Core;
using Linkast.Core.Audio;
using Linkast.Engine.FFmpeg;
using Linkast.Networking;
using Xunit.Abstractions;

namespace Linkast.IntegrationTests;

/// <summary>
/// A captured game's own sound is always in the stream, silent until its fader is up, and optional: when
/// Windows can't capture it, or it stops (the game closed), the stream goes on. A required sound still
/// stops the stream as before.
/// </summary>
public sealed class OptionalAudioIntegrationTests(ITestOutputHelper output) : IAsyncLifetime
{
    private FfmpegInstallation? _ffmpeg;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "linkast-optional-" + Guid.NewGuid().ToString("N")[..6]);

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

    public Task DisposeAsync()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }

        return Task.CompletedTask;
    }

    [SkippableFact]
    public async Task An_optional_sound_that_cant_be_opened_leaves_the_stream_running()
    {
        Skip.If(_ffmpeg is null, "ffmpeg not found.");
        var (engine, lines) = Engine(new FakeAudioProvider(failStart: true));
        await using (engine)
        {
            await engine.StartAsync(Session(new ProcessAudioSource(4242, "cs2") { Channel = AudioChannels.ForGame("cs2"), Optional = true }));
            await WaitForAsync(engine, StreamState.Live, TimeSpan.FromSeconds(20));
            Assert.True(await engine.SetGainAsync(AudioChannels.ForGame("cs2"), 1), "Its fader still works (on silence).");
            await engine.StopAsync();
        }

        Assert.Contains(lines, l => l.Contains("The sound of cs2 can't be captured", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task An_optional_sound_that_stops_while_live_only_goes_quiet()
    {
        Skip.If(_ffmpeg is null, "ffmpeg not found.");
        var (engine, lines) = Engine(new FakeAudioProvider(failAfter: TimeSpan.FromSeconds(3)));
        await using (engine)
        {
            await engine.StartAsync(Session(new ProcessAudioSource(4242, "cs2") { Channel = AudioChannels.ForGame("cs2"), Optional = true }));
            await WaitForAsync(engine, StreamState.Live, TimeSpan.FromSeconds(20));
            await Task.Delay(TimeSpan.FromSeconds(6));
            Assert.Equal(StreamState.Live, engine.State);
            await engine.StopAsync();
        }

        Assert.Contains(lines, l => l.Contains("The sound of cs2 stopped", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task A_required_sound_that_cant_be_opened_still_stops_the_stream()
    {
        Skip.If(_ffmpeg is null, "ffmpeg not found.");
        var (engine, _) = Engine(new FakeAudioProvider(failStart: true));
        await using (engine)
        {
            await engine.StartAsync(Session(new MicrophoneSource(null, "Mic")) with { Reconnect = ReconnectPolicy.Disabled });
            await WaitForAsync(engine, StreamState.Failed, TimeSpan.FromSeconds(20));
        }
    }

    private (FfmpegStreamEngine Engine, List<string> Lines) Engine(IAudioInputProvider audio)
    {
        var engine = new FfmpegStreamEngine(_ffmpeg!, new RelayOutputTransport(), audio, options: new EngineOptions { ConnectTimeout = TimeSpan.FromSeconds(20) });
        var lines = new List<string>();
        engine.DiagnosticLine += (_, l) =>
        {
            lock (lines)
            {
                lines.Add(l);
            }

            output.WriteLine("line: " + l);
        };
        engine.StateChanged += (_, e) => output.WriteLine($"state {e.Previous} -> {e.Current} {e.Error}");
        return (engine, lines);
    }

    private StreamSession Session(AudioSourceSpec sound) => new(
        new TestPatternSource(),
        [new TestToneSource(), sound],
        new EncoderSettings("libx264", 640, 360, 30, 1500, Preset: "ultrafast"),
        new Destination("None", new Uri("rtmp://invalid/"), new Secret("")),
        NetworkSelection.SystemDefault,
        ReconnectPolicy.Default)
    {
        Broadcast = false,
        Recording = new RecordingSpec(_folder),
    };

    private static async Task WaitForAsync(IStreamEngine engine, StreamState state, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (engine.State != state)
        {
            Assert.True(DateTime.UtcNow < end, $"Still {engine.State}, waiting for {state}.");
            await Task.Delay(100);
        }
    }

    private sealed class FakeAudioProvider(bool failStart = false, TimeSpan? failAfter = null) : IAudioInputProvider
    {
        public bool CanOpen(AudioSourceSpec source) => true;

        public IAudioInput Open(AudioSourceSpec source) => new FakeAudioInput(source, failStart, failAfter);
    }

    /// <summary>Silence at real time over a pipe; can fail to open, or fail (and close) after a while.</summary>
    private sealed class FakeAudioInput(AudioSourceSpec source, bool failStart, TimeSpan? failAfter) : IAudioInput
    {
        private readonly string _name = "linkast-fake-" + Guid.NewGuid().ToString("N");
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
            if (failStart)
            {
                throw new StreamFailureException(new StreamError(ErrorCategory.Audio, "audio.device", "The audio device could not be opened.", "process loopback refused"));
            }

            _pipe = new NamedPipeServerStream(_name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _writer = Task.Run(WriteAsync, CancellationToken.None);
            return Task.CompletedTask;
        }

        private async Task WriteAsync()
        {
            var ct = _cts.Token;
            try
            {
                await _pipe!.WaitForConnectionAsync(ct);
                var started = DateTime.UtcNow;
                var chunk = new byte[48000 * 8 / 100]; // 10 ms
                long written = 0;
                while (!ct.IsCancellationRequested)
                {
                    if (failAfter is { } after && DateTime.UtcNow - started > after)
                    {
                        _completion.TrySetException(new StreamFailureException(new StreamError(ErrorCategory.Audio, "audio.device", "The game closed.")));
                        await _pipe.DisposeAsync();
                        return;
                    }

                    if (written * 10 < (DateTime.UtcNow - started).TotalMilliseconds)
                    {
                        await _pipe.WriteAsync(chunk, ct);
                        written++;
                    }
                    else
                    {
                        await Task.Delay(5, ct);
                    }
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // Stopped, or FFmpeg closed the pipe.
            }

            _completion.TrySetResult();
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            if (_writer is not null)
            {
                await Task.WhenAny(_writer, Task.Delay(2000));
            }

            if (_pipe is not null)
            {
                await _pipe.DisposeAsync();
            }

            _completion.TrySetResult();
            _cts.Dispose();
        }
    }
}

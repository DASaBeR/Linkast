// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading.Channels;
using Linkast.Core;
using Linkast.Core.Audio;
using NAudio.Wave;

namespace Linkast.Platform.Windows.Audio;

/// <summary>
/// Serves a WASAPI endpoint (microphone, or loopback of an output device) to FFmpeg as raw PCM over a
/// current-user-only named pipe. Capture comes from the shared <see cref="AudioCaptureHub"/>, so the
/// device is opened once even if other parts of Linkast (audio-reactive effects) listen too. Gaps
/// (loopback is silent when nothing plays) are filled with silence so FFmpeg never stalls.
/// </summary>
public sealed class WasapiAudioInput : IAudioInput
{
    /// <summary>How far audio may run ahead of wall-clock time before chunks are dropped.</summary>
    private static readonly TimeSpan MaxLead = TimeSpan.FromMilliseconds(200);

    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly AudioCaptureHub _hub;
    private readonly bool _loopback;
    private readonly string? _deviceId;

    // A program's sound: its process ID, and whether that program is included (or everything but it).
    private readonly (uint Id, bool Include)? _process;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(200)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true,
    });

    private AudioTap? _tap;
    private NamedPipeServerStream? _pipe;
    private Task? _writer;
    private long _lastDataTimestamp;
    private volatile bool _pipeConnected;

    public WasapiAudioInput(AudioSourceSpec source, AudioCaptureHub? hub = null)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        _hub = hub ?? new AudioCaptureHub();
        (_loopback, _deviceId, _process) = source switch
        {
            SystemAudioSource { ExcludeProcessId: { } exclude } => (true, null, (exclude, false)),
            SystemAudioSource s => (true, s.DeviceId, ((uint, bool)?)null),
            MicrophoneSource m => (false, m.DeviceId, null),
            ProcessAudioSource p => (true, null, (p.ProcessId, true)),
            _ => throw new ArgumentException($"Unsupported source {source.GetType().Name}.", nameof(source)),
        };
        _pipeName = $"linkast-{(source is ProcessAudioSource ? "app" : _loopback ? "sys" : "mic")}-{Guid.NewGuid():N}";
    }

    public AudioSourceSpec Source { get; }

    public string PipePath => @"\\.\pipe\" + _pipeName;

    public PcmFormat Format { get; private set; } = new(48000, 2, PcmSampleFormat.F32);

    public Task Completion => _completion.Task;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _tap = _process is { } p ? _hub.SubscribeProcess(p.Id, p.Include, OnData) : _hub.Subscribe(_deviceId, _loopback, OnData);
        }
        catch (StreamFailureException ex)
        {
            throw new StreamFailureException(ex.Error with { UserMessage = $"The audio device '{Describe()}' could not be opened. It may be unplugged or in use." }, ex);
        }

        Format = _tap.Format;
        _tap.Failed += OnFailed;

        _pipe = new NamedPipeServerStream(
            _pipeName,
            PipeDirection.Out,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            inBufferSize: 0,
            outBufferSize: Format.BytesPerSecond / 4);

        _writer = Task.Run(() => WriterLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_tap is not null)
        {
            _tap.Failed -= OnFailed;
            _tap.Dispose();
        }

        if (_writer is not null)
        {
            try
            {
                await _writer.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The pipe disposal below unblocks it.
            }
        }

        if (_pipe is not null)
        {
            await _pipe.DisposeAsync().ConfigureAwait(false);
        }

        _completion.TrySetResult();
        _cts.Dispose();
    }

    internal static PcmFormat ToPcmFormat(WaveFormat wf)
    {
        var isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat
            || (wf is WaveFormatExtensible ext && ext.SubFormat == SubtypeIeeeFloat);
        var isPcm = wf.Encoding == WaveFormatEncoding.Pcm
            || (wf is WaveFormatExtensible ext2 && ext2.SubFormat == SubtypePcm);

        var sampleFormat = (isFloat, isPcm, wf.BitsPerSample) switch
        {
            (true, _, 32) => PcmSampleFormat.F32,
            (_, true, 16) => PcmSampleFormat.S16,
            (_, true, 24) => PcmSampleFormat.S24,
            (_, true, 32) => PcmSampleFormat.S32,
            _ => throw new StreamFailureException(new StreamError(
                ErrorCategory.Audio, "audio.format", $"Unsupported audio device format ({wf.Encoding}, {wf.BitsPerSample} bit).")),
        };

        return new PcmFormat(wf.SampleRate, wf.Channels, sampleFormat);
    }

    private void OnData(ReadOnlySpan<byte> buffer, bool silent)
    {
        if (buffer.IsEmpty)
        {
            return;
        }

        Volatile.Write(ref _lastDataTimestamp, Stopwatch.GetTimestamp());
        if (!_pipeConnected)
        {
            return; // Nothing is reading yet; drop rather than deliver stale audio later.
        }

        // The span is only valid during the callback. A "silent" packet's contents are undefined.
        var copy = silent ? new byte[buffer.Length] : buffer.ToArray();
        _queue.Writer.TryWrite(copy);
    }

    private void OnFailed(object? sender, Exception e)
    {
        if (!_cts.IsCancellationRequested)
        {
            _completion.TrySetException(new StreamFailureException(new StreamError(
                ErrorCategory.Audio, "audio.device", $"The audio device '{Describe()}' stopped working.", e.Message), e));
        }
    }

    private async Task WriterLoopAsync(CancellationToken ct)
    {
        var pipe = _pipe!;
        try
        {
            await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            _pipeConnected = true;

            var padder = new SilencePadder(Format.BytesPerSecond, Format.BlockAlign, TimeSpan.FromMilliseconds(60));
            var silence = new byte[Format.BytesPerSecond];
            var start = Stopwatch.GetTimestamp();
            long written = 0;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));

            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var chunk))
                {
                    if (padder.IsAhead(Stopwatch.GetElapsedTime(start), written, MaxLead))
                    {
                        continue; // device clock runs fast; drop to stay locked to wall clock
                    }

                    await pipe.WriteAsync(chunk, ct).ConfigureAwait(false);
                    written += chunk.Length;
                }

                var now = Stopwatch.GetTimestamp();
                var last = Volatile.Read(ref _lastDataTimestamp);
                var sinceData = last == 0 ? Stopwatch.GetElapsedTime(start, now) : Stopwatch.GetElapsedTime(last, now);
                var pad = padder.SilenceToWrite(Stopwatch.GetElapsedTime(start, now), written, sinceData);
                if (pad > 0)
                {
                    await pipe.WriteAsync(silence.AsMemory(0, pad), ct).ConfigureAwait(false);
                    written += pad;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (IOException)
        {
            // FFmpeg closed the pipe (it exited). The engine reports the process's reason, not ours.
        }
        catch (ObjectDisposedException)
        {
            // Disposed during shutdown.
        }

        _completion.TrySetResult();
    }

    private string Describe() => Source switch
    {
        MicrophoneSource m => m.DisplayName,
        SystemAudioSource s => s.DisplayName,
        ProcessAudioSource p => p.DisplayName,
        _ => "audio",
    };
}

/// <summary>Opens WASAPI-backed inputs for microphone, system-audio and program sources, sharing captures through the hub.</summary>
public sealed class WasapiAudioInputProvider(AudioCaptureHub hub) : IAudioInputProvider
{
    public WasapiAudioInputProvider()
        : this(new AudioCaptureHub())
    {
    }

    public bool CanOpen(AudioSourceSpec source) => source is MicrophoneSource or SystemAudioSource or ProcessAudioSource;

    public IAudioInput Open(AudioSourceSpec source) => new WasapiAudioInput(source, hub);
}

/// <summary>
/// Feeds an <see cref="AudioLevelBus"/> from a microphone, or from an output device's loopback when
/// <paramref name="loopback"/> is true, through the shared hub. If the stream is already capturing that
/// device, no second capture session is opened.
/// </summary>
public sealed class AudioLevelMonitor(AudioCaptureHub hub, AudioLevelBus bus, bool loopback = false) : IDisposable
{
    private AudioTap? _tap;
    private string? _source;

    public string? Problem { get; private set; }

    /// <summary>Starts (or switches) metering of <paramref name="deviceId"/> (null = the default device).</summary>
    public void Start(string? deviceId) =>
        Open("device:" + deviceId, handler => hub.Subscribe(deviceId, loopback, handler),
            loopback ? "System audio can't be metered: " : "Audio-reactive effects need a microphone: ");

    /// <summary>
    /// Starts (or switches) metering of a program's sound (<paramref name="include"/>), or of everything
    /// except that program. Shares the capture with the stream when both use it.
    /// </summary>
    public void StartProcess(uint processId, bool include) =>
        Open($"process:{processId.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{include}",
            handler => hub.SubscribeProcess(processId, include, handler), "This sound can't be metered: ");

    private void Open(string source, Func<AudioDataHandler, AudioTap> subscribe, string problemPrefix)
    {
        if (_tap is not null && _source == source)
        {
            return;
        }

        Stop();
        try
        {
            _tap = subscribe((data, silent) =>
            {
                if (silent)
                {
                    bus.Meter.Process(new float[data.Length / 4], TimeSpan.FromSeconds(data.Length / 4.0 / 2 / 48000));
                }
                else
                {
                    bus.Meter.ProcessPcm(data, _tap?.Format ?? new PcmFormat(48000, 2, PcmSampleFormat.F32));
                }
            });
            _source = source;
            bus.Active = true;
            Problem = null;
        }
        catch (StreamFailureException ex)
        {
            Problem = problemPrefix + ex.Error.UserMessage;
            bus.Active = false;
        }
    }

    public void Stop()
    {
        _tap?.Dispose();
        _tap = null;
        _source = null;
        bus.Active = false;
        bus.Meter.Reset();
    }

    public void Dispose() => Stop();
}

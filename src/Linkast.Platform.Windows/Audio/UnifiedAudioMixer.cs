// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Linkast.Core;
using Linkast.Core.Audio;
using Linkast.Core.Audio.Noise;
using Microsoft.Extensions.Logging;

namespace Linkast.Platform.Windows.Audio;

/// <summary>
/// Hardware audio mixer combining multiple WASAPI capture sessions (desktop audio, mic, game audio)
/// into a single continuous 48kHz stereo 16-bit PCM (s16le) stream.
/// Includes format normalization (int16/float32, mono upmixing, 44.1kHz->48kHz resampling)
/// and real-time microphone noise removal (RNNoise, see <see cref="MicrophoneNoiseRemover"/>).
/// <para>
/// The sound keeps to the picture's clock: wall time since the stream started, less the time the video lost
/// (frames the render loop could not deliver, given by <c>videoLostTime</c>). FFmpeg numbers both tracks by
/// count (samples and frames), so a stall that cost the picture two seconds must cost the sound the same two
/// seconds, or the two drift apart for the rest of the stream. When the sound is behind (its output was
/// blocked), the gap is filled with silence and the newest sound follows; when it is ahead, slices are dropped.
/// </para>
/// </summary>
public sealed partial class UnifiedAudioMixer : IAsyncDisposable, ILiveAudioControl
{
    private const int TargetSampleRate = 48000;
    private const int TargetChannels = 2;
    private const int BytesPerSample = 2; // 16-bit PCM
    private const int BytesPerSec = TargetSampleRate * TargetChannels * BytesPerSample; // 192,000 bytes/sec
    private const int BlockAlign = TargetChannels * BytesPerSample; // 4 bytes

    // 10ms processing interval: 480 frames = 960 floats (stereo) = 1920 bytes (s16le)
    private const int SamplesPerSlice = 480 * TargetChannels;
    private const int BytesPerSlice = 480 * BlockAlign;

    private readonly AudioCaptureHub _hub;
    private readonly Stream _audioPipeStream;
    private readonly long _streamStartTimestamp;
    private readonly ILogger? _logger;
    private readonly Func<TimeSpan>? _videoLostTime;

    private readonly ConcurrentDictionary<string, ChannelState> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly SilencePadder _padder;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _workerFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task? _mixTask;
    private long _totalBytesWritten;
    private long _lastDataTimestamp;
    private volatile bool _isRunning;
    private volatile NoiseRemoval _noiseRemoval;
    private bool _disposed;

    // For the log: how the sound was kept on the picture's clock.
    private long _droppedAheadSlices;
    private long _paddedSilenceBytes;
    private long _resyncSilenceBytes;
    private int _resyncs;
    private long _slowWrites;
    private long _longestWriteTicks;
    private long _maxLagTicks;
    private long _maxLeadTicks;

    /// <param name="videoLostTime">
    /// The time the picture has lost so far (frames the render loop could not deliver). The sound drops the same
    /// time so both stay on one timeline. Null: the wall clock alone.
    /// </param>
    public UnifiedAudioMixer(
        AudioCaptureHub hub,
        Stream audioPipeStream,
        long streamStartTimestamp = 0,
        ILogger? logger = null,
        Func<TimeSpan>? videoLostTime = null)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _audioPipeStream = audioPipeStream ?? throw new ArgumentNullException(nameof(audioPipeStream));
        _streamStartTimestamp = streamStartTimestamp > 0 ? streamStartTimestamp : Stopwatch.GetTimestamp();
        _logger = logger;
        _videoLostTime = videoLostTime;

        _padder = new SilencePadder(BytesPerSec, BlockAlign, TimeSpan.FromMilliseconds(40));
    }

    public long TotalBytesWritten => Interlocked.Read(ref _totalBytesWritten);

    /// <summary>A slice the audio may trail the master clock by before the mixer writes extra slices to catch up.</summary>
    private const int CatchUpThresholdSlices = 2;

    /// <summary>Most slices written in one tick to catch up (200 ms), so a long stall never floods the pipe.</summary>
    private const int MaxCatchUpSlices = 20;

    /// <summary>
    /// Behind the picture by more than this, the sound's output was blocked (FFmpeg stopped reading) or starved
    /// for longer than the channels can hold (200 ms): what is missing is gone, and silence takes its place.
    /// </summary>
    internal static readonly TimeSpan ResyncThreshold = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The picture's clock: time since the stream started, less what the video lost. Where the next sound
    /// belongs on the stream's timeline.
    /// </summary>
    private TimeSpan MediaElapsed(long now)
    {
        var elapsed = Stopwatch.GetElapsedTime(_streamStartTimestamp, now);
        var lost = _videoLostTime?.Invoke() ?? TimeSpan.Zero;
        return lost > TimeSpan.Zero ? elapsed - lost : elapsed;
    }

    /// <summary>
    /// How far the sound written so far trails the picture. Positive: viewers hear it later than they see what
    /// made it; negative: earlier. Near zero while everything keeps up.
    /// </summary>
    public TimeSpan ClockLag => MediaElapsed(Stopwatch.GetTimestamp()) - TimeSpan.FromSeconds((double)TotalBytesWritten / BytesPerSec);

    /// <summary>The furthest the sound trailed the picture since the mixer started.</summary>
    public TimeSpan MaxClockLag => TimeSpan.FromTicks(Interlocked.Read(ref _maxLagTicks));

    /// <summary>The furthest the sound ran ahead of the picture since the mixer started.</summary>
    public TimeSpan MaxClockLead => TimeSpan.FromTicks(Interlocked.Read(ref _maxLeadTicks));

    /// <summary>Times the sound fell so far behind that silence had to fill the gap.</summary>
    public int Resyncs => Volatile.Read(ref _resyncs);

    private void NoteLag()
    {
        var lag = ClockLag.Ticks;
        StoreMax(ref _maxLagTicks, lag);
        StoreMax(ref _maxLeadTicks, -lag);
    }

    private static void StoreMax(ref long field, long value)
    {
        long seen;
        while (value > (seen = Interlocked.Read(ref field)) && Interlocked.CompareExchange(ref field, value, seen) != seen)
        {
        }
    }

    /// <summary>
    /// A tick that arrives late (the OS timer is coarse, or the process was starved) writes only one slice, and
    /// the missed time was never made up while sound kept arriving: the audio fell further behind the video.
    /// Writes the slices that are due, as long as there is sound (or a gap to pad) to write.
    /// <para>
    /// Far behind (<see cref="ResyncThreshold"/>), the output was blocked and the sound of that time was dropped
    /// by the full channels: it can't come back. Silence fills the gap up to where the sound still waiting in
    /// the channels belongs, so that sound plays with its picture. Before, the gap was never filled and the
    /// sound stayed late for the rest of the stream (42 s in a tester's log).
    /// </para>
    /// </summary>
    internal async ValueTask CatchUpAsync(float[] mixFloatBuffer, byte[] outByteBuffer, byte[] silenceBuffer, CancellationToken ct)
    {
        var lag = ClockLag;
        if (lag > ResyncThreshold)
        {
            var waiting = TimeSpan.Zero;
            foreach (var channel in _channels.Values)
            {
                var buffered = channel.BufferedDuration;
                if (buffered > waiting)
                {
                    waiting = buffered;
                }
            }

            var bytes = (long)((lag - waiting - TimeSpan.FromMilliseconds(10)).TotalSeconds * BytesPerSec);
            bytes -= bytes % BlockAlign;
            if (bytes > 0)
            {
                var written = 0L;
                while (written < bytes)
                {
                    var chunk = (int)Math.Min(bytes - written, silenceBuffer.Length);
                    chunk -= chunk % BlockAlign;
                    await WriteAsync(silenceBuffer.AsMemory(0, chunk), ct).ConfigureAwait(false);
                    written += chunk;
                }

                Interlocked.Add(ref _resyncSilenceBytes, written);
                Interlocked.Increment(ref _resyncs);
                if (_logger is not null)
                {
                    LogResynced(_logger, lag.TotalMilliseconds, written * 1000.0 / BytesPerSec, waiting.TotalMilliseconds);
                }
            }
        }

        for (var i = 0; i < MaxCatchUpSlices && ClockLag > TimeSpan.FromSeconds((double)CatchUpThresholdSlices * BytesPerSlice / BytesPerSec); i++)
        {
            if (await MixOneSliceAsync(mixFloatBuffer, outByteBuffer, silenceBuffer, ct).ConfigureAwait(false) == 0)
            {
                break;
            }
        }
    }

    /// <summary>Writes to FFmpeg's audio pipe, timing it: a write that waits means FFmpeg is not reading.</summary>
    private async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var began = Stopwatch.GetTimestamp();
        await _audioPipeStream.WriteAsync(data, ct).ConfigureAwait(false);
        var took = Stopwatch.GetTimestamp() - began;
        Interlocked.Add(ref _totalBytesWritten, data.Length);
        if (took > Stopwatch.Frequency / 10)
        {
            Interlocked.Increment(ref _slowWrites);
            StoreMax(ref _longestWriteTicks, took);
        }
    }

    /// <summary>
    /// For the log: how the sound keeps to the picture, what each channel lost, and noise removal's work.
    /// Counts since this mixer (this connection) started.
    /// </summary>
    public string Describe()
    {
        var inv = CultureInfo.InvariantCulture;
        var lag = ClockLag;
        var parts = new List<string>
        {
            string.Create(inv, $"audio {Math.Abs(lag.TotalMilliseconds):0} ms {(lag >= TimeSpan.Zero ? "behind" : "ahead of")} the picture (most behind {Math.Max(MaxClockLag.TotalMilliseconds, lag.TotalMilliseconds):0} ms, most ahead {Math.Max(MaxClockLead.TotalMilliseconds, -lag.TotalMilliseconds):0} ms)"),
        };

        var dropped = Interlocked.Read(ref _droppedAheadSlices);
        var padded = Interlocked.Read(ref _paddedSilenceBytes);
        var resync = Interlocked.Read(ref _resyncSilenceBytes);
        if (dropped > 0 || padded > 0 || resync > 0)
        {
            parts.Add(string.Create(inv, $"audio kept in step: {dropped * 10} ms dropped while ahead, {padded * 1000.0 / BytesPerSec:0} ms silence while no sound came, {resync * 1000.0 / BytesPerSec:0} ms silence after {Resyncs} stall(s)"));
        }

        var slow = Interlocked.Read(ref _slowWrites);
        if (slow > 0)
        {
            parts.Add(string.Create(inv, $"audio output waited {slow} time(s), longest {Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _longestWriteTicks)).TotalMilliseconds:0} ms"));
        }

        foreach (var channel in _channels.Values)
        {
            if (channel.Describe() is { } line)
            {
                parts.Add(line);
            }
        }

        return string.Join("; ", parts);
    }

    public bool IsRunning => _isRunning;

    public bool NoiseSuppressionEnabled => _noiseRemoval != NoiseRemoval.Off;

    /// <summary>How much noise is removed from the microphone.</summary>
    public NoiseRemoval NoiseRemoval => _noiseRemoval;

    /// <summary>
    /// Adds or attaches a captured audio source to the mixer.
    /// </summary>
    public void AddSource(string channelName, AudioSourceSpec source, double initialGain = 1.0)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);

        var state = new ChannelState(channelName, initialGain, this);
        if (!_channels.TryAdd(channelName, state))
        {
            return;
        }

        try
        {
            var tap = source switch
            {
                SystemAudioSource { ExcludeProcessId: { } exclude } => _hub.SubscribeProcess(exclude, false, (d, s) => state.EnqueueData(d, s)),
                SystemAudioSource s => _hub.Subscribe(s.DeviceId, true, (d, s) => state.EnqueueData(d, s)),
                MicrophoneSource m => _hub.Subscribe(m.DeviceId, false, (d, s) => state.EnqueueData(d, s)),
                ProcessAudioSource p => _hub.SubscribeProcess(p.ProcessId, true, (d, s) => state.EnqueueData(d, s)),
                _ => throw new ArgumentException($"Unsupported audio source: {source.GetType().Name}", nameof(source))
            };
            state.Tap = tap;
        }
        catch (Exception ex)
        {
            if (_logger is not null)
            {
                LogAddSourceFailed(_logger, channelName, ex);
            }
            _channels.TryRemove(channelName, out _);
            throw;
        }
    }

    /// <summary>True when the mixer has this channel.</summary>
    public bool HasChannel(string channelName) => _channels.ContainsKey(channelName);

    /// <summary>Internal helper for unit testing channel data without a physical WASAPI device.</summary>
    internal void AddTestChannel(string channelName, double initialGain = 1.0, PcmFormat? format = null)
    {
        var state = new ChannelState(channelName, initialGain, this)
        {
            OverrideFormat = format
        };
        _channels[channelName] = state;
    }

    /// <summary>Internal helper for feeding raw bytes directly to a channel in unit tests.</summary>
    internal void EnqueueTestChannelData(string channelName, ReadOnlySpan<byte> data, bool silent = false)
    {
        if (_channels.TryGetValue(channelName, out var state))
        {
            state.EnqueueData(data, silent);
        }
    }

    /// <summary>
    /// Starts the mixer worker loop.
    /// </summary>
    public void Start()
    {
        if (_isRunning)
        {
            return;
        }

        _isRunning = true;
        _mixTask = Task.Run(() => MixLoopAsync(_cts.Token));
    }

    private async Task MixLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        var mixFloatBuffer = new float[SamplesPerSlice];
        var outByteBuffer = new byte[BytesPerSlice];
        var silenceBuffer = new byte[BytesPerSec];

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await MixOneSliceAsync(mixFloatBuffer, outByteBuffer, silenceBuffer, ct).ConfigureAwait(false);
                await CatchUpAsync(mixFloatBuffer, outByteBuffer, silenceBuffer, ct).ConfigureAwait(false);
                NoteLag();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Clean shutdown
        }
        catch (IOException ex)
        {
            // The audio pipe is gone: FFmpeg ended (a stop, or a reconnect that starts a new mixer).
            if (_logger is not null)
            {
                LogOutputClosed(_logger, ex.Message);
            }
        }
        catch (Exception ex)
        {
            if (_logger is not null)
            {
                LogMixLoopFailed(_logger, ex);
            }
        }
        finally
        {
            _isRunning = false;
            _workerFinished.TrySetResult();
        }
    }

    internal async ValueTask<int> MixOneSliceAsync(
        float[]? mixFloatBuffer = null,
        byte[]? outByteBuffer = null,
        byte[]? silenceBuffer = null,
        CancellationToken ct = default)
    {
        mixFloatBuffer ??= new float[SamplesPerSlice];
        outByteBuffer ??= new byte[BytesPerSlice];
        silenceBuffer ??= new byte[BytesPerSec];

        var now = Stopwatch.GetTimestamp();
        var elapsed = MediaElapsed(now);

        // Ahead of the picture (a device clock that runs fast, or the video lost time): drop this slice.
        if (_padder.IsAhead(elapsed, Interlocked.Read(ref _totalBytesWritten), TimeSpan.FromMilliseconds(50)))
        {
            DrainOneSliceFromAllChannels();
            Interlocked.Increment(ref _droppedAheadSlices);
            return 0;
        }

        Array.Clear(mixFloatBuffer, 0, mixFloatBuffer.Length);
        bool hasAudio = false;

        // Mix all active channels
        foreach (var channel in _channels.Values)
        {
            if (channel.TryReadSlice(mixFloatBuffer))
            {
                hasAudio = true;
            }
        }

        if (hasAudio)
        {
            Volatile.Write(ref _lastDataTimestamp, now);

            // Convert float32 [-1.0, 1.0] to int16 s16le with soft clamping
            for (int i = 0; i < SamplesPerSlice; i++)
            {
                int s16 = (int)Math.Round(mixFloatBuffer[i] * 32767.0f);
                short clamped = (short)Math.Clamp(s16, short.MinValue, short.MaxValue);

                int byteIndex = i * 2;
                outByteBuffer[byteIndex] = (byte)(clamped & 0xFF);
                outByteBuffer[byteIndex + 1] = (byte)((clamped >> 8) & 0xFF);
            }

            await WriteAsync(outByteBuffer.AsMemory(0, BytesPerSlice), ct).ConfigureAwait(false);
            return BytesPerSlice;
        }
        else
        {
            // Check if silence padding is required to preserve monotonic clock continuity
            var last = Volatile.Read(ref _lastDataTimestamp);
            var sinceData = last == 0 ? Stopwatch.GetElapsedTime(_streamStartTimestamp, now) : Stopwatch.GetElapsedTime(last, now);
            var pad = _padder.SilenceToWrite(elapsed, Interlocked.Read(ref _totalBytesWritten), sinceData);

            if (pad > 0)
            {
                await WriteAsync(silenceBuffer.AsMemory(0, pad), ct).ConfigureAwait(false);
                Interlocked.Add(ref _paddedSilenceBytes, pad);
                return pad;
            }
            return 0;
        }
    }

    private void DrainOneSliceFromAllChannels()
    {
        foreach (var channel in _channels.Values)
        {
            channel.DiscardSlice();
        }
    }

    public Task<bool> SetGainAsync(string channel, double gain, CancellationToken cancellationToken = default)
    {
        if (_channels.TryGetValue(channel, out var state))
        {
            state.Gain = (float)Math.Clamp(gain, 0.0, 10.0);
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public Task<bool> SetNoiseSuppressionAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SetNoiseRemovalAsync(enabled ? NoiseRemoval.Standard : NoiseRemoval.Off, cancellationToken);

    public Task<bool> SetNoiseRemovalAsync(NoiseRemoval level, CancellationToken cancellationToken = default)
    {
        _noiseRemoval = Enum.IsDefined(level) ? level : NoiseRemoval.Off;
        return Task.FromResult(true);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();

        if (_isRunning && _mixTask is not null)
        {
            await _workerFinished.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }

        foreach (var channel in _channels.Values)
        {
            channel.Dispose();
        }
        _channels.Clear();

        _cts.Dispose();
    }

    private sealed class ChannelState : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly Queue<float> _buffer = new(TargetSampleRate); // Holds up to 1 second
        private volatile float _gain;
        private readonly UnifiedAudioMixer _mixer;
        private readonly MicrophoneNoiseRemover? _noiseRemover;
        private long _overflowSamples;

        private float _lastSampleL;
        private float _lastSampleR;
        private double _resamplePhase;

        public ChannelState(string name, double initialGain, UnifiedAudioMixer mixer)
        {
            Name = name;
            _gain = (float)Math.Clamp(initialGain, 0.0, 10.0);
            _mixer = mixer;
            if (string.Equals(name, AudioChannels.Microphone, StringComparison.OrdinalIgnoreCase))
            {
                _noiseRemover = new MicrophoneNoiseRemover();
            }
        }

        public string Name { get; }

        public AudioTap? Tap { get; set; }

        public PcmFormat? OverrideFormat { get; set; }

        public PcmFormat Format => OverrideFormat ?? Tap?.Format ?? new PcmFormat(TargetSampleRate, TargetChannels, PcmSampleFormat.F32);

        public float Gain
        {
            get => _gain;
            set => _gain = value;
        }

        /// <summary>Sound waiting to be mixed.</summary>
        public TimeSpan BufferedDuration
        {
            get
            {
                lock (_gate)
                {
                    return TimeSpan.FromSeconds((double)_buffer.Count / (TargetSampleRate * TargetChannels));
                }
            }
        }

        /// <summary>For the log: sound this channel lost, and noise removal's work. Null when there is nothing to say.</summary>
        public string? Describe()
        {
            var inv = CultureInfo.InvariantCulture;
            var parts = new List<string>();
            var overflow = Interlocked.Read(ref _overflowSamples);
            if (overflow > 0)
            {
                parts.Add(string.Create(inv, $"{overflow * 1000.0 / (TargetSampleRate * TargetChannels):0} ms dropped (not mixed in time)"));
            }

            if (_noiseRemover is { BlocksProcessed: > 0 } nr)
            {
                parts.Add(string.Create(inv, $"noise removal {_mixer.NoiseRemoval} for {nr.BlocksProcessed / 100.0:0} s, speech in {nr.SpeechShare:P0}, CPU {nr.CoreShare:P1} of one core"));
            }

            return parts.Count == 0 ? null : $"sound '{Name}': {string.Join(", ", parts)}";
        }

        public void EnqueueData(ReadOnlySpan<byte> data, bool silent)
        {
            if (data.IsEmpty)
            {
                return;
            }

            var format = Format;
            int inChannels = format.Channels > 0 ? format.Channels : TargetChannels;
            int inSampleRate = format.SampleRate > 0 ? format.SampleRate : TargetSampleRate;
            bool isFloat = format.SampleFormat == PcmSampleFormat.F32;

            int bytesPerInputSample = isFloat ? 4 : 2;
            int frameBytes = inChannels * bytesPerInputSample;
            int inFrameCount = data.Length / frameBytes;
            if (inFrameCount <= 0)
            {
                return;
            }

            lock (_gate)
            {
                if (silent || _gain == 0)
                {
                    int outFrameCount = inSampleRate == TargetSampleRate
                        ? inFrameCount
                        : (int)Math.Round((double)inFrameCount * TargetSampleRate / inSampleRate);

                    int samplesToAdd = outFrameCount * TargetChannels;
                    for (int i = 0; i < samplesToAdd; i++)
                    {
                        _buffer.Enqueue(0f);
                    }
                }
                else
                {
                    double step = (double)inSampleRate / TargetSampleRate;

                    for (int f = 0; f < inFrameCount; f++)
                    {
                        int offset = f * frameBytes;
                        float sampleL;
                        float sampleR;

                        if (isFloat)
                        {
                            var floatSpan = MemoryMarshal.Cast<byte, float>(data[offset..]);
                            sampleL = floatSpan[0];
                            sampleR = inChannels > 1 ? floatSpan[1] : sampleL;
                        }
                        else
                        {
                            var shortSpan = MemoryMarshal.Cast<byte, short>(data[offset..]);
                            sampleL = shortSpan[0] / 32768.0f;
                            sampleR = inChannels > 1 ? shortSpan[1] / 32768.0f : sampleL;
                        }

                        if (inSampleRate == TargetSampleRate)
                        {
                            _buffer.Enqueue(sampleL);
                            _buffer.Enqueue(sampleR);
                        }
                        else
                        {
                            while (_resamplePhase < 1.0)
                            {
                                float fraction = (float)_resamplePhase;
                                float outL = _lastSampleL + fraction * (sampleL - _lastSampleL);
                                float outR = _lastSampleR + fraction * (sampleR - _lastSampleR);
                                _buffer.Enqueue(outL);
                                _buffer.Enqueue(outR);
                                _resamplePhase += step;
                            }

                            _resamplePhase -= 1.0;
                            _lastSampleL = sampleL;
                            _lastSampleR = sampleR;
                        }
                    }
                }

                // Bound buffer size to 200 ms to avoid memory growth if consumer falls behind
                int maxCapacity = (int)(TargetSampleRate * TargetChannels * 0.2);
                while (_buffer.Count > maxCapacity)
                {
                    _buffer.Dequeue();
                    _overflowSamples++;
                }
            }
        }

        public bool TryReadSlice(Span<float> destination)
        {
            float currentGain = _gain;
            lock (_gate)
            {
                if (_buffer.Count < SamplesPerSlice)
                {
                    return false;
                }

                Span<float> slice = stackalloc float[SamplesPerSlice];
                for (int i = 0; i < SamplesPerSlice; i++)
                {
                    slice[i] = _buffer.Dequeue();
                }

                // Noise removal hears the microphone as it is, before the fader: the network was trained on
                // ordinary recording levels, and a fader at 560 % must not change what it takes for noise.
                if (_noiseRemover is not null)
                {
                    _noiseRemover.Level = _mixer.NoiseRemoval;
                    _noiseRemover.Process(slice);
                }

                for (int i = 0; i < SamplesPerSlice; i++)
                {
                    destination[i] += slice[i] * currentGain;
                }

                return true;
            }
        }

        public void DiscardSlice()
        {
            lock (_gate)
            {
                int toDiscard = Math.Min(_buffer.Count, SamplesPerSlice);
                for (int i = 0; i < toDiscard; i++)
                {
                    _buffer.Dequeue();
                }
            }
        }

        public void Dispose()
        {
            Tap?.Dispose();
            Tap = null;
        }
    }

    [LoggerMessage(LogLevel.Warning, "Audio was {Lag:0} ms behind the picture (its output was blocked); {Silence:0} ms of silence keep it in step, then the {Waiting:0} ms of sound still waiting.")]
    private static partial void LogResynced(ILogger logger, double lag, double silence, double waiting);

    [LoggerMessage(LogLevel.Debug, "Audio output closed; the mixer loop ends ({Reason}).")]
    private static partial void LogOutputClosed(ILogger logger, string reason);

    [LoggerMessage(LogLevel.Error, "Audio mixer loop failed with exception.")]
    private static partial void LogMixLoopFailed(ILogger logger, Exception ex);

    [LoggerMessage(LogLevel.Warning, "Failed to attach audio source for channel '{Channel}'.")]
    private static partial void LogAddSourceFailed(ILogger logger, string channel, Exception ex);
}

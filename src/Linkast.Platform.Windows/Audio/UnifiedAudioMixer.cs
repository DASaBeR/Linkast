// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Linkast.Core;
using Linkast.Core.Audio;
using Microsoft.Extensions.Logging;

namespace Linkast.Platform.Windows.Audio;

/// <summary>
/// Hardware audio mixer combining multiple WASAPI capture sessions (desktop audio, mic, game audio)
/// into a single continuous 48kHz stereo 16-bit PCM (s16le) stream.
/// Includes format normalization (int16/float32, mono upmixing, 44.1kHz->48kHz resampling)
/// and real-time microphone noise suppression.
/// Timeline is locked to the same monotonic master clock as the video scheduler to eliminate A/V drift.
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

    private readonly ConcurrentDictionary<string, ChannelState> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly SilencePadder _padder;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _workerFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task? _mixTask;
    private long _totalBytesWritten;
    private long _lastDataTimestamp;
    private volatile bool _isRunning;
    private volatile bool _noiseSuppressionEnabled;
    private bool _disposed;

    public UnifiedAudioMixer(
        AudioCaptureHub hub,
        Stream audioPipeStream,
        long streamStartTimestamp = 0,
        ILogger? logger = null)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _audioPipeStream = audioPipeStream ?? throw new ArgumentNullException(nameof(audioPipeStream));
        _streamStartTimestamp = streamStartTimestamp > 0 ? streamStartTimestamp : Stopwatch.GetTimestamp();
        _logger = logger;

        _padder = new SilencePadder(BytesPerSec, BlockAlign, TimeSpan.FromMilliseconds(40));
    }

    public long TotalBytesWritten => Interlocked.Read(ref _totalBytesWritten);

    public bool IsRunning => _isRunning;

    public bool NoiseSuppressionEnabled => _noiseSuppressionEnabled;

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
        var elapsed = Stopwatch.GetElapsedTime(_streamStartTimestamp, now);

        // Check if audio has run ahead of monotonic clock
        if (_padder.IsAhead(elapsed, Interlocked.Read(ref _totalBytesWritten), TimeSpan.FromMilliseconds(50)))
        {
            // Drop this slice to stay locked to master clock
            DrainOneSliceFromAllChannels();
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

            await _audioPipeStream.WriteAsync(outByteBuffer.AsMemory(0, BytesPerSlice), ct).ConfigureAwait(false);
            Interlocked.Add(ref _totalBytesWritten, BytesPerSlice);
            return BytesPerSlice;
        }
        else
        {
            // Check if silence padding is required to preserve monotonic clock continuity
            var last = Volatile.Read(ref _lastDataTimestamp);
            var sinceData = last == 0 ? elapsed : Stopwatch.GetElapsedTime(last, now);
            var pad = _padder.SilenceToWrite(elapsed, Interlocked.Read(ref _totalBytesWritten), sinceData);

            if (pad > 0)
            {
                await _audioPipeStream.WriteAsync(silenceBuffer.AsMemory(0, pad), ct).ConfigureAwait(false);
                Interlocked.Add(ref _totalBytesWritten, pad);
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

    public Task<bool> SetNoiseSuppressionAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        _noiseSuppressionEnabled = enabled;
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
        private readonly bool _isMicrophone;
        private readonly UnifiedAudioMixer _mixer;
        private readonly SpeechGate? _noiseSuppressor;
        private bool _noiseSuppressorWasOn;

        private float _lastSampleL;
        private float _lastSampleR;
        private double _resamplePhase;

        public ChannelState(string name, double initialGain, UnifiedAudioMixer mixer)
        {
            Name = name;
            _gain = (float)Math.Clamp(initialGain, 0.0, 10.0);
            _mixer = mixer;
            _isMicrophone = string.Equals(name, AudioChannels.Microphone, StringComparison.OrdinalIgnoreCase);
            if (_isMicrophone)
            {
                _noiseSuppressor = new SpeechGate();
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
                    slice[i] = _buffer.Dequeue() * currentGain;
                }

                // Apply real-time noise suppression on microphone channel when enabled
                if (_isMicrophone && _noiseSuppressor is not null)
                {
                    var on = _mixer.NoiseSuppressionEnabled;
                    if (on && !_noiseSuppressorWasOn)
                    {
                        _noiseSuppressor.Reset(); // old audio in its delay line must not be replayed
                    }

                    _noiseSuppressorWasOn = on;
                    if (on)
                    {
                        _noiseSuppressor.Process(slice);
                    }
                }

                for (int i = 0; i < SamplesPerSlice; i++)
                {
                    destination[i] += slice[i];
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

    [LoggerMessage(LogLevel.Debug, "Audio output closed; the mixer loop ends ({Reason}).")]
    private static partial void LogOutputClosed(ILogger logger, string reason);

    [LoggerMessage(LogLevel.Error, "Audio mixer loop failed with exception.")]
    private static partial void LogMixLoopFailed(ILogger logger, Exception ex);

    [LoggerMessage(LogLevel.Warning, "Failed to attach audio source for channel '{Channel}'.")]
    private static partial void LogAddSourceFailed(ILogger logger, string channel, Exception ex);
}

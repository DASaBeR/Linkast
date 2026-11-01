// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Linkast.Core;
using Linkast.Core.Audio;
using Microsoft.Extensions.Logging;

namespace Linkast.Platform.Windows.Audio;

/// <summary>
/// Hardware audio mixer combining multiple WASAPI capture sessions (desktop audio, mic, game audio)
/// into a single continuous 48kHz stereo 16-bit PCM (s16le) stream.
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

    /// <summary>
    /// Adds or attaches a captured audio source to the mixer.
    /// </summary>
    public void AddSource(string channelName, AudioSourceSpec source, double initialGain = 1.0)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);

        var state = new ChannelState(channelName, initialGain);
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
                var now = Stopwatch.GetTimestamp();
                var elapsed = Stopwatch.GetElapsedTime(_streamStartTimestamp, now);

                // Check if audio has run ahead of monotonic clock
                if (_padder.IsAhead(elapsed, Interlocked.Read(ref _totalBytesWritten), TimeSpan.FromMilliseconds(50)))
                {
                    // Drop this slice to stay locked to master clock
                    DrainOneSliceFromAllChannels();
                    continue;
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
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Clean shutdown
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
            state.Gain = (float)Math.Clamp(gain, 0.0, 4.0);
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public Task<bool> SetNoiseSuppressionAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        // Future expansion: hook into WebRTC or RNNoise DSP filter in software
        return Task.FromResult(false);
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

        public ChannelState(string name, double initialGain)
        {
            Name = name;
            _gain = (float)Math.Clamp(initialGain, 0.0, 4.0);
        }

        public string Name { get; }

        public AudioTap? Tap { get; set; }

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

            int floatCount = data.Length / sizeof(float);
            lock (_gate)
            {
                if (silent || _gain == 0)
                {
                    for (int i = 0; i < floatCount; i++)
                    {
                        _buffer.Enqueue(0f);
                    }
                }
                else
                {
                    var floatSpan = MemoryMarshal.Cast<byte, float>(data);
                    for (int i = 0; i < floatSpan.Length; i++)
                    {
                        _buffer.Enqueue(floatSpan[i]);
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

                for (int i = 0; i < SamplesPerSlice; i++)
                {
                    destination[i] += _buffer.Dequeue() * currentGain;
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
            lock (_gate)
            {
                _buffer.Clear();
            }
        }
    }

    [LoggerMessage(LogLevel.Error, "Failed to add audio source for channel {ChannelName}.")]
    private static partial void LogAddSourceFailed(ILogger logger, string channelName, Exception ex);

    [LoggerMessage(LogLevel.Error, "Audio mix loop error occurred.")]
    private static partial void LogMixLoopFailed(ILogger logger, Exception ex);
}

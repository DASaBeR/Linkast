// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core.Audio;

/// <summary>
/// Measures loudness for audio-reactive visuals. Read-only: it never changes the audio. Fast attack
/// and slow release make the level look natural; speaking detection uses a threshold with hysteresis
/// and a hold time so it doesn't flicker between words. Thread-safe for one writer and many readers.
/// </summary>
public sealed class AudioLevelMeter
{
    private const double FloorDb = -60;
    private const double AttackSeconds = 0.03;
    private const double ReleaseSeconds = 0.3;
    private static readonly TimeSpan SpeakingHold = TimeSpan.FromMilliseconds(250);

    private double _levelDb = FloorDb;
    private TimeSpan _belowSince = TimeSpan.Zero;
    private TimeSpan _clock;
    private volatile float _level;
    private volatile bool _speaking;

    /// <summary>Speaking starts above this level, in dBFS.</summary>
    public double ThresholdDb { get; set; } = -40;

    /// <summary>Smoothed loudness, 0 (silence, -60 dBFS or below) to 1 (full scale).</summary>
    public float Level => _level;

    /// <summary>Smoothed level in dBFS.</summary>
    public double LevelDb => Volatile.Read(ref _levelDb);

    public bool Speaking => _speaking;

    /// <summary>Feeds interleaved float samples covering <paramref name="duration"/>.</summary>
    public void Process(ReadOnlySpan<float> samples, TimeSpan duration)
    {
        if (samples.IsEmpty || duration <= TimeSpan.Zero)
        {
            return;
        }

        double sum = 0;
        foreach (var s in samples)
        {
            sum += s * s;
        }

        var rms = Math.Sqrt(sum / samples.Length);
        var blockDb = rms <= 1e-6 ? FloorDb : Math.Max(FloorDb, 20 * Math.Log10(rms));

        var current = Volatile.Read(ref _levelDb);
        var tau = blockDb > current ? AttackSeconds : ReleaseSeconds;
        var k = 1 - Math.Exp(-duration.TotalSeconds / tau);
        var next = current + ((blockDb - current) * k);
        Volatile.Write(ref _levelDb, next);
        _level = (float)Math.Clamp((next - FloorDb) / -FloorDb, 0, 1);

        _clock += duration;
        if (next >= ThresholdDb)
        {
            _speaking = true;
            _belowSince = _clock;
        }
        else if (_speaking && next < ThresholdDb - 6 && _clock - _belowSince > SpeakingHold)
        {
            _speaking = false;
        }
        else if (next >= ThresholdDb - 6)
        {
            _belowSince = _clock; // still in the hysteresis band: keep the hold timer fresh
        }
    }

    /// <summary>Feeds 48 kHz-style interleaved float PCM bytes.</summary>
    public void ProcessPcm(ReadOnlySpan<byte> pcm, PcmFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (format.SampleFormat != PcmSampleFormat.F32 || pcm.Length < 4)
        {
            return;
        }

        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(pcm);
        var frames = samples.Length / format.Channels;
        Process(samples, TimeSpan.FromSeconds((double)frames / format.SampleRate));
    }

    public void Reset()
    {
        Volatile.Write(ref _levelDb, FloorDb);
        _level = 0;
        _speaking = false;
    }
}

/// <summary>
/// The current audio level, published by whichever component owns the microphone and read by
/// renderers for audio-reactive effects. One per app.
/// </summary>
public sealed class AudioLevelBus
{
    public AudioLevelMeter Meter { get; } = new();

    /// <summary>True while a level source is attached and running.</summary>
    public bool Active { get; set; }
}

// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core.Audio;

/// <summary>
/// The sound of one source waiting to be mixed (48 kHz interleaved stereo float), kept close to the
/// picture. Each source runs on its own clock: a USB microphone or a sound card is often 0.01 % fast or slow
/// against the PC, and a capture can hand over a burst after a hiccup. What arrived early used to stay
/// waiting for the rest of the stream (up to 200 ms, then the oldest sound was dropped), so that source was
/// heard late: a lip-sync error that comes and goes over a long stream. A slow clock ran dry now and then
/// instead, a 10 ms gap each time.
/// <para>
/// Once a second this looks at the least sound left after a read during that second. More than
/// <see cref="HighWater"/>: the source plays 0.5 % faster until <see cref="Target"/> is left. Less than
/// <see cref="LowWater"/>, or it ran dry while sound came: 0.5 % slower until it keeps <see cref="Target"/>
/// again. 0.5 % is below what ears notice (8 cents of pitch); 100 ms are made up in 20 s.
/// </para>
/// Not thread-safe: the owner locks.
/// </summary>
public sealed class JitterBuffer
{
    /// <summary>The sound left waiting after a read in steady state: enough to ride out a late capture packet.</summary>
    public static readonly TimeSpan Target = TimeSpan.FromMilliseconds(20);

    /// <summary>More than this left for a whole second is played away faster.</summary>
    public static readonly TimeSpan HighWater = TimeSpan.FromMilliseconds(40);

    /// <summary>Less than this left at some point in a second: built back up by playing slower.</summary>
    public static readonly TimeSpan LowWater = TimeSpan.FromMilliseconds(5);

    /// <summary>The most that is kept; the oldest sound beyond it is dropped.</summary>
    public static readonly TimeSpan Capacity = TimeSpan.FromMilliseconds(200);

    /// <summary>How much faster or slower the sound plays while it is brought back to <see cref="Target"/>.</summary>
    public const double RateStep = 0.005;

    private const int SampleRate = 48000;
    private const int ReadsPerWindow = 100; // reads of 10 ms: one second

    private static readonly int TargetFrames = FramesIn(Target);
    private static readonly int HighWaterFrames = FramesIn(HighWater);
    private static readonly int LowWaterFrames = FramesIn(LowWater);
    private static readonly int CapacityFrames = FramesIn(Capacity);

    private readonly float[] _ring = new float[CapacityFrames * 2];
    private int _head;
    private int _frames;
    private double _phase;

    private int _reads;
    private int _windowMin = int.MaxValue;
    private int _windowUnderruns;

    /// <summary>Stereo frames waiting.</summary>
    public int Frames => _frames;

    /// <summary>The sound waiting.</summary>
    public TimeSpan Buffered => TimeSpan.FromSeconds((double)_frames / SampleRate);

    /// <summary>−1 slower (building up), 0 as it comes, +1 faster (catching up).</summary>
    public int Rate { get; private set; }

    /// <summary>Frames dropped because more than <see cref="Capacity"/> was waiting.</summary>
    public long OverflowFrames { get; private set; }

    /// <summary>Frames played away by running faster (sound that would otherwise have stayed late).</summary>
    public long FramesCaughtUp { get; private set; }

    /// <summary>Frames added by running slower (gaps that would otherwise have been heard).</summary>
    public long FramesStretched { get; private set; }

    /// <summary>Reads that found too little sound.</summary>
    public long Underruns { get; private set; }

    /// <summary>Adds one stereo frame. Beyond <see cref="Capacity"/> the oldest frame is dropped.</summary>
    public void Write(float left, float right)
    {
        if (_frames == CapacityFrames)
        {
            _head = (_head + 1) % CapacityFrames;
            _frames--;
            OverflowFrames++;
        }

        var at = (_head + _frames) % CapacityFrames;
        _ring[2 * at] = left;
        _ring[(2 * at) + 1] = right;
        _frames++;
    }

    /// <summary>Drops up to <paramref name="frames"/> of the oldest sound.</summary>
    public void Discard(int frames)
    {
        var n = Math.Clamp(frames, 0, _frames);
        _head = (_head + n) % CapacityFrames;
        _frames -= n;
        _phase = 0;
    }

    /// <summary>
    /// Fills <paramref name="destination"/> (interleaved stereo) at the current rate. False, reading nothing,
    /// when too little sound is waiting.
    /// </summary>
    public bool TryRead(Span<float> destination)
    {
        var outFrames = destination.Length / 2;
        var step = 1 + (Rate * RateStep);

        // The input frames this read reaches: between frames, the last one is interpolated towards the next.
        var exact = Rate == 0 && _phase == 0;
        var needed = exact ? outFrames : (int)(_phase + ((outFrames - 1) * step)) + 2;
        if (_frames < needed)
        {
            Underruns++;
            _windowUnderruns++;
            EndOfRead();
            return false;
        }

        if (exact)
        {
            for (var i = 0; i < outFrames; i++)
            {
                var at = 2 * ((_head + i) % CapacityFrames);
                destination[2 * i] = _ring[at];
                destination[(2 * i) + 1] = _ring[at + 1];
            }

            _head = (_head + outFrames) % CapacityFrames;
            _frames -= outFrames;
        }
        else
        {
            var position = _phase;
            for (var i = 0; i < outFrames; i++)
            {
                var whole = (int)position;
                var fraction = (float)(position - whole);
                var a = 2 * ((_head + whole) % CapacityFrames);
                var b = 2 * ((_head + whole + 1) % CapacityFrames);
                destination[2 * i] = _ring[a] + ((_ring[b] - _ring[a]) * fraction);
                destination[(2 * i) + 1] = _ring[a + 1] + ((_ring[b + 1] - _ring[a + 1]) * fraction);
                position += step;
            }

            var consumed = (int)position;
            if (consumed > outFrames)
            {
                FramesCaughtUp += consumed - outFrames;
            }
            else
            {
                FramesStretched += outFrames - consumed;
            }

            _head = (_head + consumed) % CapacityFrames;
            _frames -= consumed;
            _phase = position - consumed;
        }

        _windowMin = Math.Min(_windowMin, _frames);
        EndOfRead();
        return true;
    }

    private static int FramesIn(TimeSpan t) => (int)(t.TotalSeconds * SampleRate);

    /// <summary>Once a second: sets the rate from the least sound left after a read, and whether it ran dry.</summary>
    private void EndOfRead()
    {
        if (++_reads < ReadsPerWindow)
        {
            return;
        }

        // A second without any sound is a quiet source (a loopback hands over nothing while nothing plays),
        // not a slow clock: it says nothing about the rate.
        var soundCame = _windowUnderruns < ReadsPerWindow;
        if (soundCame)
        {
            var low = _windowUnderruns > 0 || _windowMin < LowWaterFrames;
            Rate = Rate switch
            {
                > 0 when _windowMin <= TargetFrames => 0,
                < 0 when !low && _windowMin >= TargetFrames => 0,
                0 when _windowMin > HighWaterFrames => 1,
                0 when low => -1,
                _ => Rate,
            };
        }

        if (Rate == 0)
        {
            // Read whole frames again: between two frames the sound would keep losing some of its highs.
            _phase = 0;
        }

        _reads = 0;
        _windowMin = int.MaxValue;
        _windowUnderruns = 0;
    }
}

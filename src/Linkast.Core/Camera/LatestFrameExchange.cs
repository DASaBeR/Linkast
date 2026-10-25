// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core.Camera;

/// <summary>An uncompressed BGRA frame in a reusable buffer.</summary>
public sealed class BgraFrame
{
    public BgraFrame(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        Pixels = new byte[width * height * 4];
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride => Width * 4;

    public byte[] Pixels { get; }

    /// <summary>Increments for every new frame written into the exchange.</summary>
    public long Sequence { get; internal set; }

    public TimeSpan Timestamp { get; internal set; }

    internal int Slot { get; set; }
}

/// <summary>
/// Latest-frame-wins buffer pool for real-time video. The producer never waits: stale frames are
/// overwritten, never queued, so latency cannot build up. Several consumers (preview, stream,
/// later a virtual camera) may each hold the newest frame while reading it.
/// </summary>
public sealed class LatestFrameExchange
{
    /// <summary>Latest + one being written + up to two concurrent readers on older frames.</summary>
    private const int Slots = 4;

    private readonly Lock _gate = new();
    private readonly BgraFrame?[] _buffers = new BgraFrame?[Slots];
    private readonly int[] _readers = new int[Slots];
    private int _writeIndex = -1;
    private int _latestIndex = -1;
    private long _sequence;
    private long _dropped;

    /// <summary>Total frames published.</summary>
    public long PublishedCount => Interlocked.Read(ref _sequence);

    /// <summary>Frames the producer had to drop because every buffer was in use.</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Gets a buffer to write the next frame into, or null if all buffers are busy (drop the frame).
    /// Call <see cref="Publish"/> when the frame is complete. Single producer only.
    /// </summary>
    public BgraFrame? BeginWrite(int width, int height)
    {
        lock (_gate)
        {
            _writeIndex = -1;
            for (var i = 0; i < Slots; i++)
            {
                if (i != _latestIndex && _readers[i] == 0)
                {
                    _writeIndex = i;
                    break;
                }
            }

            if (_writeIndex < 0)
            {
                _dropped++;
                return null;
            }

            var f = _buffers[_writeIndex];
            if (f is null || f.Width != width || f.Height != height)
            {
                f = new BgraFrame(width, height) { Slot = _writeIndex };
                _buffers[_writeIndex] = f;
            }

            return f;
        }
    }

    public void Publish(TimeSpan timestamp)
    {
        lock (_gate)
        {
            if (_writeIndex < 0)
            {
                throw new InvalidOperationException("Publish without a successful BeginWrite.");
            }

            var f = _buffers[_writeIndex]!;
            f.Sequence = ++_sequence;
            f.Timestamp = timestamp;
            _latestIndex = _writeIndex;
            _writeIndex = -1;
        }
    }

    /// <summary>
    /// Returns the newest frame if it is newer than <paramref name="lastSequence"/>, reserved for
    /// reading until <see cref="EndRead"/> is called with it. Otherwise null.
    /// </summary>
    public BgraFrame? TryBeginRead(long lastSequence)
    {
        lock (_gate)
        {
            if (_latestIndex < 0)
            {
                return null;
            }

            var f = _buffers[_latestIndex]!;
            if (f.Sequence <= lastSequence)
            {
                return null;
            }

            _readers[_latestIndex]++;
            return f;
        }
    }

    public void EndRead(BgraFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            if (_readers[frame.Slot] > 0)
            {
                _readers[frame.Slot]--;
            }
        }
    }

    /// <summary>Forgets the latest frame (for example after the device stops).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _latestIndex = -1;
        }
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Engine.FFmpeg;

/// <summary>
/// The encoder's FLV waiting to be sent, kept as whole FLV tags so that, when the upload can't carry the stream,
/// the oldest part can be dropped cleanly instead of making the encoder wait.
/// <para>
/// Waiting was the old answer, and it broke sound and picture apart: a blocked encoder stops reading both its
/// pipes, the picture loses frames and the sound loses its own (different) share, and from then on they no
/// longer match (in a tester's log the sound ended 42 s behind). Dropping keeps the encoder at real time.
/// </para>
/// <para>
/// What is dropped is always whole groups of pictures, from the oldest waiting tag up to (not including) a video
/// key frame, sound and picture of that time together, so the stream decodes cleanly and stays in sync; viewers
/// see a jump forward. The FLV header, metadata and the codecs' sequence headers are never dropped. The newest
/// group is never dropped, so there is always something to send.
/// </para>
/// Not thread-safe: the owner locks.
/// </summary>
internal sealed class FlvBacklog
{
    private const int HeaderSize = 13; // 9-byte FLV header + PreviousTagSize0
    private const int TagHeaderSize = 11;
    private const int TagTypeAudio = 8;
    private const int TagTypeVideo = 9;

    private readonly LinkedList<Unit> _units = new();
    private byte[] _pending = new byte[64 * 1024];
    private int _pendingLength;
    private bool _headerSeen;
    private bool _passthrough;

    /// <summary>Bytes waiting (whole tags and any partial tag still arriving).</summary>
    public long Bytes { get; private set; }

    /// <summary>Whole tags waiting.</summary>
    public int Count => _units.Count;

    /// <summary>How much stream time the waiting tags span, from the oldest to the newest timestamp.</summary>
    public TimeSpan Duration
    {
        get
        {
            var first = _units.First;
            while (first is not null && first.Value.Timestamp is null)
            {
                first = first.Next;
            }

            var last = _units.Last;
            while (last is not null && last.Value.Timestamp is null)
            {
                last = last.Previous;
            }

            return first is null || last is null || last.Value.Timestamp < first.Value.Timestamp
                ? TimeSpan.Zero
                : TimeSpan.FromMilliseconds(last.Value.Timestamp!.Value - first.Value.Timestamp!.Value);
        }
    }

    /// <summary>Total bytes and stream time dropped so far.</summary>
    public long DroppedBytes { get; private set; }

    public TimeSpan DroppedDuration { get; private set; }

    /// <summary>False when the data turned out not to be FLV: then it is passed on as it came, never dropped.</summary>
    public bool IsFlv => !_passthrough;

    /// <summary>Adds what the encoder wrote. Whole tags become available to <see cref="TryTake"/>.</summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        Bytes += data.Length;
        if (_passthrough)
        {
            _units.AddLast(new Unit(data.ToArray(), Kind.Keep, null));
            return;
        }

        EnsureCapacity(_pendingLength + data.Length);
        data.CopyTo(_pending.AsSpan(_pendingLength));
        _pendingLength += data.Length;
        Split();
    }

    /// <summary>The oldest whole tag (or header) to send, or null when none is complete yet.</summary>
    public byte[]? TryTake()
    {
        if (_units.First is not { } first)
        {
            return null;
        }

        _units.RemoveFirst();
        Bytes -= first.Value.Data.Length;
        return first.Value.Data;
    }

    /// <summary>
    /// Drops the oldest groups of pictures while more than <paramref name="maxDuration"/> (or
    /// <paramref name="maxBytes"/>) is waiting, down to <paramref name="targetDuration"/>. Returns the stream time
    /// dropped (zero when nothing could be).
    /// </summary>
    public TimeSpan DropOldest(TimeSpan maxDuration, long maxBytes, TimeSpan targetDuration)
    {
        if (_passthrough || (Duration <= maxDuration && Bytes <= maxBytes))
        {
            return TimeSpan.Zero;
        }

        var dropped = TimeSpan.Zero;
        while (Duration > targetDuration || Bytes > maxBytes / 2)
        {
            // The next key frame after the oldest tag that may go: everything before it goes (headers stay).
            // The oldest such tag is always among what goes, so each round drops something.
            var start = _units.First;
            while (start is not null && start.Value.Kind == Kind.Keep)
            {
                start = start.Next;
            }

            if (start is null)
            {
                break;
            }

            var node = start.Value.Kind == Kind.KeyFrame ? start.Next : start;
            while (node is not null && node.Value.Kind != Kind.KeyFrame)
            {
                node = node.Next;
            }

            if (node is null)
            {
                break; // only the newest group is left
            }

            var from = FirstTimestamp();
            var cursor = _units.First;
            while (cursor is not null && cursor != node)
            {
                var next = cursor.Next;
                if (cursor.Value.Kind != Kind.Keep)
                {
                    _units.Remove(cursor);
                    Bytes -= cursor.Value.Data.Length;
                    DroppedBytes += cursor.Value.Data.Length;
                }

                cursor = next;
            }

            if (from is { } a && node.Value.Timestamp is { } b && b > a)
            {
                var span = TimeSpan.FromMilliseconds(b - a);
                dropped += span;
                DroppedDuration += span;
            }
        }

        return dropped;
    }

    private long? FirstTimestamp()
    {
        for (var n = _units.First; n is not null; n = n.Next)
        {
            if (n.Value.Timestamp is { } t)
            {
                return t;
            }
        }

        return null;
    }

    private void Split()
    {
        var at = 0;
        if (!_headerSeen)
        {
            if (_pendingLength < HeaderSize)
            {
                return;
            }

            if (_pending[0] != 'F' || _pending[1] != 'L' || _pending[2] != 'V')
            {
                // Not FLV: pass everything on unchanged from now on.
                _passthrough = true;
                _units.AddLast(new Unit(_pending.AsSpan(0, _pendingLength).ToArray(), Kind.Keep, null));
                _pendingLength = 0;
                return;
            }

            _units.AddLast(new Unit(_pending.AsSpan(0, HeaderSize).ToArray(), Kind.Keep, null));
            _headerSeen = true;
            at = HeaderSize;
        }

        while (_pendingLength - at >= TagHeaderSize)
        {
            var dataSize = (_pending[at + 1] << 16) | (_pending[at + 2] << 8) | _pending[at + 3];
            var total = TagHeaderSize + dataSize + 4; // the tag and its PreviousTagSize
            if (_pendingLength - at < total)
            {
                break;
            }

            var tag = _pending.AsSpan(at, total);
            var type = tag[0] & 0x1F;
            var timestamp = (long)(((uint)tag[7] << 24) | ((uint)tag[4] << 16) | ((uint)tag[5] << 8) | tag[6]);
            var kind = Classify(type, tag.Slice(TagHeaderSize, dataSize));

            // Only tags that may be dropped measure the backlog: a kept header (timestamp 0) can wait in
            // the queue for as long as the sender is not connected.
            _units.AddLast(new Unit(tag.ToArray(), kind, kind == Kind.Keep ? null : timestamp));
            at += total;
        }

        if (at > 0)
        {
            _pending.AsSpan(at, _pendingLength - at).CopyTo(_pending);
            _pendingLength -= at;
        }
    }

    private static Kind Classify(int type, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return Kind.Keep;
        }

        if (type == TagTypeVideo)
        {
            var first = data[0];
            var exHeader = (first & 0x80) != 0;
            var frameType = (first >> 4) & 0x07;
            var sequenceHeader = exHeader
                ? (first & 0x0F) == 0                                  // enhanced RTMP: PacketTypeSequenceStart
                : (first & 0x0F) == 7 && data.Length > 1 && data[1] == 0; // AVC sequence header
            if (sequenceHeader || frameType == 5)                     // 5: command frame
            {
                return Kind.Keep;
            }

            return frameType == 1 ? Kind.KeyFrame : Kind.Other;
        }

        if (type == TagTypeAudio)
        {
            // AAC sequence header (AudioSpecificConfig) must reach the server; other sound may go.
            return (data[0] >> 4) == 10 && data.Length > 1 && data[1] == 0 ? Kind.Keep : Kind.Other;
        }

        return Kind.Keep; // metadata and anything unknown
    }

    private void EnsureCapacity(int needed)
    {
        if (needed > _pending.Length)
        {
            Array.Resize(ref _pending, Math.Max(needed, _pending.Length * 2));
        }
    }

    private enum Kind
    {
        Keep,
        KeyFrame,
        Other,
    }

    private readonly record struct Unit(byte[] Data, Kind Kind, long? Timestamp);
}

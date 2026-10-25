// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Linkast.Networking;

/// <summary>
/// Keeps the stream key off FFmpeg's command line. FFmpeg publishes to the local relay with a random
/// placeholder of exactly the same byte length as the real key; this class parses FFmpeg's outgoing
/// RTMP (handshake, then chunked messages) until the <c>publish</c> command and replaces the
/// placeholder with the real key inside the command messages. Lengths never change, so chunk headers
/// stay valid. After <c>publish</c> everything is passed through untouched.
/// </summary>
/// <remarks>
/// Feed every upstream read to <see cref="Process"/> and forward what it returns. Bytes of a message
/// that is not complete yet are held back until it is, so a placeholder split across chunks or reads
/// is still replaced. If anything unexpected happens the rewriter passes data through unchanged:
/// the server then sees the placeholder and refuses it, which fails safe (no key is leaked).
/// </remarks>
public sealed class RtmpKeyRewriter
{
    private const int HandshakeLength = 1 + 1536 + 1536; // C0 + C1 + C2
    private const int MaxRewriteBytes = 1024 * 1024;
    private const byte AmfCommand = 20;
    private const byte AmfCommandAmf3 = 17;
    private const byte SetChunkSize = 1;

    private static readonly byte[] PublishName = Encoding.ASCII.GetBytes("publish");

    private readonly byte[] _placeholder;
    private readonly byte[] _key;
    private readonly Dictionary<int, ChunkStream> _streams = [];
    private byte[] _buffer = new byte[8 * 1024];
    private int _length;    // bytes held in _buffer
    private int _parsed;    // bytes of _buffer fully parsed (complete chunks)
    private long _consumed; // total bytes ever given to Process
    private int _chunkSize = 128;
    private bool _passthrough;

    public RtmpKeyRewriter(string placeholder, string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(placeholder);
        ArgumentException.ThrowIfNullOrEmpty(key);
        _placeholder = Encoding.UTF8.GetBytes(placeholder);
        _key = Encoding.UTF8.GetBytes(key);
        if (_placeholder.Length != _key.Length)
        {
            throw new ArgumentException("The placeholder must have the same byte length as the key.", nameof(placeholder));
        }
    }

    /// <summary>Number of placeholder occurrences replaced so far.</summary>
    public int Replacements { get; private set; }

    /// <summary>True once the publish command was seen (or rewriting gave up); data then flows unchanged.</summary>
    public bool IsPassthrough => _passthrough;

    /// <summary>A random ASCII placeholder with the same UTF-8 byte length as <paramref name="key"/>.</summary>
    public static string CreatePlaceholder(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var length = Encoding.UTF8.GetByteCount(key);
        return RandomNumberGenerator.GetString(alphabet, length);
    }

    /// <summary>
    /// Takes the next bytes from FFmpeg and returns the bytes that may be forwarded now (possibly
    /// empty, possibly including bytes held back from earlier calls). The returned memory is valid
    /// until the next call.
    /// </summary>
    public ReadOnlyMemory<byte> Process(ReadOnlySpan<byte> data)
    {
        if (_passthrough && _length == 0)
        {
            _consumed += data.Length;
            return data.ToArray();
        }

        Append(data);
        _consumed += data.Length;
        try
        {
            Parse();
        }
        catch (InvalidDataException)
        {
            _passthrough = true;
        }

        if (!_passthrough && _consumed > MaxRewriteBytes)
        {
            _passthrough = true;
        }

        var ready = _passthrough ? _length : SafeEnd();
        var output = _buffer.AsSpan(0, ready).ToArray();
        Discard(ready);
        return output;
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (_length + data.Length > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + data.Length));
        }

        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;
    }

    /// <summary>Everything before the first incomplete message can be forwarded.</summary>
    private int SafeEnd()
    {
        var end = _parsed;
        foreach (var s in _streams.Values)
        {
            if (s.Remaining > 0 && s.MessageStart < end)
            {
                end = s.MessageStart;
            }
        }

        return end;
    }

    private void Discard(int count)
    {
        if (count == 0)
        {
            return;
        }

        Buffer.BlockCopy(_buffer, count, _buffer, 0, _length - count);
        _length -= count;
        _parsed -= count;
        foreach (var s in _streams.Values)
        {
            s.MessageStart -= count;
            for (var i = 0; i < s.Segments.Count; i++)
            {
                s.Segments[i] = (s.Segments[i].Offset - count, s.Segments[i].Length);
            }
        }
    }

    private long HandshakeBytesLeft => Math.Max(0, HandshakeLength - (_consumed - _length + _parsed));

    private void Parse()
    {
        // The handshake is forwarded untouched.
        var skip = (int)Math.Min(HandshakeBytesLeft, _length - _parsed);
        _parsed += skip;

        while (!_passthrough && HandshakeBytesLeft == 0 && TryParseChunk())
        {
        }
    }

    private bool TryParseChunk()
    {
        var span = _buffer.AsSpan(_parsed, _length - _parsed);
        if (span.Length < 1)
        {
            return false;
        }

        var fmt = span[0] >> 6;
        var csid = span[0] & 0x3F;
        var pos = 1;
        if (csid == 0)
        {
            if (span.Length < 2)
            {
                return false;
            }

            csid = 64 + span[1];
            pos = 2;
        }
        else if (csid == 1)
        {
            if (span.Length < 3)
            {
                return false;
            }

            csid = 64 + span[1] + (span[2] * 256);
            pos = 3;
        }

        var headerLength = fmt switch { 0 => 11, 1 => 7, 2 => 3, _ => 0 };
        if (span.Length < pos + headerLength)
        {
            return false;
        }

        if (!_streams.TryGetValue(csid, out var s))
        {
            if (fmt != 0)
            {
                throw new InvalidDataException("First chunk of a stream must have a full header.");
            }

            _streams[csid] = s = new ChunkStream();
        }

        var header = span.Slice(pos, headerLength);
        var extended = s.ExtendedTimestamp;
        if (fmt <= 2)
        {
            extended = (header[0] << 16 | header[1] << 8 | header[2]) == 0xFFFFFF;
        }

        var length = s.MessageLength;
        var type = s.MessageType;
        if (fmt <= 1)
        {
            length = header[3] << 16 | header[4] << 8 | header[5];
            type = header[6];
        }

        pos += headerLength + (extended ? 4 : 0);
        if (span.Length < pos)
        {
            return false;
        }

        var startsMessage = s.Remaining == 0;
        if (!startsMessage && fmt != 3)
        {
            throw new InvalidDataException("New message header before the previous message finished.");
        }

        var remaining = startsMessage ? length : s.Remaining;
        var payload = Math.Min(_chunkSize, remaining);
        if (span.Length < pos + payload)
        {
            return false;
        }

        // The chunk is complete: commit its state.
        s.ExtendedTimestamp = extended;
        if (startsMessage)
        {
            s.MessageLength = length;
            s.MessageType = type;
            s.MessageStart = _parsed;
            s.Segments.Clear();
        }

        s.Segments.Add((_parsed + pos, payload));
        s.Remaining = remaining - payload;
        _parsed += pos + payload;

        if (s.Remaining == 0)
        {
            OnMessage(s);
        }

        return true;
    }

    private void OnMessage(ChunkStream s)
    {
        var payload = new byte[s.MessageLength];
        var at = 0;
        foreach (var (offset, length) in s.Segments)
        {
            _buffer.AsSpan(offset, length).CopyTo(payload.AsSpan(at));
            at += length;
        }

        if (s.MessageType == SetChunkSize && payload.Length >= 4)
        {
            _chunkSize = (int)(BinaryPrimitives.ReadUInt32BigEndian(payload) & 0x7FFFFFFF);
            if (_chunkSize < 1)
            {
                throw new InvalidDataException("Invalid chunk size.");
            }
        }
        else if (s.MessageType is AmfCommand or AmfCommandAmf3)
        {
            var body = s.MessageType == AmfCommandAmf3 && payload.Length > 0 ? payload.AsSpan(1) : payload;
            var bodyStart = payload.Length - body.Length;
            for (var i = body.IndexOf(_placeholder); i >= 0;)
            {
                WriteBack(s, bodyStart + i, _key);
                Replacements++;
                var next = body[(i + _placeholder.Length)..].IndexOf(_placeholder);
                i = next < 0 ? -1 : i + _placeholder.Length + next;
            }

            // AMF0 command name: 0x02, 16-bit length, "publish". After it, nothing else needs rewriting.
            if (body.Length >= 3 + PublishName.Length && body[0] == 0x02
                && BinaryPrimitives.ReadUInt16BigEndian(body[1..]) == PublishName.Length
                && body.Slice(3, PublishName.Length).SequenceEqual(PublishName))
            {
                _passthrough = true;
            }
        }

        s.Segments.Clear();
    }

    /// <summary>Writes <paramref name="value"/> at a payload offset, following the chunk segments.</summary>
    private void WriteBack(ChunkStream s, int payloadOffset, byte[] value)
    {
        var written = 0;
        var skipped = 0;
        foreach (var (offset, length) in s.Segments)
        {
            if (skipped + length <= payloadOffset + written)
            {
                skipped += length;
                continue;
            }

            var from = payloadOffset + written - skipped;
            var count = Math.Min(length - from, value.Length - written);
            value.AsSpan(written, count).CopyTo(_buffer.AsSpan(offset + from));
            written += count;
            skipped += length;
            if (written == value.Length)
            {
                return;
            }
        }
    }

    private sealed class ChunkStream
    {
        public int MessageLength { get; set; }

        public byte MessageType { get; set; }

        public int Remaining { get; set; }

        public bool ExtendedTimestamp { get; set; }

        /// <summary>Buffer offset of the first chunk header of the message in progress.</summary>
        public int MessageStart { get; set; }

        public List<(int Offset, int Length)> Segments { get; } = [];
    }
}

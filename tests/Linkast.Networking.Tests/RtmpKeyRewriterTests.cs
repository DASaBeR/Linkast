// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace Linkast.Networking.Tests;

public class RtmpKeyRewriterTests
{
    private const string Key = "live_123456789_AbCdEfGhIjKlMnOpQrStUvWxYz";

    /// <summary>Long enough that its commands exceed 128 bytes, so the key crosses a chunk boundary.</summary>
    private static readonly string LongKey = "k" + new string('Z', 149);

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(1000)]
    [InlineData(100_000)]
    public void Replaces_the_placeholder_in_publish_commands_whatever_the_read_sizes(int readSize)
    {
        var placeholder = RtmpKeyRewriter.CreatePlaceholder(Key);
        var (input, expected) = Session(placeholder, chunkSize: 128, padForSplit: true);

        var output = Run(new RtmpKeyRewriter(placeholder, Key), input, readSize, out var rewriter);

        Assert.Equal(expected, output);
        Assert.Equal(3, rewriter.Replacements); // releaseStream, FCPublish, publish
        Assert.True(rewriter.IsPassthrough);
    }

    [Fact]
    public void Placeholder_split_across_a_chunk_boundary_is_replaced()
    {
        var placeholder = RtmpKeyRewriter.CreatePlaceholder(LongKey);
        var (input, expected) = Session(placeholder, chunkSize: 128, padForSplit: true, key: LongKey);

        // The input itself must contain the placeholder interrupted by a chunk header somewhere.
        Assert.True(CountOccurrences(input, Encoding.ASCII.GetBytes(placeholder)) < 3, "Test data should split at least one placeholder.");

        var output = Run(new RtmpKeyRewriter(placeholder, LongKey), input, 4096, out var rewriter);
        Assert.Equal(expected, output);
        Assert.Equal(3, rewriter.Replacements);
        Assert.Equal(0, CountOccurrences(output, Encoding.ASCII.GetBytes(placeholder)));
    }

    [Fact]
    public void Honours_set_chunk_size_and_leaves_media_after_publish_untouched()
    {
        var placeholder = RtmpKeyRewriter.CreatePlaceholder(Key);
        var (input, expected) = Session(placeholder, chunkSize: 4096, padForSplit: false, mediaContainsPlaceholder: true);

        var output = Run(new RtmpKeyRewriter(placeholder, Key), input, 333, out var rewriter);

        Assert.Equal(expected, output);
        Assert.Equal(3, rewriter.Replacements);

        // The media message after publish still contains the placeholder bytes verbatim.
        Assert.Equal(1, CountOccurrences(output, Encoding.ASCII.GetBytes(placeholder)));
    }

    [Fact]
    public void Placeholder_has_the_key_byte_length_and_is_random()
    {
        const string unicodeKey = "clé-ü-🙂";
        var a = RtmpKeyRewriter.CreatePlaceholder(unicodeKey);
        var b = RtmpKeyRewriter.CreatePlaceholder(unicodeKey);
        Assert.Equal(Encoding.UTF8.GetByteCount(unicodeKey), Encoding.UTF8.GetByteCount(a));
        Assert.NotEqual(a, b);
        Assert.All(a, c => Assert.True(char.IsAsciiLetterOrDigit(c)));
        Assert.Throws<ArgumentException>(() => new RtmpKeyRewriter("short", Key));
    }

    [Fact]
    public void Garbage_after_the_handshake_passes_through_unchanged()
    {
        var placeholder = RtmpKeyRewriter.CreatePlaceholder(Key);
        var input = new byte[3073 + 50];
        Random.Shared.NextBytes(input);
        input[3073] = 0x45; // fmt 1 on a chunk stream never started with a full header
        var output = Run(new RtmpKeyRewriter(placeholder, Key), input, 64, out var rewriter);
        Assert.Equal(input, output);
        Assert.True(rewriter.IsPassthrough);
        Assert.Equal(0, rewriter.Replacements);
    }

    private static byte[] Run(RtmpKeyRewriter rewriter, byte[] input, int readSize, out RtmpKeyRewriter used)
    {
        used = rewriter;
        var output = new List<byte>();
        for (var i = 0; i < input.Length; i += readSize)
        {
            var part = input.AsSpan(i, Math.Min(readSize, input.Length - i));
            output.AddRange(rewriter.Process(part).ToArray());
        }

        return [.. output];
    }

    /// <summary>
    /// A publish session as FFmpeg sends it: handshake, optional Set Chunk Size, connect,
    /// releaseStream, FCPublish, createStream, publish, then one media message.
    /// Returns the bytes with the placeholder and the bytes expected with the real key.
    /// </summary>
    private static (byte[] Input, byte[] Expected) Session(string placeholder, int chunkSize, bool padForSplit, bool mediaContainsPlaceholder = false, string key = Key)
    {
        byte[] Build(string playpath)
        {
            var w = new MemoryStream();
            var handshake = new byte[3073];
            new Random(1).NextBytes(handshake);
            handshake[0] = 3;
            w.Write(handshake);

            if (chunkSize != 128)
            {
                var size = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(size, (uint)chunkSize);
                WriteMessage(w, 2, 1, size, 128);
            }

            // Padding the app name moves the playpath of later commands across a 128-byte boundary.
            var app = padForSplit ? "live" + new string('x', 70) : "live";
            WriteMessage(w, 3, 20, Amf(Str("connect"), Num(1), Obj(("app", app), ("tcUrl", "rtmp://example.com/" + app))), chunkSize);
            WriteMessage(w, 3, 20, Amf(Str("releaseStream"), Num(2), Null(), Str(playpath)), chunkSize);
            WriteMessage(w, 3, 20, Amf(Str("FCPublish"), Num(3), Null(), Str(playpath)), chunkSize);
            WriteMessage(w, 3, 20, Amf(Str("createStream"), Num(4), Null()), chunkSize);
            var padded = padForSplit ? new string('y', 90) : string.Empty;
            WriteMessage(w, 4, 20, Amf(Str("publish"), Num(5), Null(), Str(playpath), Str("live" + padded)), chunkSize);

            var media = new byte[600];
            new Random(2).NextBytes(media);
            if (mediaContainsPlaceholder)
            {
                Encoding.ASCII.GetBytes(placeholder).CopyTo(media, 100); // must survive unchanged
            }

            WriteMessage(w, 6, 9, media, chunkSize);
            return w.ToArray();
        }

        var input = Build(placeholder);
        var expected = Build(key); // media is identical in both: post-publish bytes are never rewritten
        return (input, expected);
    }

    private static void WriteMessage(Stream w, int csid, byte type, byte[] payload, int chunkSize)
    {
        w.WriteByte((byte)csid);                      // fmt 0
        w.Write([0, 0, 0]);                            // timestamp
        w.Write([(byte)(payload.Length >> 16), (byte)(payload.Length >> 8), (byte)payload.Length]);
        w.WriteByte(type);
        w.Write([0, 0, 0, 0]);                         // message stream id
        for (var i = 0; i < payload.Length; i += chunkSize)
        {
            if (i > 0)
            {
                w.WriteByte((byte)(0xC0 | csid));      // fmt 3 continuation
            }

            w.Write(payload, i, Math.Min(chunkSize, payload.Length - i));
        }
    }

    private static byte[] Amf(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    private static byte[] Str(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        return [0x02, (byte)(bytes.Length >> 8), (byte)bytes.Length, .. bytes];
    }

    private static byte[] Num(double d)
    {
        var b = new byte[9];
        b[0] = 0x00;
        BinaryPrimitives.WriteDoubleBigEndian(b.AsSpan(1), d);
        return b;
    }

    private static byte[] Null() => [0x05];

    private static byte[] Obj(params (string Name, string Value)[] props)
    {
        var list = new List<byte> { 0x03 };
        foreach (var (name, value) in props)
        {
            var n = Encoding.UTF8.GetBytes(name);
            list.AddRange([(byte)(n.Length >> 8), (byte)n.Length]);
            list.AddRange(n);
            list.AddRange(Str(value));
        }

        list.AddRange([0, 0, 9]);
        return [.. list];
    }

    private static int CountOccurrences(byte[] haystack, byte[] needle)
    {
        var count = 0;
        var span = haystack.AsSpan();
        for (var i = span.IndexOf(needle); i >= 0;)
        {
            count++;
            var next = span[(i + 1)..].IndexOf(needle);
            i = next < 0 ? -1 : i + 1 + next;
        }

        return count;
    }
}

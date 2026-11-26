// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Engine.FFmpeg;

namespace Linkast.Engine.FFmpeg.Tests;

public class FlvBacklogTests
{
    private const byte Audio = 8;
    private const byte Video = 9;
    private const byte Script = 18;

    /// <summary>
    /// An FLV like the encoder's: header, metadata, the AVC and AAC sequence headers, then <paramref name="seconds"/>
    /// of 30 fps video (a key frame every 2 s) with AAC audio every 21 ms, in timestamp order.
    /// </summary>
    private static (byte[] Data, List<(byte Type, uint Ts, bool Key)> Tags) Stream(int seconds, int videoBytes = 20000)
    {
        var tags = new List<(byte Type, uint Ts, bool Key, byte[] Payload)>
        {
            (Script, 0, false, [2, 0, 10, .. "onMetaData"u8.ToArray()]),
            (Video, 0, false, [0x17, 0, 0, 0, 0, 1, 2, 3]),  // AVC sequence header
            (Audio, 0, false, [0xAF, 0, 0x11, 0x90]),        // AAC sequence header
        };

        var events = new List<(uint Ts, byte Type)>();
        for (uint t = 0; t < seconds * 1000; t += 33)
        {
            events.Add((t, Video));
        }

        for (uint t = 0; t < seconds * 1000; t += 21)
        {
            events.Add((t, Audio));
        }

        foreach (var (ts, type) in events.OrderBy(e => e.Ts).ThenBy(e => e.Type))
        {
            if (type == Video)
            {
                var key = ts % 2000 < 33;
                var payload = new byte[videoBytes];
                payload[0] = (byte)(key ? 0x17 : 0x27);
                payload[1] = 1; // NALU
                tags.Add((Video, ts, key, payload));
            }
            else
            {
                tags.Add((Audio, ts, false, [0xAF, 1, .. new byte[400]]));
            }
        }

        var bytes = new List<byte> { (byte)'F', (byte)'L', (byte)'V', 1, 5, 0, 0, 0, 9, 0, 0, 0, 0 };
        foreach (var (type, ts, _, payload) in tags)
        {
            var size = payload.Length;
            bytes.AddRange([type, (byte)(size >> 16), (byte)(size >> 8), (byte)size, (byte)(ts >> 16), (byte)(ts >> 8), (byte)ts, (byte)(ts >> 24), 0, 0, 0]);
            bytes.AddRange(payload);
            var prev = size + 11;
            bytes.AddRange([(byte)(prev >> 24), (byte)(prev >> 16), (byte)(prev >> 8), (byte)prev]);
        }

        return ([.. bytes], [.. tags.Select(t => (t.Type, t.Ts, t.Key))]);
    }

    private static void Feed(FlvBacklog backlog, byte[] data, int seed)
    {
        var random = new Random(seed);
        for (var at = 0; at < data.Length;)
        {
            var n = Math.Min(random.Next(1, 70000), data.Length - at);
            backlog.Append(data.AsSpan(at, n));
            at += n;
        }
    }

    private static List<byte[]> TakeAll(FlvBacklog backlog)
    {
        var units = new List<byte[]>();
        while (backlog.TryTake() is { } unit)
        {
            units.Add(unit);
        }

        return units;
    }

    private static (byte Type, uint Ts, byte First) Parse(byte[] tag) =>
        (tag[0], (uint)((tag[7] << 24) | (tag[4] << 16) | (tag[5] << 8) | tag[6]), tag[11]);

    [Fact]
    public void Passes_the_stream_on_unchanged_in_any_chunking()
    {
        var (data, _) = Stream(seconds: 3);
        var backlog = new FlvBacklog();
        Feed(backlog, data, seed: 1);

        Assert.True(backlog.IsFlv);
        Assert.Equal(data.Length, backlog.Bytes);
        Assert.Equal(data, TakeAll(backlog).SelectMany(u => u).ToArray());
        Assert.Equal(0, backlog.Bytes);
    }

    [Fact]
    public void Measures_the_waiting_stream_time_from_the_tags()
    {
        var (data, _) = Stream(seconds: 5);
        var backlog = new FlvBacklog();
        backlog.Append(data);

        Assert.InRange(backlog.Duration.TotalSeconds, 4.9, 5.0);
    }

    [Fact]
    public void Nothing_is_dropped_while_the_backlog_is_short()
    {
        var (data, _) = Stream(seconds: 6);
        var backlog = new FlvBacklog();
        backlog.Append(data);

        Assert.Equal(TimeSpan.Zero, backlog.DropOldest(TimeSpan.FromSeconds(8), 64L << 20, TimeSpan.FromSeconds(4)));
        Assert.Equal(data.Length, backlog.Bytes);
    }

    [Fact]
    public void A_long_backlog_loses_its_oldest_whole_groups_and_keeps_the_headers()
    {
        var (data, _) = Stream(seconds: 20);
        var backlog = new FlvBacklog();
        Feed(backlog, data, seed: 2);

        var dropped = backlog.DropOldest(TimeSpan.FromSeconds(8), 64L << 20, TimeSpan.FromSeconds(4));

        // Whole 2 s groups: 20 s waiting, at most 4 s left.
        Assert.InRange(dropped.TotalSeconds, 15.9, 16.1);
        Assert.InRange(backlog.Duration.TotalSeconds, 3.0, 4.0);
        Assert.Equal(dropped, backlog.DroppedDuration);

        var units = TakeAll(backlog);
        Assert.Equal("FLV", System.Text.Encoding.ASCII.GetString(units[0], 0, 3));
        var tags = units.Skip(1).Select(Parse).ToList();
        Assert.Equal(Script, tags[0].Type);
        Assert.Equal((Video, 0x17), (tags[1].Type, tags[1].First)); // AVC sequence header
        Assert.Equal(Audio, tags[2].Type);                          // AAC sequence header

        // Then the stream resumes at a key frame, audio of the same time with it; nothing older survives.
        var rest = tags.Skip(3).ToList();
        var firstVideo = rest.First(t => t.Type == Video);
        Assert.Equal(0x17, firstVideo.First);
        Assert.InRange(firstVideo.Ts, 16000u, 16032u); // the key frame of the 16 s group (frames are 33 ms apart)
        Assert.All(rest, t => Assert.True(t.Ts >= 15990));
        Assert.Contains(rest, t => t.Type == Audio && t.Ts < 16100);
    }

    [Fact]
    public void The_newest_group_is_never_dropped()
    {
        var (data, tags) = Stream(seconds: 2); // one group only
        var backlog = new FlvBacklog();
        backlog.Append(data);

        backlog.DropOldest(TimeSpan.FromSeconds(1), 1, TimeSpan.Zero);

        // At most the sound queued ahead of the group's key frame goes; every picture of the group stays.
        var left = TakeAll(backlog).Skip(1).Select(Parse).ToList();
        Assert.Equal(tags.Count(t => t.Type == Video), left.Count(t => t.Type == Video));
        Assert.True(left.Count >= tags.Count - 1);
    }

    [Fact]
    public void Too_many_bytes_drop_old_groups_even_with_little_stream_time()
    {
        var (data, _) = Stream(seconds: 6, videoBytes: 200000); // about 36 MB in 6 s
        var backlog = new FlvBacklog();
        backlog.Append(data);

        var dropped = backlog.DropOldest(TimeSpan.FromSeconds(60), 16L << 20, TimeSpan.FromSeconds(30));

        Assert.True(dropped > TimeSpan.Zero);
        Assert.True(backlog.Bytes <= 16L << 20, $"{backlog.Bytes} bytes left");
    }

    [Fact]
    public void Data_that_is_not_FLV_passes_through_and_is_never_dropped()
    {
        var data = new byte[100000];
        new Random(4).NextBytes(data);
        data[0] = (byte)'X';
        var backlog = new FlvBacklog();
        Feed(backlog, data, seed: 3);

        Assert.False(backlog.IsFlv);
        Assert.Equal(TimeSpan.Zero, backlog.DropOldest(TimeSpan.Zero, 1, TimeSpan.Zero));
        Assert.Equal(data, TakeAll(backlog).SelectMany(u => u).ToArray());
    }

    [Fact]
    public void A_tag_that_is_still_arriving_is_not_taken()
    {
        var (data, _) = Stream(seconds: 1);
        var backlog = new FlvBacklog();
        backlog.Append(data.AsSpan(0, 13 + 5)); // header and part of the first tag

        Assert.NotNull(backlog.TryTake());   // the FLV header
        Assert.Null(backlog.TryTake());      // the half tag waits
        Assert.Equal(5, backlog.Bytes);
    }

    [Fact]
    public void Drop_warning_says_what_was_skipped_and_a_bitrate_that_fits()
    {
        var message = BufferedOutputLease.DropWarning(4.1, 6200, 2300);
        Assert.Contains("The upload is not keeping up:", message, StringComparison.Ordinal); // shown in the app's warning bar
        Assert.Contains("4.1 s of the stream was skipped", message, StringComparison.Ordinal);
        Assert.Contains("1500 kbps or lower", message, StringComparison.Ordinal);
    }
}

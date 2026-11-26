// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.App.Services;
using Linkast.Core;
using Microsoft.Extensions.Logging;

namespace Linkast.App.Tests;

public class StreamStatsLoggerTests
{
    private sealed class CaptureLogger : ILogger<StreamStatsLogger>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    private static StreamTelemetry Sample(int second, double sentKbps, long encoderBytes, long buffered = 0, int reconnects = 0, long dropped = 0)
    {
        var uptime = TimeSpan.FromSeconds(second);
        return new StreamTelemetry(
            StreamState.Live,
            uptime,
            uptime,
            60,
            new EncoderProgress(second * 60, 59.5, 6000, encoderBytes, uptime, 0, dropped, 1.0, false),
            new NetworkSnapshot(second * 700_000L, 0, sentKbps, "127.0.0.1:1", "kick.example:443", "Ethernet", true, buffered),
            reconnects,
            null);
    }

    [Fact]
    public void Nothing_is_logged_before_the_interval_has_passed()
    {
        var log = new CaptureLogger();
        var stats = new StreamStatsLogger(log);
        stats.Begin();

        for (var s = 1; s < 30; s++)
        {
            stats.Observe(Sample(s, 6000, s * 750_000L));
        }

        Assert.Empty(log.Lines);
    }

    [Fact]
    public void A_stats_line_gives_the_real_encoder_bitrate_and_the_lowest_and_highest_sent_rate()
    {
        var log = new CaptureLogger();
        var stats = new StreamStatsLogger(log);
        stats.Begin();

        for (var s = 1; s <= 30; s++)
        {
            // The encoder writes 750,000 bytes a second (6000 kbps); the connection sends 800 to 6200 kbps.
            stats.Observe(Sample(s, s == 10 ? 800 : s == 20 ? 6200 : 4000, s * 750_000L, buffered: s == 15 ? 8 << 20 : 0));
        }

        var line = Assert.Single(log.Lines);
        Assert.Contains("Stream stats (30 s)", line);
        Assert.Contains("encoder output 6000 kbps", line);
        Assert.Contains("sent ", line);
        Assert.Contains("min 800", line);
        Assert.Contains("max 6200", line);
        Assert.Contains("max 8.0 MB", line);
        Assert.Contains("kick.example:443", line);
    }

    [Fact]
    public void Dropped_frames_and_reconnects_are_counted_per_interval()
    {
        var log = new CaptureLogger();
        var stats = new StreamStatsLogger(log);
        stats.Begin();

        for (var s = 1; s <= 30; s++)
        {
            stats.Observe(Sample(s, 5000, s * 750_000L, reconnects: s >= 12 ? 2 : 0, dropped: s >= 20 ? 7 : 0));
        }

        var line = Assert.Single(log.Lines);
        Assert.Contains("reconnects 2 (+2)", line);
        Assert.Contains("dropped +7", line);
    }

    [Fact]
    public void The_extra_text_is_added_and_a_failing_extra_never_throws()
    {
        var log = new CaptureLogger();
        var stats = new StreamStatsLogger(log) { Extra = () => "game deadlock.exe is NOT RESPONDING" };
        stats.Begin();
        for (var s = 1; s <= 30; s++)
        {
            stats.Observe(Sample(s, 5000, s * 750_000L));
        }

        Assert.Contains("game deadlock.exe is NOT RESPONDING", Assert.Single(log.Lines));

        var failing = new CaptureLogger();
        var other = new StreamStatsLogger(failing) { Extra = () => throw new InvalidOperationException("boom") };
        other.Begin();
        for (var s = 1; s <= 30; s++)
        {
            other.Observe(Sample(s, 5000, s * 750_000L));
        }

        Assert.Contains("extra info failed", Assert.Single(failing.Lines));
    }

    [Fact]
    public void A_state_change_is_logged_with_its_reason_and_the_end_of_a_stream_adds_a_summary()
    {
        var log = new CaptureLogger();
        var stats = new StreamStatsLogger(log);
        stats.Begin();
        stats.Observe(Sample(10, 5000, 7_500_000));

        stats.StateChanged(new StreamStateChangedEventArgs(
            StreamState.Live,
            StreamState.Reconnecting,
            new StreamError(ErrorCategory.Network, "network.reset", "The connection was lost.", "forcibly closed by the remote host")));
        stats.StateChanged(new StreamStateChangedEventArgs(StreamState.Stopping, StreamState.Stopped, null));

        Assert.Contains(log.Lines, l => l.Contains("Live -> Reconnecting", StringComparison.Ordinal) && l.Contains("network.reset", StringComparison.Ordinal) && l.Contains("forcibly closed", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.StartsWith("Stream summary:", StringComparison.Ordinal));
    }

    [Fact]
    public void The_summary_counts_every_connection_not_only_the_last()
    {
        // A tester's log: 88 MB sent, then reconnects; the summary said "sent 0.5 MB" (the last connection's).
        var log = new CaptureLogger();
        var stats = new StreamStatsLogger(log);
        stats.Begin();
        for (var s = 1; s <= 20; s++)
        {
            stats.Observe(Sample(s, 5600, s * 700_000L, dropped: 3));
        }

        // The new connection and FFmpeg count from zero again.
        for (var s = 1; s <= 5; s++)
        {
            var t = Sample(s, 5600, s * 700_000L, reconnects: 1, dropped: 1);
            stats.Observe(t with { Uptime = TimeSpan.FromSeconds(20 + s) });
        }

        stats.StateChanged(new StreamStateChangedEventArgs(StreamState.Stopping, StreamState.Stopped, null));

        var summary = Assert.Single(log.Lines, l => l.StartsWith("Stream summary:", StringComparison.Ordinal));
        Assert.Contains("sent 16.7 MB", summary, StringComparison.Ordinal); // (20 + 5) × 700,000 bytes
        Assert.Contains("dropped 4,", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Backlog_maximum_is_never_below_the_backlog_now()
    {
        var log = new CaptureLogger();
        var stats = new StreamStatsLogger(log);
        stats.Begin();
        for (var s = 1; s < 30; s++)
        {
            stats.Observe(Sample(s, 5000, s * 750_000L, buffered: 1 << 20));
        }

        // The last sample (a connection that just dropped) is not counted as live, but its backlog is shown.
        stats.Observe(Sample(30, 5000, 30 * 750_000L, buffered: 3 << 20) with { State = StreamState.Connecting });

        var line = Assert.Single(log.Lines);
        Assert.Contains("backlog now 3.0 MB (max 3.0 MB", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Stream_time_skipped_for_a_slow_upload_is_reported()
    {
        var log = new CaptureLogger();
        var stats = new StreamStatsLogger(log);
        stats.Begin();
        for (var s = 1; s <= 30; s++)
        {
            var t = Sample(s, 2000, s * 750_000L);
            stats.Observe(t with { Network = t.Network! with { DroppedDuration = TimeSpan.FromSeconds(s >= 25 ? 4.2 : 0) } });
        }

        Assert.Contains("skipped 4.2 s of the stream so far", Assert.Single(log.Lines), StringComparison.Ordinal);
    }
}

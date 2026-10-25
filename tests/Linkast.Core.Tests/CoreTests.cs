// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core;
using Linkast.Core.Audio;

namespace Linkast.Core.Tests;

public class ReconnectBackoffTests
{
    [Fact]
    public void Delays_double_and_cap_at_max()
    {
        var b = new ReconnectBackoff(new ReconnectPolicy(6, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60)));
        var delays = Enumerable.Range(0, 6).Select(_ => b.NextDelay()!.Value.TotalSeconds).ToArray();
        Assert.Equal([1, 2, 4, 8, 10, 10], delays);
    }

    [Fact]
    public void Budget_is_bounded()
    {
        var b = new ReconnectBackoff(new ReconnectPolicy(2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60)));
        Assert.NotNull(b.NextDelay());
        Assert.NotNull(b.NextDelay());
        Assert.Null(b.NextDelay());
    }

    [Fact]
    public void Disabled_policy_never_retries() => Assert.Null(new ReconnectBackoff(ReconnectPolicy.Disabled).NextDelay());

    [Fact]
    public void Stable_connection_resets_budget()
    {
        var b = new ReconnectBackoff(new ReconnectPolicy(1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60)));
        b.NextDelay();
        b.ReportStable(TimeSpan.FromSeconds(30));
        Assert.Equal(1, b.Attempts);
        b.ReportStable(TimeSpan.FromSeconds(61));
        Assert.Equal(0, b.Attempts);
        Assert.NotNull(b.NextDelay());
    }
}

public class StateMachineTests
{
    [Theory]
    [InlineData(StreamState.Idle, StreamState.Preparing, true)]
    [InlineData(StreamState.Idle, StreamState.Live, false)]
    [InlineData(StreamState.Connecting, StreamState.Live, true)]
    [InlineData(StreamState.Live, StreamState.Reconnecting, true)]
    [InlineData(StreamState.Reconnecting, StreamState.Live, false)]
    [InlineData(StreamState.Stopped, StreamState.Preparing, true)]
    [InlineData(StreamState.Failed, StreamState.Live, false)]
    public void Transitions_follow_rules(StreamState from, StreamState to, bool allowed) =>
        Assert.Equal(allowed, StreamStateMachine.CanTransition(from, to));

    [Fact]
    public void Raises_changed_event()
    {
        var sm = new StreamStateMachine();
        StreamStateChangedEventArgs? seen = null;
        sm.Changed += (_, e) => seen = e;
        sm.Transition(StreamState.Preparing);
        Assert.Equal(StreamState.Idle, seen?.Previous);
        Assert.Equal(StreamState.Preparing, seen?.Current);
        Assert.Throws<InvalidOperationException>(() => sm.Transition(StreamState.Stopped));
    }
}

public class RedactorTests
{
    [Fact]
    public void Masks_registered_secret_anywhere()
    {
        var r = new Redactor();
        r.Register(new Secret("live_abc123XYZ"));
        Assert.Equal("key=******** end", r.Redact("key=live_abc123XYZ end"));
    }

    [Fact]
    public void Masks_url_encoded_secret()
    {
        var r = new Redactor();
        r.Register(new Secret("a b/c?d"));
        Assert.DoesNotContain("a%20b", r.Redact("x a%20b%2Fc%3Fd y"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Opening rtmp://127.0.0.1:5000/live/sk_live_123 for writing", "Opening rtmp://127.0.0.1:5000/live/******** for writing")]
    [InlineData("rtmps://live-api-s.facebook.com:443/rtmp/FB-123-0-Ab", "rtmps://live-api-s.facebook.com:443/rtmp/********")]
    [InlineData("rtmp://host/app", "rtmp://host/app")]
    public void Masks_rtmp_key_segment_structurally(string input, string expected) =>
        Assert.Equal(expected, new Redactor().Redact(input));

    [Fact]
    public void Secret_never_prints_value()
    {
        var s = new Secret("hunter22");
        Assert.Equal(Secret.Mask, s.ToString());
        Assert.DoesNotContain("hunter22", new Destination("x", new Uri("rtmp://h/a"), s).ToString(), StringComparison.Ordinal);
    }
}

public class SessionValidatorTests
{
    private static StreamSession Session(string url = "rtmp://live.example.com/app", string key = "key123", EncoderSettings? enc = null) =>
        new(new DisplaySource(0), [], enc ?? EncoderSettings.Default1080p60("libx264"),
            new Destination("Test", new Uri(url), new Secret(key)), NetworkSelection.SystemDefault, ReconnectPolicy.Default);

    [Fact]
    public void Valid_session_has_no_errors() => Assert.Empty(SessionValidator.Validate(Session()));

    [Theory]
    [InlineData("http://example.com/app", "destination.scheme")]
    [InlineData("rtmp://example.com", "destination.app")]
    [InlineData("rtmp://user:pw@example.com/app", "destination.userinfo")]
    [InlineData("rtmp://example.com/app?x=1", "destination.query")]
    public void Rejects_bad_urls(string url, string code) =>
        Assert.Contains(SessionValidator.Validate(Session(url)), e => e.Code == code);

    [Theory]
    [InlineData("", "destination.key.empty")]
    [InlineData("has space", "destination.key.invalid")]
    [InlineData("new\nline", "destination.key.invalid")]
    public void Rejects_bad_keys(string key, string code) =>
        Assert.Contains(SessionValidator.Validate(Session(key: key)), e => e.Code == code);

    [Fact]
    public void Rejects_out_of_range_encoder_values()
    {
        var errors = SessionValidator.Validate(Session(enc: new EncoderSettings("libx264; rm", 1921, 1080, 0, 50, Preset: "a b")));
        var codes = errors.Select(e => e.Code).ToHashSet();
        Assert.Contains("encoder.id", codes);
        Assert.Contains("encoder.resolution.even", codes);
        Assert.Contains("encoder.fps", codes);
        Assert.Contains("encoder.video_bitrate", codes);
        Assert.Contains("encoder.preset", codes);
        Assert.All(errors, e => Assert.Equal(ErrorCategory.Configuration, e.Category));
    }
}

public class SilencePadderTests
{
    private static readonly PcmFormat Fmt = new(48000, 2, PcmSampleFormat.F32); // 384000 B/s, align 8

    [Fact]
    public void No_padding_while_data_flows()
    {
        var p = new SilencePadder(Fmt.BytesPerSecond, Fmt.BlockAlign, TimeSpan.FromMilliseconds(100));
        Assert.Equal(0, p.SilenceToWrite(TimeSpan.FromSeconds(1), 0, TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public void Pads_deficit_after_gap_block_aligned()
    {
        var p = new SilencePadder(Fmt.BytesPerSecond, Fmt.BlockAlign, TimeSpan.FromMilliseconds(100));
        var bytes = p.SilenceToWrite(TimeSpan.FromMilliseconds(500), 100_000, TimeSpan.FromMilliseconds(200));
        Assert.Equal(192_000 - 100_000, bytes);
        Assert.Equal(0, bytes % Fmt.BlockAlign);
    }

    [Fact]
    public void Detects_running_ahead_of_wall_clock()
    {
        var p = new SilencePadder(Fmt.BytesPerSecond, Fmt.BlockAlign, TimeSpan.FromMilliseconds(100));
        var lead = TimeSpan.FromMilliseconds(200);
        Assert.False(p.IsAhead(TimeSpan.FromSeconds(1), Fmt.BytesPerSecond + (Fmt.BytesPerSecond / 10), lead));
        Assert.True(p.IsAhead(TimeSpan.FromSeconds(1), Fmt.BytesPerSecond + (Fmt.BytesPerSecond / 4), lead));
    }

    [Fact]
    public void Never_pads_when_ahead_and_caps_at_one_second()
    {
        var p = new SilencePadder(Fmt.BytesPerSecond, Fmt.BlockAlign, TimeSpan.FromMilliseconds(100));
        Assert.Equal(0, p.SilenceToWrite(TimeSpan.FromSeconds(1), 500_000, TimeSpan.FromSeconds(5)));
        Assert.Equal(Fmt.BytesPerSecond, p.SilenceToWrite(TimeSpan.FromSeconds(10), 0, TimeSpan.FromSeconds(10)));
    }
}

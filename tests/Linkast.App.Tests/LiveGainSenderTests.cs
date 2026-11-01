// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.App.ViewModels;

namespace Linkast.App.Tests;

/// <summary>A dragged fader reaches the stream as its newest position, once, and only when it changed.</summary>
public class LiveGainSenderTests
{
    [Fact]
    public void A_dragged_fader_sends_only_its_newest_position()
    {
        var q = new LiveGainSender();
        foreach (var g in new[] { 0.2, 0.4, 0.6, 0.8 })
        {
            q.Request("sys", g);
        }

        Assert.Equal([("sys", 0.8)], q.TakeChanges());
        Assert.False(q.HasPending);
    }

    [Fact]
    public void A_gain_the_stream_has_is_not_sent_again()
    {
        var q = new LiveGainSender();
        q.Request("sys", 0);
        q.TakeChanges();

        // Dragging a muted fader: every step asks for 0 again.
        Assert.False(q.Request("sys", 0));
        Assert.Empty(q.TakeChanges());
    }

    [Fact]
    public void Moving_away_and_back_before_sending_sends_nothing()
    {
        var q = new LiveGainSender();
        q.Request("mic", 1);
        q.TakeChanges();
        q.Request("mic", 1.5);
        q.Request("mic", 1);

        Assert.Empty(q.TakeChanges());
    }

    [Fact]
    public void Channels_are_kept_apart()
    {
        var q = new LiveGainSender();
        q.Request("sys", 0.5);
        q.Request("mic", 2);

        Assert.Equal([("sys", 0.5), ("mic", 2.0)], q.TakeChanges().OrderByDescending(c => c.Channel));
    }

    [Fact]
    public void A_channel_held_by_its_test_sound_is_skipped()
    {
        var q = new LiveGainSender();
        q.Request("web_overlay_1", 0.3);
        q.Note("web_overlay_1", 1); // the test plays at 100 %

        Assert.Empty(q.TakeChanges(skip: "web_overlay_1"));
        Assert.True(q.Request("web_overlay_1", 0.3)); // afterwards the fader is sent back
    }

    [Fact]
    public void A_new_stream_starts_fresh()
    {
        var q = new LiveGainSender();
        q.Request("sys", 0.7);
        q.TakeChanges();
        q.Reset();

        Assert.True(q.Request("sys", 0.7));
    }
}

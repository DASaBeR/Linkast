// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core.Camera;

namespace Linkast.Core.Tests;

/// <summary>Each scene's own stacking order, and the one running stack a stream builds from them.</summary>
public class SceneStackingTests
{
    [Fact]
    public void A_scene_without_its_own_order_uses_the_default() =>
        Assert.Equal(["capture", "cam", "a", "b"], SceneStacking.Resolve([], ["capture", "cam", "a", "b"]));

    [Fact]
    public void A_stored_order_wins_gone_sources_drop_and_new_ones_go_above_their_default_neighbour() =>
        Assert.Equal(["b", "capture", "new", "a"], SceneStacking.Resolve(["b", "gone", "capture", "a"], ["capture", "new", "a", "b"]));

    [Fact]
    public void Moving_a_source_puts_it_at_the_new_place() =>
        Assert.Equal(["a", "capture", "cam", "b"], SceneStacking.Move(["capture", "a", "cam", "b"], "a", 0));

    [Fact]
    public void A_stack_fits_a_scene_when_the_shared_sources_are_in_the_same_order()
    {
        Assert.True(SceneStacking.Fits(["a", "capture", "x", "b"], ["capture", "b"]));
        Assert.False(SceneStacking.Fits(["a", "capture", "b"], ["b", "capture"]));
        Assert.True(SceneStacking.Fits(["a"], ["missing", "a"]));
    }

    [Fact]
    public void Merging_keeps_the_active_scene_and_fits_the_others_where_it_can()
    {
        // Gaming (active): game, cam, alerts. Just chatting: cam, then a picture under the alerts.
        // Starting soon wants the alerts under the cam, which conflicts: it only adds its own sources.
        var stack = SceneStacking.Merge([["game", "cam", "alerts"], ["cam", "picture", "alerts"], ["alerts", "cam", "timer"]]);
        Assert.Equal(["game", "cam", "picture", "alerts", "timer"], stack);
        Assert.True(SceneStacking.Fits(stack, ["game", "cam", "alerts"]));
        Assert.True(SceneStacking.Fits(stack, ["cam", "picture", "alerts"]));
        Assert.False(SceneStacking.Fits(stack, ["alerts", "cam", "timer"]));
    }

    [Fact]
    public void A_new_source_with_nothing_below_it_goes_to_the_bottom() =>
        Assert.Equal(["bg", "game", "cam"], SceneStacking.Merge([["game", "cam"], ["bg", "game"]]));
}

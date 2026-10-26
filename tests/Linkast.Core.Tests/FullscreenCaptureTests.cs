// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core;

namespace Linkast.Core.Tests;

public class FullscreenRulesTests
{
    private static readonly ScreenRect Monitor = new(0, 0, 1920, 1080);

    [Theory]
    [InlineData(0, 0, 1920, 1080, true)]
    [InlineData(-8, -8, 1928, 1088, true)] // a little larger, as some games size themselves
    [InlineData(0, 0, 1920, 1040, false)] // maximized above the taskbar
    [InlineData(100, 100, 1380, 820, false)]
    public void Covers_monitor_only_when_every_edge_is_reached(int l, int t, int r, int b, bool expected) =>
        Assert.Equal(expected, FullscreenRules.CoversMonitor(new(l, t, r, b), Monitor));

    [Fact]
    public void Covers_a_second_monitor_in_its_own_coordinates() =>
        Assert.True(FullscreenRules.CoversMonitor(new(1920, 0, 4480, 1440), new(1920, 0, 4480, 1440)));

    [Fact]
    public void Exclusive_needs_the_window_in_front_and_windows_to_report_it()
    {
        Assert.Equal(FullscreenKind.Exclusive, FullscreenRules.Classify(minimized: false, foreground: true, hasCaption: false, coversMonitor: true, exclusiveReported: true));
        Assert.Equal(FullscreenKind.Fullscreen, FullscreenRules.Classify(minimized: false, foreground: false, hasCaption: false, coversMonitor: true, exclusiveReported: true));
        Assert.Equal(FullscreenKind.Fullscreen, FullscreenRules.Classify(minimized: false, foreground: true, hasCaption: false, coversMonitor: true, exclusiveReported: false));
    }

    [Theory]
    [InlineData(true, false, true)] // minimized
    [InlineData(false, true, true)] // has a title bar (maximized with an auto-hidden taskbar)
    [InlineData(false, false, false)] // does not fill the monitor
    public void Ordinary_windows_are_not_fullscreen(bool minimized, bool hasCaption, bool covers) =>
        Assert.Equal(FullscreenKind.None, FullscreenRules.Classify(minimized, foreground: true, hasCaption, covers, exclusiveReported: true));

    [Theory]
    [InlineData("~ DISABLEDXMAXIMIZEDWINDOWEDMODE", true)]
    [InlineData("~ HIGHDPIAWARE DisableDXMaximizedWindowedMode", true)]
    [InlineData("~ HIGHDPIAWARE", false)]
    [InlineData("~ DISABLEDXMAXIMIZEDWINDOWEDMODEX", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Reads_the_fullscreen_optimizations_flag(string? layers, bool expected) =>
        Assert.Equal(expected, FullscreenRules.HasOptimizationsDisabled(layers));
}

public class FullscreenTrackerTests
{
    private static readonly FullscreenState Exclusive = new(FullscreenKind.Exclusive, 1, "game.exe", false);
    private static readonly FullscreenState Plain = new(FullscreenKind.None, 1, "game.exe", false);

    [Fact]
    public void Keeps_exclusive_while_the_game_is_minimized()
    {
        var t = new FullscreenTracker();
        Assert.True(t.Observe(10, Exclusive, minimized: false));
        Assert.False(t.Observe(10, Plain, minimized: true));
        Assert.Equal(FullscreenKind.Exclusive, t.Current.Kind);
    }

    [Fact]
    public void Forgets_once_the_game_is_seen_as_a_normal_window()
    {
        var t = new FullscreenTracker();
        t.Observe(10, Exclusive, minimized: false);
        Assert.True(t.Observe(10, Plain, minimized: false));
        Assert.Equal(FullscreenKind.None, t.Current.Kind);
    }

    [Fact]
    public void Another_window_starts_fresh()
    {
        var t = new FullscreenTracker();
        t.Observe(10, Exclusive, minimized: false);
        t.Observe(11, Plain, minimized: true);
        Assert.Equal(FullscreenKind.None, t.Current.Kind);
    }

    [Fact]
    public void Picks_up_a_changed_optimizations_setting_while_minimized()
    {
        var t = new FullscreenTracker();
        t.Observe(10, Exclusive, minimized: false);
        Assert.True(t.Observe(10, Plain with { OptimizationsDisabled = true }, minimized: true));
        Assert.Equal(FullscreenKind.Exclusive, t.Current.Kind);
        Assert.True(t.Current.OptimizationsDisabled);
    }
}

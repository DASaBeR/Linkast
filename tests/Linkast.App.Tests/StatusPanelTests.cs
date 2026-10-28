// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Windows;
using Linkast.App.ViewModels;
using Linkast.Core;

namespace Linkast.App.Tests;

/// <summary>The status panel's "where to fix it" and "what is missing" logic, which drives buttons and checklist text.</summary>
public class StatusPanelTests
{
    [Theory]
    [InlineData(ErrorCategory.Configuration, "destination.scheme", "Destination")]
    [InlineData(ErrorCategory.Configuration, "destination.host", "Destination")]
    [InlineData(ErrorCategory.Configuration, "encoder.fps", "Quality")]
    [InlineData(ErrorCategory.Encoder, "nvenc.init", "Quality")]
    [InlineData(ErrorCategory.Network, "rtmp.connect", "Network")]
    [InlineData(ErrorCategory.Capture, "wgc.start", "Capture")]
    [InlineData(ErrorCategory.Audio, "wasapi.open", "Audio")]
    public void Errors_open_the_tab_that_fixes_them(ErrorCategory category, string code, string tab) =>
        Assert.Equal(tab, Converters.ErrorFixTab(new StreamError(category, code, "message")));

    [Theory]
    [InlineData(ErrorCategory.Process, "ffmpeg.exit")]
    [InlineData(ErrorCategory.Unknown, "unknown")]
    [InlineData(ErrorCategory.Configuration, "settings.corrupt")]
    public void Errors_no_tab_can_fix_offer_no_button(ErrorCategory category, string code)
    {
        var error = new StreamError(category, code, "message");
        Assert.Null(Converters.ErrorFixTab(error));
        Assert.Equal(Visibility.Collapsed, Converters.ErrorFixVisibility.Convert(error, typeof(Visibility), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void No_error_shows_no_fix_button() =>
        Assert.Null(Converters.ErrorFixLabel.Convert(null, typeof(string), null, CultureInfo.InvariantCulture));

    [Fact]
    public void Fix_button_names_the_tab() =>
        Assert.Equal("Open Destination settings",
            Converters.ErrorFixLabel.Convert(new StreamError(ErrorCategory.Configuration, "destination.app", "m"), typeof(string), null, CultureInfo.InvariantCulture));

    [Theory]
    [InlineData("", false, "Custom server", "Add the server address.")]
    [InlineData("   ", true, "Custom server", "Add the server address.")]
    [InlineData("rtmp://live.twitch.tv/app", false, "Twitch", "Add your stream key for Twitch.")]
    [InlineData("rtmp://live.twitch.tv/app", true, "Twitch", "Twitch, stream key saved.")]
    public void Destination_step_says_what_is_missing(string server, bool hasKey, string where, string expected) =>
        Assert.Equal(expected,
            Converters.DestinationCheck.Convert([server, hasKey, where], typeof(string), null, CultureInfo.InvariantCulture));

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void All_true_needs_every_value(bool a, bool b, bool expected) =>
        Assert.Equal(expected, Converters.AllTrue.Convert([a, b], typeof(bool), null, CultureInfo.InvariantCulture));

    [Theory]
    [InlineData(null, Visibility.Visible)]
    [InlineData("", Visibility.Visible)]
    [InlineData("  ", Visibility.Visible)]
    [InlineData("rtmp://host/live", Visibility.Collapsed)]
    public void Empty_field_hint(string? value, Visibility expected) =>
        Assert.Equal(expected, Converters.EmptyToVisibility.Convert(value, typeof(Visibility), null, CultureInfo.InvariantCulture));
}

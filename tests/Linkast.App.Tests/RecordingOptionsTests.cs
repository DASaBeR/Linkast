// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.App.ViewModels;
using Linkast.Core;

namespace Linkast.App.Tests;

public class RecordingOptionsTests
{
    [Fact]
    public void RecordingQualityOption_maps_legacy_854x480_to_852x480()
    {
        var option = RecordingQualityOption.For(RecordingQuality.Custom, 854, 480);
        Assert.True(option.IsCustom);
        Assert.Equal((852, 480, 4000), (option.Width, option.Height, option.DefaultBitrateKbps));
    }

    [Fact]
    public void RecordingQualityOption_defaults_to_same_as_stream()
    {
        var option = RecordingQualityOption.For(RecordingQuality.SameAsStream, 1920, 1080);
        Assert.False(option.IsCustom);
        Assert.Equal(RecordingQuality.SameAsStream, option.Quality);
    }

    [Fact]
    public void VerticalResolutionOption_falls_back_to_1080x1920()
    {
        Assert.Equal((1080, 1920), (VerticalResolutionOption.For(1080, 1920).Width, VerticalResolutionOption.For(1080, 1920).Height));
        Assert.Equal((720, 1280), (VerticalResolutionOption.For(720, 1280).Width, VerticalResolutionOption.For(720, 1280).Height));
        Assert.Equal((1080, 1920), (VerticalResolutionOption.For(999, 999).Width, VerticalResolutionOption.For(999, 999).Height));
    }
}

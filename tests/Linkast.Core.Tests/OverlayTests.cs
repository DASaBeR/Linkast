// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core.Camera;
using Linkast.Core.Settings;

namespace Linkast.Core.Tests;

public class OverlayTests
{
    [Fact]
    public void Full_screen_fits_the_canvas_without_stretching()
    {
        var wide = OverlayGeometry.Place(OverlayPosition.FullScreen, 0.5, 16.0 / 9, 1920, 1080);
        Assert.Equal(new NormalizedRect(0, 0, 1, 1), wide);

        // A 16:9 alert on a vertical stream keeps its shape: full width, centred vertically.
        var vertical = OverlayGeometry.Place(OverlayPosition.FullScreen, 0.5, 16.0 / 9, 1080, 1920);
        Assert.Equal(1, vertical.Width, 6);
        Assert.Equal(1080.0 / 1920 * 1080 / 1920, vertical.Height, 6);
        Assert.Equal((1 - vertical.Height) / 2, vertical.Y, 6);
    }

    [Fact]
    public void Corner_positions_keep_the_margin_and_the_aspect_ratio()
    {
        var r = OverlayGeometry.Place(OverlayPosition.TopRight, 0.2, 1, 1920, 1080);
        Assert.Equal(0.2, r.Width, 6);
        Assert.Equal(0.2 * 1920 / 1080, r.Height, 6); // square in pixels
        Assert.Equal(1 - 0.2 - OverlayGeometry.Margin, r.X, 6);
        Assert.Equal(OverlayGeometry.Margin * 1920 / 1080, r.Y, 6);

        var bottomLeft = OverlayGeometry.Place(OverlayPosition.BottomLeft, 0.2, 1, 1920, 1080);
        Assert.Equal(1, bottomLeft.Y + bottomLeft.Height + (OverlayGeometry.Margin * 1920 / 1080), 6);
    }

    [Fact]
    public void A_tall_picture_never_leaves_the_canvas()
    {
        var r = OverlayGeometry.Place(OverlayPosition.Center, 1, 0.2, 1920, 1080);
        Assert.True(r.Height <= 1 && r.Y >= 0 && r.Y + r.Height <= 1 + 1e-9);
    }

    [Fact]
    public void Overlay_settings_are_sanitized_and_round_trip()
    {
        var items = Enumerable.Range(1, 5).Select(i => new OverlayConfig { LayerId = "o", Name = new string('x', 80), Size = 9 }).ToList();
        var s = new LinkastSettings { Overlays = new OverlaySettings { Items = items, Away = new AwayScreenSettings { Title = "  " } } };
        var clean = SettingsSerializer.Sanitize(s).Overlays;
        Assert.Single(clean.Items); // duplicate IDs dropped
        Assert.Equal(40, clean.Items[0].Name.Length);
        Assert.Equal(1, clean.Items[0].Size);
        Assert.Equal("Be right back", clean.Away.Title);

        var (back, _) = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(new LinkastSettings
        {
            Overlays = new OverlaySettings { Items = [new OverlayConfig { LayerId = "logo", Kind = OverlayKind.Image, ImagePath = @"C:\logo.png", Position = OverlayPosition.BottomRight }] },
        }));
        Assert.Equal(OverlayPosition.BottomRight, back.Overlays.Items[0].Position);
        Assert.Equal(@"C:\logo.png", back.Overlays.Items[0].ImagePath);
        Assert.True(back.Overlays.Away.MuteSystemAudio);
    }
}

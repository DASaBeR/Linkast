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
    public void Web_overlay_page_size_is_clamped_and_kept()
    {
        var s = new LinkastSettings
        {
            Overlays = new OverlaySettings { Items = [new OverlayConfig { LayerId = "alerts", Kind = OverlayKind.Web, PageWidth = 99999, PageHeight = 1 }] },
        };
        var web = SettingsSerializer.Sanitize(s).Overlays.Items[0];
        Assert.Equal(OverlayKind.Web, web.Kind);
        Assert.Equal(3840, web.PageWidth);
        Assert.Equal(100, web.PageHeight);

        // The link is a secret: it is never part of the settings file.
        var overlaysJson = System.Text.Json.JsonSerializer.Serialize(s.Overlays, SettingsSerializer.Options);
        Assert.DoesNotContain("url", overlaysJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_dragged_overlay_keeps_its_rectangle_and_needs_one()
    {
        var dragged = new NormalizedRect(0.6, 0.7, 0.3, 0.2);
        var s = new LinkastSettings
        {
            Overlays = new OverlaySettings
            {
                Items =
                [
                    new OverlayConfig { LayerId = "a", Position = OverlayPosition.Custom, CustomPlacement = dragged },
                    new OverlayConfig { LayerId = "b", Position = OverlayPosition.Custom },
                    new OverlayConfig { LayerId = "c", Position = OverlayPosition.Custom, CustomPlacement = new NormalizedRect(2, 2, 0.5, 0.5) },
                ],
            },
        };
        var clean = SettingsSerializer.Sanitize(s).Overlays.Items;
        Assert.Equal(OverlayPosition.Custom, clean[0].Position);
        Assert.Equal(dragged, clean[0].CustomPlacement);
        Assert.Equal(OverlayPosition.FullScreen, clean[1].Position);
        Assert.Equal(new NormalizedRect(0.5, 0.5, 0.5, 0.5), clean[2].CustomPlacement);

        var (back, _) = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(s));
        Assert.Equal(dragged, back.Overlays.Items[0].CustomPlacement);

        // Custom and full screen are placed alike by the geometry; the view model substitutes the rectangle.
        Assert.Equal(OverlayGeometry.Place(OverlayPosition.FullScreen, 1, 1, 1920, 1080), OverlayGeometry.Place(OverlayPosition.Custom, 1, 1, 1920, 1080));
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

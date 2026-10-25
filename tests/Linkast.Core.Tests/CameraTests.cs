// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core.Camera;

namespace Linkast.Core.Tests;

public class CameraFormatRequestTests
{
    private static readonly CameraFormat[] Formats =
    [
        new(3840, 2160, 30, 1, "YUY2"),
        new(1920, 1080, 30, 1, "MJPG"),
        new(1920, 1080, 30, 1, "NV12"),
        new(1280, 720, 60, 1, "YUY2"),
        new(1280, 720, 30, 1, "MJPG"),
        new(1280, 720, 30, 1, "NV12"),
        new(640, 480, 30, 1, "YUY2"),
    ];

    [Fact]
    public void Auto_prefers_720p30_uncompressed() =>
        Assert.Equal(new CameraFormat(1280, 720, 30, 1, "NV12"), CameraFormatRequest.Auto.Choose(Formats));

    [Fact]
    public void Explicit_request_matches_exactly()
    {
        var f = new CameraFormatRequest(1920, 1080, 30).Choose(Formats);
        Assert.Equal(1920, f!.Width);
        Assert.Equal("NV12", f.Subtype);
    }

    [Fact]
    public void Unavailable_request_falls_back_to_closest_instead_of_failing()
    {
        var f = new CameraFormatRequest(2560, 1440, 30).Choose(Formats);
        Assert.NotNull(f);
        Assert.Equal(1920, f.Width);
    }

    [Fact]
    public void No_formats_returns_null() => Assert.Null(CameraFormatRequest.Auto.Choose([]));
}

public class LayerGeometryTests
{
    [Fact]
    public void Default_placement_is_bottom_right_with_layer_aspect()
    {
        var p = LayerGeometry.DefaultPlacement(16.0 / 9, 1920, 1080);
        var px = LayerGeometry.ToPixels(p, 1920, 1080);
        Assert.True(px.X + px.Width <= 1920 && px.Y + px.Height <= 1080);
        Assert.True(px.X > 1920 / 2 && px.Y > 1080 / 2);
        Assert.Equal(16.0 / 9, px.Width / (double)px.Height, 1);
    }

    [Fact]
    public void Pixels_are_even_and_inside_canvas()
    {
        var px = LayerGeometry.ToPixels(new NormalizedRect(0.9, 0.9, 0.333, 0.333), 1920, 1080);
        Assert.Equal(0, px.Width % 2);
        Assert.Equal(0, px.Height % 2);
        Assert.True(px.X + px.Width <= 1920);
        Assert.True(px.Y + px.Height <= 1080);
    }

    [Fact]
    public void Clamped_rejects_nan_and_out_of_range()
    {
        var r = new NormalizedRect(double.NaN, -5, 3, 0.0001).Clamped();
        Assert.InRange(r.X, 0, 1);
        Assert.InRange(r.Y, 0, 1);
        Assert.True(r.Right <= 1 + 1e-9 && r.Bottom <= 1 + 1e-9);
    }

    [Fact]
    public void Square_crop_from_wide_source_is_centered_and_zoomable()
    {
        var (x, y, w, h) = LayerGeometry.SourceCrop(1280, 720, 1, 1, 0.5, 0.5);
        Assert.Equal((280.0, 0.0, 720.0, 720.0), (x, y, w, h));
        var (zx, zy, zw, zh) = LayerGeometry.SourceCrop(1280, 720, 1, 2, 0.5, 0.5);
        Assert.Equal((460.0, 180.0, 360.0, 360.0), (zx, zy, zw, zh));
    }

    [Fact]
    public void Crop_center_is_kept_inside_source()
    {
        var (x, _, w, _) = LayerGeometry.SourceCrop(1280, 720, 16.0 / 9, 2, 1.0, 0.5);
        Assert.Equal(1280, x + w, 6);
    }
}

public class LatestFrameExchangeTests
{
    [Fact]
    public void Reader_gets_newest_frame_once()
    {
        var ex = new LatestFrameExchange();
        Write(ex, 1);
        Write(ex, 2);
        var f = ex.TryBeginRead(0);
        Assert.Equal(2, f!.Pixels[0]);
        ex.EndRead(f);
        Assert.Null(ex.TryBeginRead(f.Sequence));
    }

    [Fact]
    public void Producer_never_overwrites_a_frame_being_read()
    {
        var ex = new LatestFrameExchange();
        Write(ex, 1);
        var a = ex.TryBeginRead(0)!;
        var b = ex.TryBeginRead(0)!; // second consumer on the same frame
        for (byte i = 2; i < 20; i++)
        {
            Write(ex, i);
        }

        Assert.Equal(1, a.Pixels[0]);
        Assert.Equal(1, b.Pixels[0]);
        ex.EndRead(a);
        ex.EndRead(b);
        var latest = ex.TryBeginRead(0)!;
        Assert.Equal(19, latest.Pixels[0]);
        ex.EndRead(latest);
    }

    [Fact]
    public void Drops_rather_than_blocks_when_all_buffers_are_held()
    {
        var ex = new LatestFrameExchange();
        var held = new List<BgraFrame>();
        for (byte i = 1; i <= 3; i++)
        {
            Write(ex, i);
            held.Add(ex.TryBeginRead(i - 1)!);
        }

        Write(ex, 4); // newest frame occupies the fourth buffer
        Assert.Null(ex.BeginWrite(2, 2)); // three held + latest = all four buffers busy
        Assert.Equal(1, ex.DroppedCount);
        ex.EndRead(held[0]);
        Assert.NotNull(ex.BeginWrite(2, 2));
    }

    private static void Write(LatestFrameExchange ex, byte value)
    {
        var f = ex.BeginWrite(2, 2)!;
        Array.Fill(f.Pixels, value);
        ex.Publish(TimeSpan.Zero);
    }
}

public class CameraLayerRendererTests
{
    private static BgraFrame Solid(int w, int h, byte b, byte g, byte r)
    {
        var f = new BgraFrame(w, h);
        for (var i = 0; i < f.Pixels.Length; i += 4)
        {
            f.Pixels[i] = b;
            f.Pixels[i + 1] = g;
            f.Pixels[i + 2] = r;
            f.Pixels[i + 3] = 255;
        }

        return f;
    }

    private static (byte B, byte G, byte R, byte A) Px(byte[] img, int w, int x, int y)
    {
        var o = ((y * w) + x) * 4;
        return (img[o], img[o + 1], img[o + 2], img[o + 3]);
    }

    [Fact]
    public void Neutral_rectangle_reproduces_source()
    {
        var dst = new byte[64 * 36 * 4];
        new CameraLayerRenderer().Render(Solid(128, 72, 10, 120, 200), CameraLayerSettings.Default with { Shape = LayerShape.Rectangle }, dst, 64, 36);
        Assert.Equal(((byte)10, (byte)120, (byte)200, (byte)255), Px(dst, 64, 0, 0));
        Assert.Equal(((byte)10, (byte)120, (byte)200, (byte)255), Px(dst, 64, 32, 18));
    }

    [Fact]
    public void Circle_is_transparent_in_corners_opaque_in_center()
    {
        var dst = new byte[50 * 50 * 4];
        new CameraLayerRenderer().Render(Solid(100, 100, 50, 50, 50), CameraLayerSettings.Default with { Shape = LayerShape.Circle }, dst, 50, 50);
        Assert.Equal(0, Px(dst, 50, 0, 0).A);
        Assert.Equal(0, Px(dst, 50, 49, 49).A);
        Assert.Equal(255, Px(dst, 50, 25, 25).A);
        Assert.True(Px(dst, 50, 25, 0).A >= 250); // top centre touches the edge (anti-aliased)
    }

    [Fact]
    public void Rounded_corners_are_transparent_edges_are_not()
    {
        Assert.Equal(0, CameraLayerRenderer.MaskAlpha(LayerShape.RoundedRectangle, 0.2, 100, 100, 0, 0));
        Assert.Equal(255, CameraLayerRenderer.MaskAlpha(LayerShape.RoundedRectangle, 0.2, 100, 100, 50, 0));
        Assert.Equal(255, CameraLayerRenderer.MaskAlpha(LayerShape.Rectangle, 0, 100, 100, 0, 0));
    }

    [Fact]
    public void Border_paints_edge_with_border_colour()
    {
        var dst = new byte[100 * 100 * 4];
        var s = CameraLayerSettings.Default with { Shape = LayerShape.Rectangle, BorderWidth = 0.05, BorderColor = new Argb(255, 255, 0, 0) };
        new CameraLayerRenderer().Render(Solid(100, 100, 0, 255, 0), s, dst, 100, 100);
        Assert.Equal(((byte)0, (byte)0, (byte)255, (byte)255), Px(dst, 100, 1, 50)); // red border (BGRA)
        Assert.Equal(((byte)0, (byte)255, (byte)0, (byte)255), Px(dst, 100, 50, 50)); // green inside
    }

    [Fact]
    public void Brightness_contrast_and_saturation_change_pixels()
    {
        var src = Solid(16, 16, 40, 100, 200);
        var dst = new byte[16 * 16 * 4];
        var r = new CameraLayerRenderer();

        r.Render(src, CameraLayerSettings.Default with { Shape = LayerShape.Rectangle, Adjustments = new() { Brightness = 0.4 } }, dst, 16, 16);
        Assert.True(Px(dst, 16, 8, 8).G > 100);

        r.Render(src, CameraLayerSettings.Default with { Shape = LayerShape.Rectangle, Adjustments = new() { Saturation = 0 } }, dst, 16, 16);
        var grey = Px(dst, 16, 8, 8);
        Assert.Equal(grey.B, grey.G);
        Assert.Equal(grey.G, grey.R);

        r.Render(src, CameraLayerSettings.Default with { Shape = LayerShape.Rectangle, Adjustments = new() { Contrast = 2 } }, dst, 16, 16);
        var c = Px(dst, 16, 8, 8);
        Assert.True(c.R > 200 && c.B < 40);
    }

    [Fact]
    public void Mirror_flips_horizontally()
    {
        var src = new BgraFrame(2, 1);
        src.Pixels[0] = 255; // left pixel blue
        src.Pixels[3] = src.Pixels[7] = 255;
        var dst = new byte[2 * 1 * 4];
        new CameraLayerRenderer().Render(src, CameraLayerSettings.Default with { Shape = LayerShape.Rectangle, Mirror = true, Aspect = LayerAspect.Wide16x9 }, dst, 2, 1);
        Assert.True(dst[4] > dst[0], "Blue should now be on the right.");
    }

    [Fact]
    public void Sharpen_increases_edge_contrast()
    {
        // Vertical edge: left half dark grey, right half light grey.
        var src = new BgraFrame(32, 16);
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 32; x++)
            {
                var v = (byte)(x < 16 ? 80 : 170);
                var o = ((y * 32) + x) * 4;
                src.Pixels[o] = src.Pixels[o + 1] = src.Pixels[o + 2] = v;
                src.Pixels[o + 3] = 255;
            }
        }

        var plain = new byte[32 * 16 * 4];
        var sharp = new byte[32 * 16 * 4];
        var s = CameraLayerSettings.Default with { Shape = LayerShape.Rectangle, Aspect = LayerAspect.Wide16x9 };
        new CameraLayerRenderer().Render(src, s, plain, 32, 16);
        new CameraLayerRenderer().Render(src, s with { Adjustments = new() { Sharpen = 1 } }, sharp, 32, 16);
        Assert.True(Px(sharp, 32, 15, 8).G < Px(plain, 32, 15, 8).G, "Dark side of the edge should get darker.");
        Assert.True(Px(sharp, 32, 16, 8).G > Px(plain, 32, 16, 8).G, "Light side of the edge should get lighter.");
        Assert.Equal(Px(plain, 32, 4, 8).G, Px(sharp, 32, 4, 8).G); // flat areas untouched
    }

    [Fact]
    public void Denoise_smooths_small_flicker_but_keeps_real_motion()
    {
        var r = new CameraLayerRenderer();
        var s = CameraLayerSettings.Default with { Shape = LayerShape.Rectangle, Adjustments = new() { Denoise = 1 } };
        var dst = new byte[16 * 16 * 4];
        r.Render(Solid(16, 16, 100, 100, 100), s, dst, 16, 16);

        r.Render(Solid(16, 16, 108, 108, 108), s, dst, 16, 16); // sensor noise (+8)
        Assert.True(Px(dst, 16, 8, 8).G < 108, "Small change should be damped.");

        r.Render(Solid(16, 16, 200, 200, 200), s, dst, 16, 16); // real change (+~95)
        Assert.Equal(200, Px(dst, 16, 8, 8).G);
    }

    [Fact]
    public void Low_light_boost_brightens_dark_scenes_only()
    {
        var dark = Solid(32, 18, 30, 30, 30);
        var bright = Solid(32, 18, 180, 180, 180);
        var s = CameraLayerSettings.Default with { Shape = LayerShape.Rectangle, Adjustments = new() { LowLightBoost = 1 } };
        var r = new CameraLayerRenderer();
        var dst = new byte[32 * 18 * 4];
        for (var i = 0; i < 60; i++)
        {
            r.Render(dark, s, dst, 32, 18); // smoothing converges over frames
        }

        Assert.True(Px(dst, 32, 16, 9).G > 60, $"Dark scene should be lifted (got {Px(dst, 32, 16, 9).G}).");

        var r2 = new CameraLayerRenderer();
        for (var i = 0; i < 60; i++)
        {
            r2.Render(bright, s, dst, 32, 18);
        }

        Assert.Equal(180, Px(dst, 32, 16, 9).G);
    }

    private static SegmentationMask PersonInMiddle(int w, int h)
    {
        // Person = middle third of the frame.
        var m = new SegmentationMask(w, h);
        for (var y = 0; y < h; y++)
        {
            for (var x = w / 3; x < 2 * w / 3; x++)
            {
                m.Alpha[(y * w) + x] = 255;
            }
        }

        return m;
    }

    private static BgraFrame Stripes(int w, int h)
    {
        // High-contrast vertical stripes so a blur is easy to detect.
        var f = new BgraFrame(w, h);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var v = (byte)((x / 4) % 2 == 0 ? 20 : 230);
                var o = ((y * w) + x) * 4;
                f.Pixels[o] = f.Pixels[o + 1] = f.Pixels[o + 2] = v;
                f.Pixels[o + 3] = 255;
            }
        }

        return f;
    }

    [Fact]
    public void Remove_makes_background_transparent_and_keeps_person()
    {
        var src = Stripes(96, 54);
        var dst = new byte[96 * 54 * 4];
        var s = CameraLayerSettings.Default with { Shape = LayerShape.Rectangle, Background = new() { Mode = BackgroundMode.Remove, EdgeSoftness = 0 } };
        new CameraLayerRenderer().Render(src, s, dst, 96, 54, PersonInMiddle(96, 54));
        Assert.Equal(0, Px(dst, 96, 5, 27).A);
        Assert.Equal(255, Px(dst, 96, 48, 27).A);
    }

    [Fact]
    public void Blur_smooths_background_but_not_person()
    {
        var src = Stripes(96, 54);
        var plain = new byte[96 * 54 * 4];
        var blurred = new byte[96 * 54 * 4];
        var look = CameraLayerSettings.Default with { Shape = LayerShape.Rectangle };
        var mask = PersonInMiddle(96, 54);
        new CameraLayerRenderer().Render(src, look, plain, 96, 54, mask);
        new CameraLayerRenderer().Render(src, look with { Background = new() { Mode = BackgroundMode.Blur, BlurStrength = 1, EdgeSoftness = 0 } }, blurred, 96, 54, mask);

        static int Contrast(byte[] img, int x0) => Enumerable.Range(x0, 12).Max(x => Px(img, 96, x, 27).G) - Enumerable.Range(x0, 12).Min(x => Px(img, 96, x, 27).G);
        Assert.True(Contrast(blurred, 4) < Contrast(plain, 4) / 3, "Background stripes should be blurred away.");
        Assert.Equal(Contrast(plain, 42), Contrast(blurred, 42)); // person area untouched
    }

    [Fact]
    public void Image_mode_uses_the_image_and_falls_back_to_blur_without_one()
    {
        var src = Stripes(96, 54);
        var red = Solid(8, 8, 0, 0, 255);
        var dst = new byte[96 * 54 * 4];
        var s = CameraLayerSettings.Default with { Shape = LayerShape.Rectangle, Background = new() { Mode = BackgroundMode.Image, EdgeSoftness = 0 } };
        new CameraLayerRenderer().Render(src, s, dst, 96, 54, PersonInMiddle(96, 54), red);
        Assert.Equal(((byte)0, (byte)0, (byte)255, (byte)255), Px(dst, 96, 5, 27));

        var fallback = new byte[96 * 54 * 4];
        new CameraLayerRenderer().Render(src, s, fallback, 96, 54, PersonInMiddle(96, 54), backgroundImage: null);
        Assert.NotEqual(Px(src.Pixels, 96, 5, 27), Px(fallback, 96, 5, 27)); // blurred, not the raw stripes
    }

    [Fact]
    public void Without_a_mask_effects_show_the_original_image()
    {
        var src = Stripes(96, 54);
        var plain = new byte[96 * 54 * 4];
        var effect = new byte[96 * 54 * 4];
        var look = CameraLayerSettings.Default with { Shape = LayerShape.Rectangle };
        new CameraLayerRenderer().Render(src, look, plain, 96, 54);
        new CameraLayerRenderer().Render(src, look with { Background = new() { Mode = BackgroundMode.Remove } }, effect, 96, 54, segmentation: null);
        Assert.Equal(plain, effect);
    }

    [Fact]
    public void Mask_follows_crop_and_mirror()
    {
        // Person on the left third of the source; mirrored output should show them on the right.
        var src = Stripes(96, 54);
        var mask = new SegmentationMask(96, 54);
        for (var y = 0; y < 54; y++)
        {
            for (var x = 0; x < 32; x++)
            {
                mask.Alpha[(y * 96) + x] = 255;
            }
        }

        var dst = new byte[96 * 54 * 4];
        var s = CameraLayerSettings.Default with { Shape = LayerShape.Rectangle, Mirror = true, Background = new() { Mode = BackgroundMode.Remove, EdgeSoftness = 0 } };
        new CameraLayerRenderer().Render(src, s, dst, 96, 54, mask);
        Assert.Equal(255, Px(dst, 96, 90, 27).A);
        Assert.Equal(0, Px(dst, 96, 5, 27).A);
    }

    [Fact]
    public void Transparent_clears_everything()
    {
        var dst = Enumerable.Repeat((byte)7, 16).ToArray();
        CameraLayerRenderer.RenderTransparent(dst, 2, 2);
        Assert.All(dst, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Settings_are_clamped()
    {
        var s = new CameraLayerSettings { Zoom = 99, CenterX = -1, BorderWidth = 5, Adjustments = new() { Brightness = double.NaN, Contrast = 9 } }.Clamped();
        Assert.Equal(4, s.Zoom);
        Assert.Equal(0, s.CenterX);
        Assert.Equal(0.1, s.BorderWidth);
        Assert.Equal(0, s.Adjustments.Brightness);
        Assert.Equal(2, s.Adjustments.Contrast);
    }
}

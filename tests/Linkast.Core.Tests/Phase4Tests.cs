// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core.Audio;
using Linkast.Core.Camera;

namespace Linkast.Core.Tests;

public class AutoFramerTests
{
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(33);

    private static AutoFramer Framer(double speed = 0.5) =>
        new(new FramingSettings { Enabled = true, Headroom = 0.12, FaceSize = 0.3, MaxZoom = 3, Speed = speed }, 16.0 / 9);

    private static FramingCrop Run(AutoFramer f, FaceBox? face, double seconds, ref TimeSpan t)
    {
        FramingCrop crop = default;
        for (var i = 0; i < seconds / Frame.TotalSeconds; i++)
        {
            t += Frame;
            if (face is { } fb && i % 3 == 0)
            {
                f.OnDetection([fb], t); // detection at ~10 Hz
            }

            crop = f.Update(t);
        }

        return crop;
    }

    [Fact]
    public void Frames_a_face_with_target_size_and_headroom()
    {
        var f = Framer();
        var t = TimeSpan.Zero;
        var face = new FaceBox(0.55, 0.3, 0.1, 0.15);
        var crop = Run(f, face, 6, ref t);
        Assert.Equal(0.6, crop.CenterX, 2);              // follows the face horizontally
        Assert.Equal(0.15 / 0.3, crop.Height, 2);        // face is 30 % of crop height
        var top = crop.CenterY - (crop.Height / 2);
        Assert.Equal(0.12, (face.Y - top) / crop.Height, 2); // 12 % headroom
    }

    [Fact]
    public void Does_not_move_for_small_jitter()
    {
        var f = Framer();
        var t = TimeSpan.Zero;
        Run(f, new FaceBox(0.45, 0.3, 0.1, 0.15), 6, ref t);
        var settled = f.Current;

        // Detector noise of ±0.5 % in position and ±3 % in size: the crop must stay put.
        var rng = new Random(1);
        for (var i = 0; i < 200; i++)
        {
            t += Frame;
            if (i % 3 == 0)
            {
                f.OnDetection([new FaceBox(0.45 + ((rng.NextDouble() - 0.5) * 0.01), 0.3 + ((rng.NextDouble() - 0.5) * 0.01), 0.1, 0.15 * (1 + ((rng.NextDouble() - 0.5) * 0.06)))], t);
            }

            f.Update(t);
        }

        Assert.Equal(settled.CenterX, f.Current.CenterX, 3);
        Assert.Equal(settled.Height, f.Current.Height, 3);
    }

    [Fact]
    public void Moves_smoothly_and_frame_rate_independently()
    {
        var f30 = Framer();
        var f60 = Framer();
        var start = new FaceBox(0.2, 0.3, 0.1, 0.15);
        var moved = new FaceBox(0.4, 0.3, 0.1, 0.15);
        for (var i = 0; i <= 300; i++)
        {
            // The face is seen continuously (10 Hz) at the start position for 10 s.
            var t = TimeSpan.FromSeconds(i / 30.0);
            if (i % 3 == 0)
            {
                f30.OnDetection([start], t);
                f60.OnDetection([start], t);
            }

            f30.Update(t);
            f60.Update(t);
        }

        f30.OnDetection([moved], TimeSpan.FromSeconds(10));
        f60.OnDetection([moved], TimeSpan.FromSeconds(10));
        double previous = f30.Current.CenterX;
        for (var i = 1; i <= 15; i++)
        {
            var c = f30.Update(TimeSpan.FromSeconds(10 + (i / 30.0)));
            Assert.True(c.CenterX >= previous - 1e-9, "Movement must be monotonic (no overshoot or oscillation).");
            Assert.True(c.CenterX - previous < 0.05, "No sudden jumps.");
            previous = c.CenterX;
        }

        for (var i = 1; i <= 30; i++)
        {
            f60.Update(TimeSpan.FromSeconds(10 + (i / 60.0)));
        }

        Assert.Equal(f30.Current.CenterX, f60.Current.CenterX, 3); // same time, same position at 30 and 60 fps
    }

    [Fact]
    public void Ignores_a_single_false_detection_far_away()
    {
        var f = Framer();
        var t = TimeSpan.Zero;
        Run(f, new FaceBox(0.2, 0.3, 0.1, 0.15), 5, ref t);
        var target = f.Target;
        f.OnDetection([new FaceBox(0.85, 0.6, 0.05, 0.08)], t); // one stray detection
        Assert.Equal(target, f.Target);
    }

    [Fact]
    public void Holds_then_eases_out_when_the_face_is_lost()
    {
        var f = Framer();
        var t = TimeSpan.Zero;
        var framed = Run(f, new FaceBox(0.6, 0.3, 0.1, 0.15), 6, ref t);
        Assert.True(framed.Height < 0.6);

        var held = Run(f, null, 1.5, ref t);
        Assert.Equal(framed.Height, held.Height, 2); // last stable crop held for 2 s

        var later = Run(f, null, 12, ref t);
        Assert.False(f.Tracking);
        Assert.True(later.Height > 0.95, "Eventually eases back to the full view.");
    }

    [Fact]
    public void Respects_max_zoom()
    {
        var f = new AutoFramer(new FramingSettings { Enabled = true, FaceSize = 0.45, MaxZoom = 1.5 }, 16.0 / 9);
        var t = TimeSpan.Zero;
        var crop = Run(f, new FaceBox(0.5, 0.4, 0.03, 0.05), 6, ref t); // tiny, far-away face
        Assert.True(crop.Height >= (1 / 1.5) - 1e-6);
    }

    [Fact]
    public void Crop_converts_to_zoom_for_any_layer_shape()
    {
        var crop = new FramingCrop(0.5, 0.5, 0.5);
        Assert.Equal(2, crop.ToZoom(1280, 720, 16.0 / 9, 4), 3);
        Assert.Equal(2, crop.ToZoom(1280, 720, 1, 4), 3);   // square layer: same vertical framing
        Assert.Equal(1.5, crop.ToZoom(1280, 720, 1, 1.5), 3); // capped
    }
}

public class AudioLevelMeterTests
{
    private static float[] Tone(double amplitude, int samples = 480 * 2) =>
        [.. Enumerable.Range(0, samples).Select(i => (float)(amplitude * Math.Sin(i * 0.1)))];

    [Fact]
    public void Silence_is_zero_and_loud_is_high()
    {
        var m = new AudioLevelMeter();
        for (var i = 0; i < 50; i++)
        {
            m.Process(new float[960], TimeSpan.FromMilliseconds(10));
        }

        Assert.Equal(0, m.Level);
        Assert.False(m.Speaking);

        for (var i = 0; i < 50; i++)
        {
            m.Process(Tone(0.5), TimeSpan.FromMilliseconds(10));
        }

        Assert.True(m.Level > 0.8);
        Assert.True(m.Speaking);
    }

    [Fact]
    public void Attack_is_fast_and_release_is_slow()
    {
        var m = new AudioLevelMeter();
        m.Process(Tone(0.5), TimeSpan.FromMilliseconds(30));
        var afterAttack = m.Level;
        Assert.True(afterAttack > 0.5, $"Attack after 30 ms: {afterAttack}");

        for (var i = 0; i < 20; i++)
        {
            m.Process(Tone(0.5), TimeSpan.FromMilliseconds(10));
        }

        var loud = m.Level;
        m.Process(new float[960], TimeSpan.FromMilliseconds(30));
        Assert.True(m.Level > loud * 0.8, "Release is slow, so the level doesn't flicker between syllables.");
    }

    [Fact]
    public void Speaking_has_hysteresis_and_hold()
    {
        var m = new AudioLevelMeter { ThresholdDb = -30 };
        for (var i = 0; i < 30; i++)
        {
            m.Process(Tone(0.1), TimeSpan.FromMilliseconds(10)); // about -23 dBFS
        }

        Assert.True(m.Speaking);

        // Slightly below the threshold (within the 6 dB band): stays on.
        for (var i = 0; i < 100; i++)
        {
            m.Process(Tone(0.035), TimeSpan.FromMilliseconds(10)); // about -32 dBFS
        }

        Assert.True(m.Speaking);

        for (var i = 0; i < 200; i++)
        {
            m.Process(new float[960], TimeSpan.FromMilliseconds(10));
        }

        Assert.False(m.Speaking);
    }

    [Fact]
    public void Pcm_bytes_are_read_without_changing_them()
    {
        var m = new AudioLevelMeter();
        var samples = Tone(0.5);
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
        var copy = (byte[])bytes.Clone();
        m.ProcessPcm(bytes, new PcmFormat(48000, 2, PcmSampleFormat.F32));
        Assert.Equal(copy, bytes);
        Assert.True(m.Level > 0);
    }
}

public class LayerEffectTests
{
    private static BgraFrame Gray(int w, int h)
    {
        var f = new BgraFrame(w, h);
        for (var i = 0; i < f.Pixels.Length; i += 4)
        {
            f.Pixels[i] = f.Pixels[i + 1] = f.Pixels[i + 2] = 100;
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
    public void Pulse_is_time_based_and_periodic()
    {
        var fx = new LayerEffectSettings { Animation = BorderAnimation.Pulse, AnimationSpeed = 0 }; // 4 s period
        var a = LayerEffectMath.Evaluate(fx, Argb.White, new FrameContext(TimeSpan.FromSeconds(1), 0, false)).WidthScale;
        var b = LayerEffectMath.Evaluate(fx, Argb.White, new FrameContext(TimeSpan.FromSeconds(5), 0, false)).WidthScale;
        var c = LayerEffectMath.Evaluate(fx, Argb.White, new FrameContext(TimeSpan.FromSeconds(3), 0, false)).WidthScale;
        Assert.Equal(a, b, 6);       // same phase one period later
        Assert.NotEqual(a, c, 3);    // different phase half a period later
    }

    [Fact]
    public void Color_cycle_changes_hue_over_time()
    {
        var fx = new LayerEffectSettings { Animation = BorderAnimation.ColorCycle };
        var c1 = LayerEffectMath.Evaluate(fx, Argb.White, new FrameContext(TimeSpan.Zero, 0, false)).Color;
        var c2 = LayerEffectMath.Evaluate(fx, Argb.White, new FrameContext(TimeSpan.FromSeconds(1), 0, false)).Color;
        Assert.NotEqual(c1, c2);
        Assert.Equal(new Argb(255, 255, 0, 0), LayerEffectMath.FromHue(0));
    }

    [Fact]
    public void Audio_border_pulse_scales_with_level_and_off_does_nothing()
    {
        var on = new LayerEffectSettings { Audio = new() { Enabled = true, Style = AudioReactiveStyle.BorderPulse, Intensity = 1 } };
        Assert.Equal(1, LayerEffectMath.Evaluate(on, Argb.White, new FrameContext(TimeSpan.Zero, 0, false)).WidthScale, 6);
        Assert.True(LayerEffectMath.Evaluate(on, Argb.White, new FrameContext(TimeSpan.Zero, 1, true)).WidthScale > 3);
        Assert.Equal(1, LayerEffectMath.Evaluate(LayerEffectSettings.None, Argb.White, new FrameContext(TimeSpan.Zero, 1, true)).WidthScale, 6);
    }

    [Fact]
    public void Speaking_indicator_changes_colour_only_while_speaking()
    {
        var fx = new LayerEffectSettings { Audio = new() { Enabled = true, Style = AudioReactiveStyle.SpeakingIndicator } };
        Assert.Equal(Argb.White, LayerEffectMath.Evaluate(fx, Argb.White, new FrameContext(TimeSpan.Zero, 0.2f, false)).Color);
        Assert.Equal(fx.Audio.IndicatorColor, LayerEffectMath.Evaluate(fx, Argb.White, new FrameContext(TimeSpan.Zero, 0.8f, true)).Color);
    }

    [Fact]
    public void Glow_draws_a_soft_ring_outside_an_inset_shape()
    {
        var look = CameraLayerSettings.Default with
        {
            Shape = LayerShape.Rectangle,
            BorderColor = new Argb(255, 255, 0, 0),
            Effects = new LayerEffectSettings { Glow = 1 },
        };
        var dst = new byte[100 * 100 * 4];
        new CameraLayerRenderer().Render(Gray(100, 100), look, dst, 100, 100);
        var margin = CameraLayerRenderer.GlowMargin(look.Effects, 100, 100); // 8 px
        Assert.Equal(8, margin, 3);
        Assert.Equal(((byte)100, (byte)100, (byte)100, (byte)255), Px(dst, 100, 50, 50)); // content inside
        var nearShape = Px(dst, 100, 50, 7);   // just outside the shape edge
        var farOut = Px(dst, 100, 50, 1);      // at the layer edge
        Assert.True(nearShape.A > 150 && nearShape.R > 200 && nearShape.B < 50, $"Red glow near the shape: {nearShape}");
        Assert.True(farOut.A < nearShape.A, "Glow fades outward.");
    }

    [Fact]
    public void Framing_crop_overrides_manual_zoom()
    {
        // Left half white, right half black: framing on the right half shows black.
        var src = new BgraFrame(160, 90);
        for (var y = 0; y < 90; y++)
        {
            for (var x = 0; x < 80; x++)
            {
                var o = ((y * 160) + x) * 4;
                src.Pixels[o] = src.Pixels[o + 1] = src.Pixels[o + 2] = 255;
            }
        }

        for (var i = 3; i < src.Pixels.Length; i += 4)
        {
            src.Pixels[i] = 255;
        }

        var look = CameraLayerSettings.Default with { Shape = LayerShape.Rectangle, Framing = new FramingSettings { Enabled = true, MaxZoom = 4 } };
        var dst = new byte[32 * 18 * 4];
        new CameraLayerRenderer().Render(src, look, dst, 32, 18, framing: new FramingCrop(0.8, 0.5, 0.4));
        Assert.Equal(0, Px(dst, 32, 16, 9).R);

        // Framing disabled: the same crop is ignored and the manual (centred, zoom 1) view is used.
        new CameraLayerRenderer().Render(src, look with { Framing = FramingSettings.Off }, dst, 32, 18, framing: new FramingCrop(0.8, 0.5, 0.4));
        Assert.Equal(255, Px(dst, 32, 2, 9).R);
    }
}

public class SceneLayoutTests
{
    [Fact]
    public void Vertical_swaps_canvas_size()
    {
        Assert.Equal((1920, 1080), SceneLayout.DefaultLandscape("a").CanvasSize(1920, 1080));
        Assert.Equal((1080, 1920), SceneLayout.DefaultVertical("a").CanvasSize(1920, 1080));
    }

    [Fact]
    public void Live_switching_only_between_same_orientation_and_base()
    {
        var land = SceneLayout.DefaultLandscape("a");
        var land2 = land with { Id = Guid.NewGuid(), CameraPlacements = new Dictionary<string, NormalizedRect> { ["a"] = new(0.05, 0.05, 0.2, 0.2) } };
        Assert.True(land2.CanSwitchLiveFrom(land));
        Assert.False(SceneLayout.DefaultVertical("a").CanSwitchLiveFrom(land));
        Assert.False((land with { BaseFit = BaseFit.Fill }).CanSwitchLiveFrom(land));
    }

    [Fact]
    public void Placements_are_per_layout()
    {
        var l = SceneLayout.DefaultLandscape("a").WithPlacement("b", new NormalizedRect(0.1, 0.1, 0.2, 0.2));
        Assert.Equal(2, l.CameraPlacements.Count);
        Assert.Single(l.Without("a").CameraPlacements);
    }

    [Fact]
    public void Fit_inside_keeps_the_camera_shape_centred_in_the_slot()
    {
        // Default vertical slot (1080 × 845 px on 1080 × 1920) holding a round camera: an 845 px square, centred.
        var slot = SceneLayout.DefaultVertical("a").CameraPlacements["a"];
        var circle = LayerGeometry.FitInside(slot, 1.0, 1080, 1920);
        var px = LayerGeometry.ToPixels(circle, 1080, 1920);
        Assert.InRange(px.Width - px.Height, -2, 2);
        Assert.InRange(px.X + (px.Width / 2), 538, 542);
        Assert.Equal(slot.Y, circle.Y, 6);

        // A 16:9 camera in the same slot uses the full width and is centred vertically.
        var wide = LayerGeometry.FitInside(slot, 16.0 / 9, 1080, 1920);
        Assert.Equal(1, wide.Width, 6);
        Assert.Equal(slot.Y + ((slot.Height - wide.Height) / 2), wide.Y, 6);

        // A slot that already has the shape is unchanged.
        var land = SceneLayout.DefaultLandscape("a").CameraPlacements["a"];
        Assert.Equal(land, LayerGeometry.FitInside(land, 16.0 / 9, 1920, 1080));
    }
}

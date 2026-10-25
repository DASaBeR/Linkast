// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Linkast.Core.Camera;
using Linkast.Effects;
using Xunit.Abstractions;

namespace Linkast.Effects.Tests;

/// <summary>
/// Real-model tests. Need models/rvm_mobilenetv3_fp32.onnx (tools/fetch-models.ps1). Quality checks also
/// need a 1280x720 BGRA portrait frame at LINKAST_TEST_PORTRAIT (any photo of a person; not shipped).
/// </summary>
public sealed class SegmenterTests(ITestOutputHelper output)
{
    private static string? Portrait => Environment.GetEnvironmentVariable("LINKAST_TEST_PORTRAIT");

    [Fact]
    public void Model_catalog_verifies_hash_and_rejects_tampering()
    {
        string path;
        try
        {
            path = ModelCatalog.Locate(ModelCatalog.Rvm);
        }
        catch (ModelUnavailableException ex)
        {
            output.WriteLine("Model not present: " + ex.Message);
            return;
        }

        Assert.True(ModelCatalog.IsAuthentic(path, ModelCatalog.Rvm));
        var copy = Path.Combine(Path.GetTempPath(), "linkast-tampered.onnx");
        File.Copy(path, copy, overwrite: true);
        using (var fs = File.OpenWrite(copy))
        {
            fs.Seek(1000, SeekOrigin.Begin);
            fs.WriteByte(0x42);
        }

        Assert.False(ModelCatalog.IsAuthentic(copy, ModelCatalog.Rvm));
        File.Delete(copy);
    }

    [SkippableTheory]
    [InlineData(SegmentationQuality.Performance, true)]
    [InlineData(SegmentationQuality.Balanced, true)]
    [InlineData(SegmentationQuality.Quality, true)]
    [InlineData(SegmentationQuality.Performance, false)]
    [InlineData(SegmentationQuality.Balanced, false)]
    [InlineData(SegmentationQuality.Quality, false)]
    public void Segments_a_person_and_reports_speed(SegmentationQuality quality, bool gpu)
    {
        Skip.If(Portrait is null || !File.Exists(Portrait), "Set LINKAST_TEST_PORTRAIT to a 1280x720 BGRA photo of a person.");
        try
        {
            ModelCatalog.Locate(ModelCatalog.Rvm);
        }
        catch (ModelUnavailableException ex)
        {
            Skip.If(true, ex.Message);
        }

        var frame = new BgraFrame(1280, 720);
        File.ReadAllBytes(Portrait!).CopyTo(frame.Pixels, 0);

        var load = Stopwatch.StartNew();
        using var seg = RvmSegmenter.Create(quality, gpu);
        load.Stop();
        var mask = new SegmentationMask(seg.Width, seg.Height);

        // A few frames for the recurrent state to settle, then measure.
        for (var i = 0; i < 5; i++)
        {
            seg.Process(frame, mask);
        }

        var times = new List<double>();
        for (var i = 0; i < 30; i++)
        {
            times.Add(seg.Process(frame, mask).TotalMilliseconds);
        }

        times.Sort();
        output.WriteLine($"{quality} {seg.Provider} {seg.Width}x{seg.Height}: load {load.ElapsedMilliseconds} ms, median {times[15]:0.0} ms, p90 {times[27]:0.0} ms" +
            (seg.GpuFailure is null ? string.Empty : $" (GPU failed: {seg.GpuFailure})"));

        byte At(double nx, double ny) => mask.Alpha[((int)(ny * seg.Height) * seg.Width) + (int)(nx * seg.Width)];
        var face = At(700 / 1280.0, 330 / 720.0);
        var flag = At(120 / 1280.0, 250 / 720.0);
        var wall = At(500 / 1280.0, 100 / 720.0);
        var suit = At(1000 / 1280.0, 450 / 720.0);
        output.WriteLine($"alpha: face {face}, suit {suit}, flag {flag}, wall {wall}");
        Assert.True(face > 200, "Face should be person.");
        Assert.True(suit > 200, "Spacesuit (body) should be person.");
        Assert.True(flag < 40, "Flag (background) should not be person.");
        Assert.True(wall < 40, "Wall (background) should not be person.");

        if (gpu)
        {
            Assert.Equal("GPU (DirectML)", seg.Provider);
        }

        var dump = Path.Combine(Path.GetTempPath(), $"linkast-mask-{quality}-{(gpu ? "gpu" : "cpu")}.gray");
        File.WriteAllBytes(dump, mask.Alpha);
        output.WriteLine($"mask dump: {dump} ({seg.Width}x{seg.Height} gray)");
    }

    [SkippableFact]
    public void Background_modes_composite_correctly_through_the_shared_renderer()
    {
        Skip.If(Portrait is null || !File.Exists(Portrait), "Set LINKAST_TEST_PORTRAIT.");
        var frame = new BgraFrame(1280, 720);
        File.ReadAllBytes(Portrait!).CopyTo(frame.Pixels, 0);
        using var seg = RvmSegmenter.Create(SegmentationQuality.Balanced, preferGpu: true);
        var mask = new SegmentationMask(seg.Width, seg.Height);
        for (var i = 0; i < 4; i++)
        {
            seg.Process(frame, mask);
        }

        const int w = 640, h = 360;
        var green = new BgraFrame(64, 36);
        for (var i = 0; i < green.Pixels.Length; i += 4)
        {
            green.Pixels[i + 1] = 200;
            green.Pixels[i + 3] = 255;
        }

        var baseLook = CameraLayerSettings.Default with { Shape = LayerShape.Rectangle };
        (byte B, byte G, byte R, byte A) Px(byte[] img, int x, int y)
        {
            var o = ((y * w) + x) * 4;
            return (img[o], img[o + 1], img[o + 2], img[o + 3]);
        }

        byte[] RenderMode(BackgroundMode mode, out double ms)
        {
            var dst = new byte[w * h * 4];
            var r = new CameraLayerRenderer();
            var look = baseLook with { Background = new BackgroundSettings { Mode = mode, BlurStrength = 0.8, EdgeSoftness = 0.3 } };
            r.Render(frame, look, dst, w, h, mask, green); // warm caches
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < 20; i++)
            {
                r.Render(frame, look, dst, w, h, mask, green);
            }

            ms = sw.Elapsed.TotalMilliseconds / 20;
            File.WriteAllBytes(Path.Combine(Path.GetTempPath(), $"linkast-bg-{mode}.bgra"), dst);
            return dst;
        }

        var original = RenderMode(BackgroundMode.Original, out var msOrig);
        var blur = RenderMode(BackgroundMode.Blur, out var msBlur);
        var removed = RenderMode(BackgroundMode.Remove, out var msRemove);
        var image = RenderMode(BackgroundMode.Image, out var msImage);
        output.WriteLine($"render {w}x{h}: original {msOrig:0.0} ms, blur {msBlur:0.0} ms, remove {msRemove:0.0} ms, image {msImage:0.0} ms");

        // Face (person) untouched in every mode.
        var face = Px(original, 350, 165);
        Assert.Equal(face, Px(blur, 350, 165));
        Assert.Equal(face, Px(image, 350, 165));
        Assert.Equal(255, Px(removed, 350, 165).A);

        // Background (flag stars area): blurred differs from original, removed is transparent, image is green.
        var bgX = 40;
        var bgY = 60;
        var diff = 0;
        for (var x = bgX; x < bgX + 60; x++)
        {
            diff += Math.Abs(Px(blur, x, bgY).R - Px(original, x, bgY).R);
        }

        Assert.True(diff > 300, $"Background should be visibly blurred (diff {diff}).");
        Assert.Equal(0, Px(removed, bgX, bgY).A);
        Assert.Equal(((byte)0, (byte)200, (byte)0, (byte)255), Px(image, bgX, bgY));
    }
}

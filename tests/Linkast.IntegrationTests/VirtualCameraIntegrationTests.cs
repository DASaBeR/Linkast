// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using Linkast.Core.Camera;
using Linkast.Platform.Windows.Camera;
using Linkast.Platform.Windows.VirtualCamera;
using Xunit.Abstractions;

namespace Linkast.IntegrationTests;

/// <summary>
/// The producer side of the Linkast Camera against a stand-in for the media source (a Local\ section
/// this test creates), plus, opt-in with LINKAST_VCAM_TEST=1 and the camera installed, the real thing
/// read back through Windows' camera stack.
/// </summary>
public sealed class VirtualCameraIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Publishes_only_while_a_client_is_watching_and_releases_when_it_stops()
    {
        var name = @"Local\LinkastVcamTest." + Guid.NewGuid().ToString("N");
        using var section = MemoryMappedFile.CreateNew(name, VirtualCameraProtocol.SectionSize);
        using var view = section.CreateViewAccessor();
        view.Write(VirtualCameraProtocol.VersionOffset, VirtualCameraProtocol.Version);
        view.Write(VirtualCameraProtocol.WidthOffset, (uint)VirtualCameraProtocol.Width);
        view.Write(VirtualCameraProtocol.HeightOffset, (uint)VirtualCameraProtocol.Height);
        view.Write(VirtualCameraProtocol.MagicOffset, VirtualCameraProtocol.Magic);

        using var cameras = new MediaCaptureCameraService();
        await using var output1 = new VirtualCameraOutput(cameras, sectionName: name) { Enabled = true };
        var standby = new byte[VirtualCameraProtocol.FrameBytes];
        Array.Fill(standby, (byte)0x42);
        output1.SetStandbyFrame(standby);

        // No client yet: the producer finds the section but renders nothing.
        await WaitUntilAsync(() => output1.State == VirtualCameraState.Waiting, TimeSpan.FromSeconds(5));
        await Task.Delay(1500);
        Assert.Equal(0, view.ReadInt64(VirtualCameraProtocol.ProducerSequenceOffset));

        // A client starts pulling frames (the media source refreshes ConsumerTime on every request).
        using var cts = new CancellationTokenSource();
        var client = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                view.Write(VirtualCameraProtocol.ConsumerTimeOffset, DateTime.UtcNow.ToFileTimeUtc());
                await Task.Delay(33);
            }
        });

        await WaitUntilAsync(() => output1.State == VirtualCameraState.Serving, TimeSpan.FromSeconds(5));
        var start = view.ReadInt64(VirtualCameraProtocol.ProducerSequenceOffset);
        await Task.Delay(2000);
        var published = view.ReadInt64(VirtualCameraProtocol.ProducerSequenceOffset) - start;
        output.WriteLine($"Published {published} frames in 2 s while watched (no camera layer: standby picture)");
        Assert.InRange(published, 40, 70);
        var slot = VirtualCameraProtocol.SlotOffset(view.ReadInt64(VirtualCameraProtocol.ProducerSequenceOffset));
        Assert.Equal(0x42, view.ReadByte(slot + 1000));
        var age = DateTime.UtcNow - DateTime.FromFileTimeUtc(view.ReadInt64(VirtualCameraProtocol.ProducerTimeOffset));
        Assert.True(age < TimeSpan.FromSeconds(1));

        // The client stops: after the timeout the producer stops rendering.
        await cts.CancelAsync();
        await client;
        await WaitUntilAsync(() => output1.State == VirtualCameraState.Waiting, TimeSpan.FromSeconds(6));
        var idle = view.ReadInt64(VirtualCameraProtocol.ProducerSequenceOffset);
        await Task.Delay(1000);
        Assert.Equal(idle, view.ReadInt64(VirtualCameraProtocol.ProducerSequenceOffset));

        output1.Enabled = false;
        await WaitUntilAsync(() => output1.State == VirtualCameraState.Off, TimeSpan.FromSeconds(3));
    }

    [SkippableFact]
    public async Task Installed_camera_shows_the_placeholder_then_what_Linkast_publishes()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("LINKAST_VCAM_TEST") == "1", "Set LINKAST_VCAM_TEST=1 with the Linkast Camera installed.");
        Skip.IfNot(await VirtualCameraInstaller.IsDeviceListedAsync(), "Linkast Camera is not installed.");

        using var cameras = new MediaCaptureCameraService { IncludeLinkastCamera = true };
        var device = (await cameras.ListAsync()).Single(d => VirtualCameraProtocol.IsLinkastCamera(d.Name));

        var feed = cameras.Open(device, CameraFormatRequest.Auto);
        await using var _ = feed;
        await WaitUntilAsync(() => feed.Frames.PublishedCount > 5, TimeSpan.FromSeconds(20));
        output.WriteLine($"Linkast Camera opened: {feed.ActiveFormat}, {feed.MeasuredFps:0.0} fps");
        var (b, g, r) = Average(feed);
        output.WriteLine($"Without Linkast sending: average BGR {b:0} {g:0} {r:0} (navy placeholder)");
        Assert.True(b > r, "The placeholder is navy blue.");

        // Now Linkast publishes a magenta standby picture through the real Global\ section.
        await using var producer = new VirtualCameraOutput(cameras) { Enabled = true };
        var magenta = new byte[VirtualCameraProtocol.FrameBytes];
        for (var i = 0; i < magenta.Length; i += 4)
        {
            magenta[i] = 255; magenta[i + 1] = 0; magenta[i + 2] = 255; magenta[i + 3] = 255;
        }

        producer.SetStandbyFrame(magenta);
        await WaitUntilAsync(() => producer.State == VirtualCameraState.Serving, TimeSpan.FromSeconds(10));
        await Task.Delay(1500);
        (b, g, r) = Average(feed);
        output.WriteLine($"While Linkast sends magenta: average BGR {b:0} {g:0} {r:0}; frames delivered {producer.FramesDelivered}");
        Assert.True(r > 180 && b > 180 && g < 80, "The camera shows what Linkast publishes.");
    }

    private static (double B, double G, double R) Average(ICameraFeed feed)
    {
        var frame = feed.Frames.TryBeginRead(0)!;
        try
        {
            double b = 0, g = 0, r = 0;
            var n = 0;
            var px = frame.Pixels;
            for (var y = frame.Height / 4; y < frame.Height * 3 / 4; y += 7)
            {
                for (var x = 0; x < frame.Width; x += 11)
                {
                    var o = (y * frame.Stride) + (x * 4);
                    b += px[o]; g += px[o + 1]; r += px[o + 2];
                    n++;
                }
            }

            return (b / n, g / n, r / n);
        }
        finally
        {
            feed.Frames.EndRead(frame);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout)
            {
                throw new Xunit.Sdk.XunitException("Timed out waiting for condition.");
            }

            await Task.Delay(100);
        }
    }
}

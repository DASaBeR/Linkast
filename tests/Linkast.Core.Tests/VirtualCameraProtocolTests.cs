// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Linkast.Core.Camera;

namespace Linkast.Core.Tests;

public class VirtualCameraProtocolTests
{
    private static byte[] Section()
    {
        // What the media source writes when it creates the section.
        var s = new byte[VirtualCameraProtocol.SectionSize];
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(VirtualCameraProtocol.VersionOffset), VirtualCameraProtocol.Version);
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(VirtualCameraProtocol.WidthOffset), VirtualCameraProtocol.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(VirtualCameraProtocol.HeightOffset), VirtualCameraProtocol.Height);
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(VirtualCameraProtocol.MagicOffset), VirtualCameraProtocol.Magic);
        return s;
    }

    private static byte[] Frame(byte value)
    {
        var f = new byte[VirtualCameraProtocol.FrameBytes];
        Array.Fill(f, value);
        return f;
    }

    [Fact]
    public void Layout_matches_the_native_header()
    {
        // Mirrors static_assert(sizeof(Header) == 64) and the field order in VirtualCameraProtocol.h.
        Assert.Equal(32, VirtualCameraProtocol.ProducerSequenceOffset);
        Assert.Equal(56, VirtualCameraProtocol.ConsumerFramesOffset);
        Assert.Equal(4096 + (2 * 1280 * 720 * 4), VirtualCameraProtocol.SectionSize);
        Assert.True(VirtualCameraProtocol.IsCompatible(Section()));

        var wrong = Section();
        BinaryPrimitives.WriteUInt32LittleEndian(wrong.AsSpan(VirtualCameraProtocol.VersionOffset), 99);
        Assert.False(VirtualCameraProtocol.IsCompatible(wrong));
    }

    [Fact]
    public void Publish_alternates_slots_and_moves_the_sequence_last()
    {
        var s = Section();
        var now = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(1, VirtualCameraProtocol.Publish(s, Frame(10), now));
        Assert.Equal(10, s[VirtualCameraProtocol.SlotOffset(1)]);
        Assert.Equal(2, VirtualCameraProtocol.Publish(s, Frame(20), now));
        Assert.Equal(20, s[VirtualCameraProtocol.SlotOffset(2)]);

        // Frame 1's slot is untouched by frame 2 (the reader may still be copying it).
        Assert.Equal(10, s[VirtualCameraProtocol.SlotOffset(1)]);
        Assert.NotEqual(VirtualCameraProtocol.SlotOffset(1), VirtualCameraProtocol.SlotOffset(2));
        Assert.Equal(2, VirtualCameraProtocol.ProducerSequence(s));
        Assert.Equal(now.ToFileTimeUtc(), BinaryPrimitives.ReadInt64LittleEndian(s.AsSpan(VirtualCameraProtocol.ProducerTimeOffset)));
        Assert.Throws<ArgumentException>(() => VirtualCameraProtocol.Publish(s, new byte[10], now));
    }

    [Fact]
    public void A_client_counts_as_watching_for_three_seconds()
    {
        var s = Section();
        var now = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(VirtualCameraProtocol.IsConsumerActive(s, now)); // never requested

        BinaryPrimitives.WriteInt64LittleEndian(s.AsSpan(VirtualCameraProtocol.ConsumerTimeOffset), now.AddSeconds(-1).ToFileTimeUtc());
        Assert.True(VirtualCameraProtocol.IsConsumerActive(s, now));
        Assert.False(VirtualCameraProtocol.IsConsumerActive(s, now.AddSeconds(5)));
    }

    [Fact]
    public void Transparent_pixels_are_flattened_onto_the_background()
    {
        byte[] px = [200, 100, 50, 255, 200, 100, 50, 0, 200, 100, 50, 128];
        VirtualCameraProtocol.FlattenOnto(px, 0, 0, 0);
        Assert.Equal(new byte[] { 200, 100, 50, 255 }, px[..4]);          // opaque unchanged
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, px[4..8]);              // fully transparent = background
        Assert.InRange(px[8], 99, 101);                                   // half blended
        Assert.Equal(255, px[11]);
    }

    [Fact]
    public void Virtual_camera_look_is_a_plain_unmirrored_rectangle_with_the_same_picture()
    {
        var layer = CameraLayerSettings.Default with
        {
            Shape = LayerShape.Circle,
            BorderWidth = 0.1,
            Mirror = true,
            Zoom = 1.5,
            Background = new BackgroundSettings { Mode = BackgroundMode.Blur },
            Effects = new LayerEffectSettings { Animation = BorderAnimation.Pulse, Glow = 0.5 },
        };
        var v = VirtualCameraProtocol.ForVirtualCamera(layer);
        Assert.Equal(LayerShape.Rectangle, v.Shape);
        Assert.Equal(LayerAspect.Wide16x9, v.Aspect);
        Assert.Equal(0, v.BorderWidth);
        Assert.False(v.Mirror);
        Assert.Equal(BorderAnimation.None, v.Effects.Animation);
        Assert.Equal(1.5, v.Zoom);
        Assert.Equal(BackgroundMode.Blur, v.Background.Mode);
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Threading;

namespace Linkast.Core.Camera;

/// <summary>
/// Shared-memory contract with the Linkast Camera media source (native/Linkast.VirtualCamera,
/// VirtualCameraProtocol.h): a 4 KB header then two BGRA frame slots. Linkast writes frames; the
/// media source, loaded by Windows' camera service, reads them. Pure helpers so the protocol can be
/// tested on a plain byte array; change both sides together and bump <see cref="Version"/>.
/// </summary>
public static class VirtualCameraProtocol
{
    public const string FriendlyName = "Linkast Camera";
    public const string Clsid = "{F515DB9C-ADFE-4750-B9B7-9648A2FC0706}";
    public const string SectionName = @"Global\LinkastVirtualCamera.v1";

    public const uint Magic = 0x43564B4C; // "LKVC"
    public const uint Version = 1;
    public const int Width = 1280;
    public const int Height = 720;
    public const int FrameRate = 30;
    public const int HeaderSize = 4096;
    public const int FrameBytes = Width * Height * 4;
    public const int SlotCount = 2;
    public const int SectionSize = HeaderSize + (FrameBytes * SlotCount);

    // Header field offsets (see Header in VirtualCameraProtocol.h; 8-byte packing).
    public const int MagicOffset = 0;
    public const int VersionOffset = 4;
    public const int WidthOffset = 8;
    public const int HeightOffset = 12;
    public const int ProducerSequenceOffset = 32;
    public const int ProducerTimeOffset = 40;
    public const int ConsumerTimeOffset = 48;
    public const int ConsumerFramesOffset = 56;

    /// <summary>A client counts as watching while it requested a frame within this time.</summary>
    public static readonly TimeSpan ConsumerTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// True for the Linkast Camera's device. Windows lists it as "Linkast Camera (Windows Virtual
    /// Camera)", so the name is matched by prefix.
    /// </summary>
    public static bool IsLinkastCamera(string? deviceName) =>
        deviceName is not null && deviceName.StartsWith(FriendlyName, StringComparison.Ordinal);

    /// <summary>True when the header was written by a compatible media source.</summary>
    public static bool IsCompatible(ReadOnlySpan<byte> header) =>
        header.Length >= HeaderSize
        && BinaryPrimitives.ReadUInt32LittleEndian(header[MagicOffset..]) == Magic
        && BinaryPrimitives.ReadUInt32LittleEndian(header[VersionOffset..]) == Version
        && BinaryPrimitives.ReadUInt32LittleEndian(header[WidthOffset..]) == Width
        && BinaryPrimitives.ReadUInt32LittleEndian(header[HeightOffset..]) == Height;

    /// <summary>True while some app (Zoom, Teams, …) is pulling frames from the Linkast Camera.</summary>
    public static bool IsConsumerActive(ReadOnlySpan<byte> header, DateTime nowUtc)
    {
        var consumer = BinaryPrimitives.ReadInt64LittleEndian(header[ConsumerTimeOffset..]);
        return consumer > 0 && nowUtc - DateTime.FromFileTimeUtc(consumer) < ConsumerTimeout;
    }

    public static long ConsumerFrames(ReadOnlySpan<byte> header) => BinaryPrimitives.ReadInt64LittleEndian(header[ConsumerFramesOffset..]);

    public static long ProducerSequence(ReadOnlySpan<byte> header) => BinaryPrimitives.ReadInt64LittleEndian(header[ProducerSequenceOffset..]);

    /// <summary>Byte offset of the slot that frame number <paramref name="sequence"/> lives in.</summary>
    public static int SlotOffset(long sequence) => HeaderSize + (int)(sequence % SlotCount * FrameBytes);

    /// <summary>
    /// Publishes a finished frame: it is written into the slot the reader isn't using (the next
    /// sequence's slot), then the timestamp and sequence are updated, sequence last. The reader copies
    /// the newest slot and checks the sequence didn't move, so it never shows a half-written frame.
    /// </summary>
    public static long Publish(Span<byte> section, ReadOnlySpan<byte> bgra, DateTime nowUtc)
    {
        if (bgra.Length != FrameBytes)
        {
            throw new ArgumentException($"A frame must be {Width} x {Height} BGRA ({FrameBytes} bytes).", nameof(bgra));
        }

        var next = ProducerSequence(section) + 1;
        bgra.CopyTo(section.Slice(SlotOffset(next), FrameBytes));
        BinaryPrimitives.WriteInt64LittleEndian(section[ProducerTimeOffset..], nowUtc.ToFileTimeUtc());
        Interlocked.MemoryBarrier();
        BinaryPrimitives.WriteInt64LittleEndian(section[ProducerSequenceOffset..], next);
        return next;
    }

    /// <summary>
    /// Flattens a straight-alpha BGRA frame onto a solid colour in place (camera apps can't show
    /// transparency, so a removed background becomes this colour).
    /// </summary>
    public static void FlattenOnto(Span<byte> bgra, byte b, byte g, byte r)
    {
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            var a = bgra[i + 3];
            if (a == 255)
            {
                continue;
            }

            var inv = 255 - a;
            bgra[i] = (byte)(((bgra[i] * a) + (b * inv) + 127) / 255);
            bgra[i + 1] = (byte)(((bgra[i + 1] * a) + (g * inv) + 127) / 255);
            bgra[i + 2] = (byte)(((bgra[i + 2] * a) + (r * inv) + 127) / 255);
            bgra[i + 3] = 255;
        }
    }

    /// <summary>
    /// The look used for the virtual camera: the layer's picture (adjustments, background effect,
    /// zoom and auto-framing), as a plain 16:9 rectangle without border, glow or animations, unmirrored
    /// (video-call apps mirror the self-view themselves).
    /// </summary>
    public static CameraLayerSettings ForVirtualCamera(CameraLayerSettings layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        return layer with
        {
            Shape = LayerShape.Rectangle,
            Aspect = LayerAspect.Wide16x9,
            BorderWidth = 0,
            Mirror = false,
            Effects = LayerEffectSettings.None,
        };
    }
}

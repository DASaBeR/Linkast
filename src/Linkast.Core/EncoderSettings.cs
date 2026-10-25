// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core;

public enum RateControl
{
    ConstantBitrate,
    VariableBitrate,
}

/// <summary>Video and audio encoding parameters for one output.</summary>
/// <param name="EncoderId">FFmpeg encoder name, for example <c>h264_nvenc</c> or <c>libx264</c>.</param>
/// <param name="Preset">Encoder-specific preset, or null for the encoder's Linkast default.</param>
public sealed record EncoderSettings(
    string EncoderId,
    int Width,
    int Height,
    int Fps,
    int VideoKbps,
    int KeyframeIntervalSeconds = 2,
    RateControl RateControl = RateControl.ConstantBitrate,
    string? Preset = null,
    int AudioKbps = 160,
    int AudioSampleRate = 48000)
{
    public static EncoderSettings Default1080p60(string encoderId) => new(encoderId, 1920, 1080, 60, 6000);

    public static EncoderSettings Default720p60(string encoderId) => new(encoderId, 1280, 720, 60, 4500);
}

public enum EncoderKind
{
    Hardware,
    Software,
}

/// <summary>An encoder that has been verified to work on this machine.</summary>
public sealed record EncoderInfo(string Id, string DisplayName, EncoderKind Kind, string Vendor)
{
    public override string ToString() => DisplayName;
}

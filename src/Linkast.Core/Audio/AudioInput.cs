// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core.Audio;

public enum PcmSampleFormat
{
    F32,
    S16,
    S24,
    S32,
}

/// <summary>Raw interleaved little-endian PCM layout of an audio pipe.</summary>
public sealed record PcmFormat(int SampleRate, int Channels, PcmSampleFormat SampleFormat)
{
    public int BytesPerSample => SampleFormat switch
    {
        PcmSampleFormat.F32 => 4,
        PcmSampleFormat.S16 => 2,
        PcmSampleFormat.S24 => 3,
        PcmSampleFormat.S32 => 4,
        _ => throw new InvalidOperationException("Unknown sample format."),
    };

    public int BlockAlign => BytesPerSample * Channels;

    public int BytesPerSecond => BlockAlign * SampleRate;
}

/// <summary>A live audio feed that FFmpeg reads from a local named pipe.</summary>
public interface IAudioInput : IAsyncDisposable
{
    AudioSourceSpec Source { get; }

    /// <summary>Full pipe path, for example <c>\\.\pipe\linkast-a1b2</c>.</summary>
    string PipePath { get; }

    PcmFormat Format { get; }

    /// <summary>Completes with an exception if the device fails while streaming.</summary>
    Task Completion { get; }

    /// <summary>Starts device capture and begins serving the pipe. Must be called before FFmpeg launches.</summary>
    Task StartAsync(CancellationToken cancellationToken);
}

/// <summary>Creates audio inputs for device-backed sources. Implemented per platform.</summary>
public interface IAudioInputProvider
{
    bool CanOpen(AudioSourceSpec source);

    IAudioInput Open(AudioSourceSpec source);
}

// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core.Audio;

/// <summary>
/// Decides how much silence to insert into a live PCM stream.
/// WASAPI loopback capture produces no packets while nothing is playing. FFmpeg would then block
/// waiting for audio and stall the whole mux, so the writer pads the gap with silence to keep the
/// stream at wall-clock rate. Padding only starts after <see cref="GapThreshold"/> without data,
/// so ordinary capture jitter is never filled.
/// </summary>
public sealed class SilencePadder
{
    public SilencePadder(int bytesPerSecond, int blockAlign, TimeSpan gapThreshold)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockAlign);
        BytesPerSecond = bytesPerSecond;
        BlockAlign = blockAlign;
        GapThreshold = gapThreshold;
    }

    public int BytesPerSecond { get; }

    public int BlockAlign { get; }

    public TimeSpan GapThreshold { get; }

    /// <summary>Bytes that wall-clock time says should have been written by now.</summary>
    public long ExpectedBytes(TimeSpan elapsed) => Align((long)(elapsed.TotalSeconds * BytesPerSecond));

    /// <summary>
    /// Returns the number of zero bytes to write now (always a multiple of <see cref="BlockAlign"/>).
    /// </summary>
    /// <param name="elapsed">Time since the stream started.</param>
    /// <param name="bytesWritten">Total bytes written so far, real and padded.</param>
    /// <param name="sinceLastData">Time since the capture device last delivered data.</param>
    public int SilenceToWrite(TimeSpan elapsed, long bytesWritten, TimeSpan sinceLastData)
    {
        if (sinceLastData < GapThreshold)
        {
            return 0;
        }

        var deficit = ExpectedBytes(elapsed) - bytesWritten;
        if (deficit <= 0)
        {
            return 0;
        }

        // Never emit more than one second in a single step; the caller loops.
        return (int)Align(Math.Min(deficit, BytesPerSecond));
    }

    /// <summary>
    /// True when the stream is more than <paramref name="maxLead"/> ahead of wall-clock time, so the
    /// next chunk should be dropped. A device clock running slightly fast would otherwise make
    /// audio drift ahead of video over a long stream.
    /// </summary>
    public bool IsAhead(TimeSpan elapsed, long bytesWritten, TimeSpan maxLead) =>
        bytesWritten - ExpectedBytes(elapsed) > (long)(maxLead.TotalSeconds * BytesPerSecond);

    private long Align(long bytes) => bytes - (bytes % BlockAlign);
}

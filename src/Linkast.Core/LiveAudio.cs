// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core;

/// <summary>The audio inputs that can be changed while live.</summary>
public enum AudioChannel
{
    System,
    Microphone,
}

/// <summary>
/// Changes the audio of a running stream without restarting it (mute, volume, noise removal). Changes
/// also carry over to automatic reconnects of the same stream.
/// </summary>
public interface ILiveAudioControl
{
    /// <summary>Sets the linear gain of a channel (0 = muted, 1 = unchanged). False when nothing is live.</summary>
    Task<bool> SetGainAsync(AudioChannel channel, double gain, CancellationToken cancellationToken = default);

    /// <summary>Switches microphone noise removal. False when nothing is live or there is no microphone.</summary>
    Task<bool> SetNoiseSuppressionAsync(bool enabled, CancellationToken cancellationToken = default);
}

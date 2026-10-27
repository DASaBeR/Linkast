// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core;

/// <summary>
/// Names of the mixer channels that can be changed while live. A channel name becomes part of an FFmpeg
/// filter name, so it is lower-case letters, digits and '_' only.
/// </summary>
public static class AudioChannels
{
    /// <summary>Desktop (system) audio.</summary>
    public const string System = "sys";

    public const string Microphone = "mic";

    /// <summary>The captured game or app on its own.</summary>
    public const string Game = "game";

    /// <summary>The channel of a web overlay, from its layer ID (for example "overlay-2" gives "web_overlay_2").</summary>
    public static string ForOverlay(string layerId) => "web_" + Clean(layerId);

    /// <summary>The channel of a scene's video, from the scene's ID.</summary>
    public static string ForSceneMedia(Guid sceneId) => "scene_" + sceneId.ToString("N")[..12];

    private static string Clean(string id) =>
        new([.. id.ToLowerInvariant().Select(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') ? c : '_').Take(32)]);
}

/// <summary>
/// Changes the audio of a running stream without restarting it (mute, volume, noise removal). Changes
/// also carry over to automatic reconnects of the same stream.
/// </summary>
public interface ILiveAudioControl
{
    /// <summary>Sets the linear gain of a channel (0 = muted, 1 = unchanged). False when nothing is live or the channel isn't in the stream.</summary>
    Task<bool> SetGainAsync(string channel, double gain, CancellationToken cancellationToken = default);

    /// <summary>Switches microphone noise removal. False when nothing is live or there is no microphone.</summary>
    Task<bool> SetNoiseSuppressionAsync(bool enabled, CancellationToken cancellationToken = default);
}

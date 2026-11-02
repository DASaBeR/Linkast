// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core;

/// <summary>Where the stream goes. A preset fills in the server so the user only pastes a stream key.</summary>
public enum StreamPlatform
{
    Custom,
    Twitch,
    YouTube,
    Kick,
    Facebook,
}

/// <param name="Server">The platform's global ingest server.</param>
/// <param name="KeyPage">The page that shows the stream key, opened by "Where do I find my key?".</param>
public sealed record StreamPlatformInfo(StreamPlatform Platform, string Name, string? Server, string? KeyPage)
{
    // Shown in lists and read by screen readers.
    public override string ToString() => Name;
}

public static class StreamPlatforms
{
    public static IReadOnlyList<StreamPlatformInfo> All { get; } =
    [
        new(StreamPlatform.Twitch, "Twitch", "rtmp://live.twitch.tv/app", "https://dashboard.twitch.tv/settings/stream"),
        new(StreamPlatform.YouTube, "YouTube", "rtmps://a.rtmps.youtube.com/live2", "https://studio.youtube.com/channel/UC/livestreaming"),
        new(StreamPlatform.Kick, "Kick", "rtmps://fa723fc1b171.global-contribute.live-video.net/app", "https://kick.com/dashboard/settings/stream"),
        new(StreamPlatform.Facebook, "Facebook", "rtmps://live-api-s.facebook.com:443/rtmp/", "https://www.facebook.com/live/producer"),
        new(StreamPlatform.Custom, "Custom", null, null),
    ];

    public static StreamPlatformInfo Get(StreamPlatform platform) => All.First(p => p.Platform == platform);

    /// <summary>
    /// Trims the server URL and adds the application path some services leave out. Kick shows its server as
    /// <c>rtmps://xxxx.global-contribute.live-video.net</c> (Amazon IVS), whose application is always <c>/app</c>.
    /// </summary>
    public static string Normalize(string? serverUrl)
    {
        var url = (serverUrl ?? string.Empty).Trim();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme is "rtmp" or "rtmps"
            && uri.AbsolutePath.Trim('/').Length == 0
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && uri.IdnHost.EndsWith(".live-video.net", StringComparison.OrdinalIgnoreCase))
        {
            return url.TrimEnd('/') + "/app";
        }

        return url;
    }

    /// <summary>The preset whose server is <paramref name="serverUrl"/>, or <see cref="StreamPlatform.Custom"/>.</summary>
    public static StreamPlatform Detect(string? serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return StreamPlatform.Custom;
        }

        var url = Normalize(serverUrl).TrimEnd('/');
        return All.FirstOrDefault(p => p.Server is { } s && string.Equals(s.TrimEnd('/'), url, StringComparison.OrdinalIgnoreCase))?.Platform
            ?? StreamPlatform.Custom;
    }
}

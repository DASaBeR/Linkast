// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core;

/// <summary>How fast one encoder ran in the speed test.</summary>
/// <param name="FramesPerSecond">Frames of 1080p it encoded per second (60 fps is real time for 1080p60).</param>
public sealed record EncoderSpeed(string EncoderId, bool IsHardware, double FramesPerSecond);

/// <summary>A suggested setup with the reason in plain words.</summary>
public sealed record QualityRecommendation(string EncoderId, int Width, int Height, int Fps, int VideoBitrateKbps, string Reason);

/// <summary>
/// Picks resolution, frame rate, bitrate and encoder from the measured upload speed and encoder speed.
/// Follows the platforms' own guidance: about 6000 kbps for 1080p60 on Twitch, 4500 for 720p60,
/// 3000 for 720p30, and never more than three quarters of the upload, so the connection keeps headroom.
/// </summary>
public static class QualityAdvisor
{
    /// <summary>Share of the measured upload the stream may use.</summary>
    public const double UploadShare = 0.75;

    /// <summary>An encoder must run this much faster than real time to be safe during a game.</summary>
    public const double SpeedMargin = 1.25;

    /// <summary>A software encoder shares the processor with the game, so it needs twice the margin.</summary>
    public const double SoftwareSpeedMargin = 2.5;

    /// <summary>The highest bitrate each platform accepts or recommends for 1080p60 H.264.</summary>
    public static int MaxBitrateKbps(StreamPlatform platform) => platform switch
    {
        StreamPlatform.Twitch => 6000,
        StreamPlatform.YouTube => 12000,
        StreamPlatform.Kick => 8000,
        StreamPlatform.Facebook => 9000,
        _ => 8000,
    };

    public static QualityRecommendation? Recommend(StreamPlatform platform, int uploadKbps, IReadOnlyList<EncoderSpeed> encoders)
    {
        ArgumentNullException.ThrowIfNull(encoders);
        if (encoders.Count == 0 || uploadKbps <= 0)
        {
            return null;
        }

        // Hardware encoders leave the CPU to the game. Among those fast enough for 1080p60 the one with the
        // better picture wins (NVENC, AMF, QSV, then Media Foundation), not simply the fastest.
        var best = encoders
            .OrderByDescending(e => e.IsHardware && e.FramesPerSecond >= 60 * SpeedMargin)
            .ThenBy(e => QualityRank(e.EncoderId))
            .ThenByDescending(e => e.FramesPerSecond)
            .First();

        var budget = (int)Math.Min(MaxBitrateKbps(platform), uploadKbps * UploadShare);
        var (w, h, fps, ideal) = budget switch
        {
            >= 5500 => (1920, 1080, 60, MaxBitrateKbps(platform)),
            >= 4000 => (1280, 720, 60, 4500),
            >= 2500 => (1280, 720, 30, 3000),
            _ => (854, 480, 30, 1500),
        };

        // The test encodes 1080p; fewer pixels or frames run proportionally faster.
        var margin = best.IsHardware ? SpeedMargin : SoftwareSpeedMargin;
        double Needed(int width, int height, int f) => f * margin * width * height / (1920.0 * 1080);
        var lowered = false;
        while (best.FramesPerSecond < Needed(w, h, fps) && (w, h, fps) != (854, 480, 30))
        {
            lowered = true;
            (w, h, fps, ideal) = (w, h, fps) switch
            {
                (1920, 1080, 60) => (1920, 1080, 30, 4500),
                (1920, 1080, 30) => (1280, 720, 60, 4500),
                (1280, 720, 60) => (1280, 720, 30, 3000),
                _ => (854, 480, 30, 1500),
            };
        }

        var bitrate = Math.Max(800, Math.Min(budget, ideal) / 100 * 100);
        var reason = lowered
            ? $"Your upload allows about {budget} kbps, but the {(best.IsHardware ? "encoder" : "processor")} can't keep up with more than {w} × {h} at {fps} FPS."
            : $"Uses up to {Math.Round(UploadShare * 100)} % of your upload ({uploadKbps / 1000.0:0.#} Mbps) and stays within {LimitName(platform)}.";
        return new QualityRecommendation(best.EncoderId, w, h, fps, bitrate, reason);
    }

    private static int QualityRank(string encoderId) => encoderId switch
    {
        "h264_nvenc" => 0,
        "h264_amf" => 1,
        "h264_qsv" => 2,
        "libx264" => 3,
        _ => 4,
    };

    private static string LimitName(StreamPlatform platform) =>
        platform == StreamPlatform.Custom ? "common platform limits" : StreamPlatforms.Get(platform).Name + "'s limit";
}

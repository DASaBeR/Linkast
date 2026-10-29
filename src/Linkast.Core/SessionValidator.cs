// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace Linkast.Core;

/// <summary>Validates user-controlled values before anything reaches a process argument.</summary>
public static partial class SessionValidator
{
    /// <summary>Most sounds (pipes FFmpeg mixes) in one stream.</summary>
    public const int MaxAudioSources = 16;

    public const int MinWidth = 160;
    public const int MaxWidth = 7680;
    public const int MinHeight = 90;
    public const int MaxHeight = 4320;

    public static IReadOnlyList<StreamError> Validate(StreamSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var errors = new List<StreamError>();
        ValidateEncoder(session.Encoder, errors);
        if (session.Broadcast)
        {
            ValidateDestination(session.Destination, errors);
        }

        ValidateRecording(session, errors);
        ValidateSources(session, errors);
        return errors;
    }

    public static void ValidateEncoder(EncoderSettings e, List<StreamError> errors)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(errors);

        if (!EncoderIdPattern().IsMatch(e.EncoderId))
        {
            errors.Add(Config("encoder.id", "The selected encoder name is not valid."));
        }

        if (e.Width is < MinWidth or > MaxWidth || e.Height is < MinHeight or > MaxHeight)
        {
            errors.Add(Config("encoder.resolution", $"Resolution must be between {MinWidth}x{MinHeight} and {MaxWidth}x{MaxHeight}."));
        }

        if (e.Width % 2 != 0 || e.Height % 2 != 0)
        {
            errors.Add(Config("encoder.resolution.even", "Width and height must be even numbers."));
        }

        if (e.Fps is < 1 or > 240)
        {
            errors.Add(Config("encoder.fps", "Frame rate must be between 1 and 240."));
        }

        if (e.VideoKbps is < 200 or > 100_000)
        {
            errors.Add(Config("encoder.video_bitrate", "Video bitrate must be between 200 and 100000 kbps."));
        }

        if (e.KeyframeIntervalSeconds is < 1 or > 10)
        {
            errors.Add(Config("encoder.keyframe", "Keyframe interval must be between 1 and 10 seconds."));
        }

        if (e.AudioKbps is < 32 or > 512)
        {
            errors.Add(Config("encoder.audio_bitrate", "Audio bitrate must be between 32 and 512 kbps."));
        }

        if (e.AudioSampleRate is not (44100 or 48000))
        {
            errors.Add(Config("encoder.sample_rate", "Audio sample rate must be 44100 or 48000 Hz."));
        }

        if (e.Preset is not null && !PresetPattern().IsMatch(e.Preset))
        {
            errors.Add(Config("encoder.preset", "The encoder preset contains unsupported characters."));
        }
    }

    public static void ValidateDestination(Destination d, List<StreamError> errors)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(errors);

        var url = d.ServerUrl;
        if (!url.IsAbsoluteUri || (url.Scheme != "rtmp" && url.Scheme != "rtmps"))
        {
            errors.Add(Config("destination.scheme", "The server URL must start with rtmp:// or rtmps://."));
            return;
        }

        if (string.IsNullOrWhiteSpace(url.Host))
        {
            errors.Add(Config("destination.host", "The server URL has no host name."));
        }

        if (!string.IsNullOrEmpty(url.UserInfo))
        {
            errors.Add(Config("destination.userinfo", "Put credentials in the stream key field, not in the server URL."));
        }

        if (!string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
        {
            errors.Add(Config("destination.query", "The server URL must not contain '?' or '#'. Put the full key in the stream key field."));
        }

        if (url.AbsolutePath.Trim('/').Length == 0)
        {
            errors.Add(Config("destination.app", "The server URL needs an application path, for example rtmp://host/live."));
        }

        var key = d.StreamKey.Reveal();
        if (key.Length == 0)
        {
            errors.Add(Config("destination.key.empty", "Enter a stream key."));
        }
        else if (key.Length > 512 || key.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            errors.Add(Config("destination.key.invalid", "The stream key contains spaces or unsupported characters."));
        }
    }

    private static void ValidateRecording(StreamSession s, List<StreamError> errors)
    {
        if (s.Recording is null)
        {
            if (!s.Broadcast)
            {
                errors.Add(Config("recording.none", "Nothing to do: the session neither streams nor records."));
            }

            return;
        }

        var folder = s.Recording.Folder;
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder) || folder.StartsWith('-')
            || folder.Any(char.IsControl) || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            errors.Add(new StreamError(ErrorCategory.Configuration, "recording.folder", "Choose a folder for recordings."));
        }
    }

    private static void ValidateSources(StreamSession s, List<StreamError> errors)
    {
        switch (s.Video)
        {
            case DisplaySource { MonitorIndex: < 0 or > 32 }:
                errors.Add(new StreamError(ErrorCategory.Capture, "capture.monitor", "The selected monitor is not valid."));
                break;
            case WindowSource { WindowHandle: <= 0 }:
                errors.Add(new StreamError(ErrorCategory.Capture, "capture.window", "The selected window is not valid."));
                break;
        }

        // Desktop, microphone, a game per scene (up to four) and every alert box each have their own sound.
        // Four, the old limit, stopped a stream with the game on its own fader and two alert boxes.
        if (s.Audio.Count > MaxAudioSources)
        {
            errors.Add(new StreamError(ErrorCategory.Audio, "audio.count", $"At most {MaxAudioSources} sounds can go into a stream. Remove an alert box or turn off a sound on the Audio tab."));
        }

        foreach (var a in s.Audio)
        {
            if (a.Gain is < 0 or > 4 || double.IsNaN(a.Gain))
            {
                errors.Add(new StreamError(ErrorCategory.Audio, "audio.gain", "Audio gain must be between 0 and 4."));
            }
        }
    }

    private static StreamError Config(string code, string message) => new(ErrorCategory.Configuration, code, message);

    [GeneratedRegex("^[a-z0-9_]{2,32}$")]
    private static partial Regex EncoderIdPattern();

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,32}$")]
    private static partial Regex PresetPattern();
}

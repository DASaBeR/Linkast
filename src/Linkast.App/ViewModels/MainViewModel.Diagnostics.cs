// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.App.Services;
using Linkast.Core;
using Linkast.Platform.Windows.Capture;
using Microsoft.Extensions.Logging;

namespace Linkast.App.ViewModels;

/// <summary>
/// What goes into the log file for a tester's report beyond the engine's own lines: everything the Activity
/// list shows, what a stream captures when it starts (the game window, its size, fullscreen or not, and its
/// monitor's refresh rate) and, while live, how busy the PC is once a minute.
/// </summary>
public sealed partial class MainViewModel
{
    private PerformanceMonitor? _performance;
    private StreamStatsLogger? _stats;

    /// <summary>Numbers for the log: bitrate, backlog, frame rate, reconnects, and whether the captured game still responds.</summary>
    private StreamStatsLogger Stats => _stats ??= new StreamStatsLogger(_loggerFactory.CreateLogger<StreamStatsLogger>()) { Extra = DescribeStreamExtra };

    /// <summary>The captured game's state, and how well sound and picture keep to the same clock.</summary>
    private string? DescribeStreamExtra()
    {
        var game = DescribeGame();
        var sync = _engine?.SyncInfo();
        return game is null ? sync : sync is null ? game : game + "; " + sync;
    }

    private string? DescribeGame()
    {
        if (SelectedSourceKind?.Kind != VideoSourceKind.Window || SelectedWindow is not { } w)
        {
            return null;
        }

        var name = w.ProcessName ?? w.Title;
        return !VideoSourceCatalog.WindowExists(w.Handle)
            ? $"game window {name} is gone"
            : VideoSourceCatalog.IsWindowHung(w.Handle)
                ? $"game {name} is NOT RESPONDING"
                : $"game {name} responding{(VideoSourceCatalog.IsWindowMinimized(w.Handle) ? ", minimized" : string.Empty)}";
    }

    private string StreamSettingsLine() =>
        $"network {(BuildProxy() is { } proxy ? proxy.Label : SelectedNetwork.IsAutomatic ? "Windows default" : SelectedNetwork.Name)}; " +
        $"video {SelectedResolution.Width}x{SelectedResolution.Height}@{SelectedFrameRate} {VideoBitrateKbps} kbps, keyframe every {KeyframeIntervalSeconds} s, preset {EncoderPreset ?? "default"}; " +
        $"audio {AudioBitrateKbps} kbps, desktop {SystemAudioVolume:0}%, microphone {(MicrophoneEnabled ? MicrophoneVolume.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%" : "off")}, noise suppression {MicrophoneNoiseSuppression}; " +
        $"record {RecordWhenLive}";

    /// <summary>Logs what each capture of a starting stream is: the window, its state and its monitor.</summary>
    private void LogStreamContext(StreamSession session)
    {
        IReadOnlyList<CaptureBinding> captures = session.Captures.Count > 0 ? session.Captures
            : [new CaptureBinding("capture", session.Video, session.BasePlacement, session.BaseCrop, session.BaseFit, !session.ShowCapture)];
        foreach (var c in captures)
        {
            var shown = c.StartHidden ? " (another scene's)" : string.Empty;
            switch (c.Source)
            {
                case WindowSource w:
                    var seen = FullscreenDetector.Inspect(w.WindowHandle, out var minimized);
                    var state = minimized ? "minimized" : seen.Kind switch
                    {
                        Linkast.Core.FullscreenKind.Exclusive => "exclusive fullscreen",
                        Linkast.Core.FullscreenKind.Fullscreen => "borderless fullscreen",
                        _ => "in a window",
                    };
                    LogGoingLive("Captures game window" + shown + " " + SystemReport.DescribeWindow(w.WindowHandle, w.ProcessName ?? w.Title, state));
                    break;
                case DisplaySource d:
                    LogGoingLive($"Captures display {d.MonitorIndex + 1}{shown} with {d.Api}");
                    break;
                case TestPatternSource:
                    LogGoingLive("Captures the test pattern" + shown);
                    break;
            }
        }

        foreach (var scene in StreamScenes(Webcam.ActiveLayout))
        {
            var cameras = string.Join(", ", scene.CameraPlacements.Select(p => $"{p.Key} {p.Value}"));
            var stack = scene.StackOrder is not null ? string.Join(", ", scene.StackOrder) : string.Empty;
            LogGoingLive($"{scene.Name}: cameras [{cameras}], stack order [{stack}]");
        }
    }

    /// <summary>Samples the PC's load once a minute while live; stops when the stream ends.</summary>
    private void FollowPerformance(StreamState state)
    {
        if (state == StreamState.Live)
        {
            _performance ??= new PerformanceMonitor(_loggerFactory.CreateLogger<PerformanceMonitor>(), () => _engine?.FfmpegProcessId ?? 0);
            _performance.Start();
        }
        else if (!StreamStateMachine.IsActive(state))
        {
            _performance?.Stop();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Activity: {Line}")]
    private partial void LogActivity(string line);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stream settings: {Details}")]
    private partial void LogStreamSettings(string details);

    [LoggerMessage(Level = LogLevel.Information, Message = "Going live: {Details}")]
    private partial void LogGoingLive(string details);
}

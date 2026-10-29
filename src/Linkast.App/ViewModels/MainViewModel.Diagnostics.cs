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

    [LoggerMessage(Level = LogLevel.Information, Message = "Going live: {Details}")]
    private partial void LogGoingLive(string details);
}

// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Linkast.Core;
using Microsoft.Extensions.Logging;

namespace Linkast.App.Services;

/// <summary>
/// Writes the numbers a tester's log needs to explain a stream: every 30 seconds one line with the encoder's
/// real output bitrate, what was actually sent (lowest, average and highest), the unsent backlog, frame rate,
/// dropped frames and reconnects, a line for every state change with its reason, and a summary when the
/// stream ends. Telemetry arrives about once a second; time comes from the telemetry itself, not a clock.
/// </summary>
public sealed partial class StreamStatsLogger(ILogger<StreamStatsLogger> logger)
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly ILogger _logger = logger;

    private Window _window = new();
    private Window _total = new();
    private EncoderProgress? _previousEncoder;
    private TimeSpan _windowStart;
    private int _reconnectsAtWindowStart;
    private int _reconnects;
    private TimeSpan _lastSeen;
    private bool _active;

    /// <summary>Extra text for each stats line, for example whether the captured game still responds. May return null.</summary>
    public Func<string?>? Extra { get; set; }

    /// <summary>A new stream starts: forget the previous one.</summary>
    public void Begin()
    {
        _window = new Window();
        _total = new Window();
        _previousEncoder = null;
        _windowStart = TimeSpan.Zero;
        _reconnectsAtWindowStart = 0;
        _reconnects = 0;
        _lastSeen = TimeSpan.Zero;
        _active = true;
    }

    /// <summary>One line for each state change, with the reason when there is one.</summary>
    public void StateChanged(StreamStateChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var reason = e.Error is null ? string.Empty : $" Reason: {e.Error.Category}/{e.Error.Code}: {e.Error.UserMessage}{(string.IsNullOrEmpty(e.Error.Detail) ? string.Empty : " (" + Short(e.Error.Detail) + ")")}";
        LogLine($"Stream state: {e.Previous} -> {e.Current}.{reason}");
        if (e.Current is StreamState.Stopped or StreamState.Failed && _active)
        {
            _active = false;
            LogLine(Summary());
        }
    }

    public void Observe(StreamTelemetry t)
    {
        ArgumentNullException.ThrowIfNull(t);
        if (!_active)
        {
            Begin();
        }

        _reconnects = t.ReconnectCount;
        _lastSeen = t.Uptime;

        if (t.State == StreamState.Live)
        {
            if (t.Network is { Connected: true } n)
            {
                var kbps = n.OutgoingKbps;
                _window.AddSent(kbps);
                _total.AddSent(kbps);
                _window.NoteBacklog(n.BufferedBytes);
                _total.NoteBacklog(n.BufferedBytes);
                _window.BytesSent = n.BytesSent;
                _total.BytesSent = n.BytesSent;
            }

            if (t.Encoder is { } e)
            {
                if (_previousEncoder is { } p && e.OutTime > p.OutTime && e.TotalSizeBytes >= p.TotalSizeBytes)
                {
                    var kbps = (e.TotalSizeBytes - p.TotalSizeBytes) * 8.0 / 1000.0 / (e.OutTime - p.OutTime).TotalSeconds;
                    _window.AddEncoder(kbps);
                    _total.AddEncoder(kbps);
                }

                if (e.Fps > 0)
                {
                    _window.AddFps(e.Fps);
                    _total.AddFps(e.Fps);
                }

                _window.Dropped = e.DroppedFrames;
                _window.Duplicated = e.DuplicatedFrames;
                _total.Dropped = e.DroppedFrames;
                _total.Duplicated = e.DuplicatedFrames;
                _previousEncoder = e;
            }
        }

        if (t.Uptime - _windowStart >= Interval)
        {
            LogLine(Describe(t));
            _windowStart = t.Uptime;
            _reconnectsAtWindowStart = t.ReconnectCount;
            _window = new Window { DroppedAtStart = _window.Dropped, DuplicatedAtStart = _window.Duplicated, Dropped = _window.Dropped, Duplicated = _window.Duplicated };
        }
    }

    internal string Describe(StreamTelemetry t)
    {
        var w = _window;
        var parts = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"Stream stats ({Interval.TotalSeconds:0} s): {t.State}, up {t.Uptime:hh\\:mm\\:ss}, connected {t.ConnectionUptime:hh\\:mm\\:ss}, reconnects {t.ReconnectCount} (+{t.ReconnectCount - _reconnectsAtWindowStart})"),
        };

        if (w.EncoderSamples > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"encoder output {w.EncoderAvg:0} kbps (min {w.EncoderMin:0}, max {w.EncoderMax:0})"));
        }

        if (w.FpsSamples > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{w.FpsAvg:0.0}/{t.TargetFps} fps (min {w.FpsMin:0.0}), speed {(t.Encoder?.Speed ?? 0):0.00}x, dropped +{w.Dropped - w.DroppedAtStart}, duplicated +{w.Duplicated - w.DuplicatedAtStart}"));
        }

        if (w.SentSamples > 0)
        {
            var seconds = w.EncoderAvg > 0 ? w.BacklogMaxBytes * 8.0 / 1000.0 / w.EncoderAvg : 0;
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"sent {w.SentAvg:0} kbps (min {w.SentMin:0}, max {w.SentMax:0}), backlog now {(t.Network?.BufferedBytes ?? 0) / 1048576.0:0.0} MB (max {w.BacklogMaxBytes / 1048576.0:0.0} MB, about {seconds:0} s), total sent {w.BytesSent / 1048576.0:0.0} MB"));
        }

        if (t.Network is { } n)
        {
            parts.Add($"to {n.RemoteEndpoint ?? "?"} from {n.LocalEndpoint ?? "?"}{(string.IsNullOrEmpty(n.InterfaceName) ? string.Empty : " via " + n.InterfaceName)}, connected {n.Connected}");
        }

        if (t.CaptureLooksBlack)
        {
            parts.Add("capture looks black");
        }

        if (t.LastError is { } err)
        {
            parts.Add($"last error {err.Category}/{err.Code}");
        }

        try
        {
            if (Extra?.Invoke() is { Length: > 0 } extra)
            {
                parts.Add(extra);
            }
        }
#pragma warning disable CA1031 // Diagnostics must never disturb a stream.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            parts.Add("extra info failed: " + ex.GetType().Name);
        }

        return string.Join("; ", parts);
    }

    internal string Summary()
    {
        var w = _total;
        var parts = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"Stream summary: {_lastSeen:hh\\:mm\\:ss} long, reconnects {_reconnects}, sent {w.BytesSent / 1048576.0:0.0} MB"),
        };

        if (w.SentSamples > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"sent average {w.SentAvg:0} kbps (min {w.SentMin:0}, max {w.SentMax:0})"));
        }

        if (w.EncoderSamples > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"encoder average {w.EncoderAvg:0} kbps (min {w.EncoderMin:0}, max {w.EncoderMax:0})"));
        }

        if (w.FpsSamples > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"fps average {w.FpsAvg:0.0} (min {w.FpsMin:0.0})"));
        }

        parts.Add(string.Create(CultureInfo.InvariantCulture, $"dropped {w.Dropped}, duplicated {w.Duplicated}, largest backlog {w.BacklogMaxBytes / 1048576.0:0.0} MB"));
        return string.Join("; ", parts);
    }

    private static string Short(string text) => text.Length <= 400 ? text : text[..400] + "…";

    private void LogLine(string line) => LogStats(line);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Line}")]
    private partial void LogStats(string line);

    private sealed class Window
    {
        public int SentSamples { get; private set; }

        public double SentMin { get; private set; } = double.MaxValue;

        public double SentMax { get; private set; }

        private double _sentSum;

        public double SentAvg => SentSamples == 0 ? 0 : _sentSum / SentSamples;

        public int EncoderSamples { get; private set; }

        public double EncoderMin { get; private set; } = double.MaxValue;

        public double EncoderMax { get; private set; }

        private double _encoderSum;

        public double EncoderAvg => EncoderSamples == 0 ? 0 : _encoderSum / EncoderSamples;

        public int FpsSamples { get; private set; }

        public double FpsMin { get; private set; } = double.MaxValue;

        private double _fpsSum;

        public double FpsAvg => FpsSamples == 0 ? 0 : _fpsSum / FpsSamples;

        public long BacklogMaxBytes { get; private set; }

        public long BytesSent { get; set; }

        public long Dropped { get; set; }

        public long Duplicated { get; set; }

        public long DroppedAtStart { get; init; }

        public long DuplicatedAtStart { get; init; }

        public void AddSent(double kbps)
        {
            SentSamples++;
            _sentSum += kbps;
            SentMin = Math.Min(SentMin, kbps);
            SentMax = Math.Max(SentMax, kbps);
        }

        public void AddEncoder(double kbps)
        {
            EncoderSamples++;
            _encoderSum += kbps;
            EncoderMin = Math.Min(EncoderMin, kbps);
            EncoderMax = Math.Max(EncoderMax, kbps);
        }

        public void AddFps(double fps)
        {
            FpsSamples++;
            _fpsSum += fps;
            FpsMin = Math.Min(FpsMin, fps);
        }

        public void NoteBacklog(long bytes) => BacklogMaxBytes = Math.Max(BacklogMaxBytes, bytes);
    }
}

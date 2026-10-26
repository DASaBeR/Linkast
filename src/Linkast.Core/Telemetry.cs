// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core;

/// <summary>Broad failure domains, so the UI can tell capture, encoder and network problems apart.</summary>
public enum ErrorCategory
{
    Configuration,
    Capture,
    Audio,
    Encoder,
    Network,
    Process,
    Unknown,
}

/// <summary>A classified, user-presentable error. <see cref="Detail"/> must already be redacted.</summary>
public sealed record StreamError(ErrorCategory Category, string Code, string UserMessage, string? Detail = null)
{
    /// <summary>Desktop Duplication lost access to the display, for example when a game changed display mode.</summary>
    public const string CaptureLostCode = "capture.dda.lost";

    /// <summary>
    /// Network failures are retried by the reconnect policy, and a lost Desktop Duplication by the
    /// capture restart policy; others need user action.
    /// </summary>
    public bool IsRetryable => Category == ErrorCategory.Network || Code == CaptureLostCode;

    public override string ToString() => $"[{Category}/{Code}] {UserMessage}";
}

/// <summary>Snapshot of encoder progress parsed from FFmpeg.</summary>
public sealed record EncoderProgress(
    long Frame,
    double Fps,
    double BitrateKbps,
    long TotalSizeBytes,
    TimeSpan OutTime,
    long DuplicatedFrames,
    long DroppedFrames,
    double Speed,
    bool IsEnd);

/// <summary>Snapshot of the outgoing connection measured on the actual socket.</summary>
public sealed record NetworkSnapshot(
    long BytesSent,
    long BytesReceived,
    double OutgoingKbps,
    string? LocalEndpoint,
    string? RemoteEndpoint,
    string? InterfaceName,
    bool Connected);

/// <summary>Combined health snapshot published to the UI roughly once per second.</summary>
public sealed record StreamTelemetry(
    StreamState State,
    TimeSpan Uptime,
    TimeSpan ConnectionUptime,
    int TargetFps,
    EncoderProgress? Encoder,
    NetworkSnapshot? Network,
    int ReconnectCount,
    StreamError? LastError)
{
    /// <summary>A captured window has been fully black for a few seconds (see the engine's black probe).</summary>
    public bool CaptureLooksBlack { get; init; }

    /// <summary>Encoder is keeping up when encoded FPS is within 10 % of target and speed is near real time.</summary>
    public bool EncoderOverloaded => Encoder is { Fps: > 0 } e && (e.Fps < TargetFps * 0.9 || e.Speed is > 0 and < 0.95);
}

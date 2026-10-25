// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core;

/// <summary>Windows capture API used for a display or window source.</summary>
public enum CaptureApi
{
    /// <summary>Windows Graphics Capture. Works for monitors and individual windows.</summary>
    WindowsGraphicsCapture,

    /// <summary>Desktop Duplication API. Monitors only, often better for exclusive fullscreen.</summary>
    DesktopDuplication,
}

/// <summary>Describes what video to capture. Concrete types are interpreted by the engine.</summary>
public abstract record VideoSourceSpec;

/// <param name="MonitorIndex">Zero-based DXGI output index. Used by Desktop Duplication, and by WGC when no handle is given.</param>
/// <param name="MonitorHandle">HMONITOR from the platform catalog. Preferred by Windows Graphics Capture.</param>
public sealed record DisplaySource(
    int MonitorIndex,
    CaptureApi Api = CaptureApi.WindowsGraphicsCapture,
    bool CaptureCursor = true,
    long? MonitorHandle = null)
    : VideoSourceSpec;

/// <param name="WindowHandle">HWND of the top-level window to capture.</param>
public sealed record WindowSource(long WindowHandle, string Title, string? ProcessName, bool CaptureCursor = true)
    : VideoSourceSpec;

/// <summary>Synthetic test pattern. Used by integration tests and the "test stream" button.</summary>
public sealed record TestPatternSource : VideoSourceSpec;

/// <summary>A solid black canvas, for camera-only scenes ("Just Chatting").</summary>
public sealed record BlankCanvasSource : VideoSourceSpec;

/// <summary>Describes an audio input to mix into the stream.</summary>
public abstract record AudioSourceSpec
{
    /// <summary>Linear gain. 1.0 = unchanged.</summary>
    public double Gain { get; init; } = 1.0;
}

/// <param name="DeviceId">WASAPI endpoint ID, or null for the default communications/console microphone.</param>
public sealed record MicrophoneSource(string? DeviceId, string DisplayName) : AudioSourceSpec;

/// <param name="DeviceId">WASAPI render endpoint ID to loop back, or null for the default output device.</param>
public sealed record SystemAudioSource(string? DeviceId, string DisplayName) : AudioSourceSpec;

/// <summary>Synthetic sine tone generated inside FFmpeg. Used by tests.</summary>
public sealed record TestToneSource : AudioSourceSpec;

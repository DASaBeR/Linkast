// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core.Camera;

/// <summary>Hardware image control exposed by the camera itself (not a software effect).</summary>
public enum HardwareControl
{
    Exposure,
    Focus,
    WhiteBalance,
    Brightness,
    Contrast,
}

/// <summary>Capabilities of one hardware control. Values are in the device's own units.</summary>
public sealed record HardwareControlInfo(HardwareControl Control, bool Supported, double Min, double Max, double Step, double Default, bool SupportsAuto);

/// <summary>
/// A reference-counted handle to a running camera. Every consumer (preview, stream, ...) holds its
/// own feed; the physical device is opened once and closed when the last feed is disposed.
/// </summary>
public interface ICameraFeed : IAsyncDisposable
{
    CameraDevice Device { get; }

    CameraState State { get; }

    /// <summary>Why the camera is in <see cref="CameraState.Error"/> or <see cref="CameraState.Unavailable"/>.</summary>
    StreamError? LastError { get; }

    CameraFormat? ActiveFormat { get; }

    /// <summary>Frames per second actually delivered, measured.</summary>
    double MeasuredFps { get; }

    /// <summary>Newest frames, converted to BGRA. Shared by all feeds of the same device.</summary>
    LatestFrameExchange Frames { get; }

    /// <summary>Raised on any thread when <see cref="State"/>, <see cref="LastError"/> or the format changes.</summary>
    event EventHandler? StateChanged;

    IReadOnlyList<HardwareControlInfo> HardwareControls { get; }

    /// <summary>Sets a hardware control. <paramref name="value"/> null = automatic. Returns false if unsupported.</summary>
    Task<bool> SetHardwareControlAsync(HardwareControl control, double? value);
}

/// <summary>Discovers cameras and opens shared feeds.</summary>
public interface ICameraService
{
    /// <summary>Raised when cameras are added or removed.</summary>
    event EventHandler? DevicesChanged;

    Task<IReadOnlyList<CameraDevice>> ListAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CameraFormat>> GetFormatsAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a feed for the device, starting it if needed. Never throws for device problems:
    /// the feed reports them through <see cref="ICameraFeed.State"/> and <see cref="ICameraFeed.LastError"/>,
    /// and recovers automatically when the device returns.
    /// </summary>
    ICameraFeed Open(CameraDevice device, CameraFormatRequest format);
}

// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core;

/// <summary>Everything needed to run one stream.</summary>
public sealed record StreamSession(
    VideoSourceSpec Video,
    IReadOnlyList<AudioSourceSpec> Audio,
    EncoderSettings Encoder,
    Destination Destination,
    NetworkSelection Network,
    ReconnectPolicy Reconnect)
{
    /// <summary>Camera layers composited over <see cref="Video"/>, bottom to top.</summary>
    public IReadOnlyList<Camera.CameraLayerBinding> CameraLayers { get; init; } = [];

    /// <summary>Overlays (pictures, web alerts, the away screen) drawn above the cameras, bottom to top.</summary>
    public IReadOnlyList<Camera.OverlayLayerBinding> Overlays { get; init; } = [];

    /// <summary>Where the gameplay or screen capture goes on the canvas (default: all of it).</summary>
    public Camera.NormalizedRect BasePlacement { get; init; } = new(0, 0, 1, 1);

    public Camera.BaseFit BaseFit { get; init; } = Camera.BaseFit.Fit;
}

/// <summary>
/// The boundary between the UI and the media pipeline. The UI only talks to this interface,
/// so the FFmpeg implementation (or the UI framework) can be replaced independently.
/// </summary>
public interface IStreamEngine : IAsyncDisposable
{
    StreamState State { get; }

    /// <summary>Raised on any thread. UI consumers must marshal to their dispatcher.</summary>
    event EventHandler<StreamStateChangedEventArgs>? StateChanged;

    /// <summary>Raised on any thread about once per second while active.</summary>
    event EventHandler<StreamTelemetry>? TelemetryUpdated;

    /// <summary>Raised for each redacted diagnostic log line from the pipeline.</summary>
    event EventHandler<string>? DiagnosticLine;

    /// <summary>Validates and starts the session. Returns once the pipeline is launched, not when it is live.</summary>
    Task StartAsync(StreamSession session, CancellationToken cancellationToken = default);

    /// <summary>Stops gracefully, forcing termination after a short grace period.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}

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

    /// <summary>Overlays (pictures, web alerts) drawn above the cameras, bottom to top.</summary>
    public IReadOnlyList<Camera.OverlayLayerBinding> Overlays { get; init; } = [];

    /// <summary>Scene pictures and videos: they cover the canvas above the captured game and below the cameras.</summary>
    public IReadOnlyList<Camera.MediaLayerBinding> Media { get; init; } = [];

    /// <summary>
    /// Every game, window or display in the stream, one per scene that has its own, each placed where the
    /// starting scene wants it or hidden. A scene switch shows one and hides the others, without a restart.
    /// Empty means just <see cref="Video"/>, placed by <see cref="BasePlacement"/> and the fields below.
    /// </summary>
    public IReadOnlyList<CaptureBinding> Captures { get; init; } = [];

    /// <summary>False starts with the captured game or screen hidden (it can be shown live).</summary>
    public bool ShowCapture { get; init; } = true;

    /// <summary>Where the gameplay or screen capture goes on the canvas (default: all of it).</summary>
    public Camera.NormalizedRect BasePlacement { get; init; } = new(0, 0, 1, 1);

    public Camera.BaseFit BaseFit { get; init; } = Camera.BaseFit.Fit;

    /// <summary>The part of the captured picture that is shown, as fractions of it (default: all of it).</summary>
    public Camera.NormalizedRect BaseCrop { get; init; } = new(0, 0, 1, 1);

    /// <summary>False records without going live (<see cref="Destination"/> is then ignored).</summary>
    public bool Broadcast { get; init; } = true;

    /// <summary>Also writes the stream to a local file, or null.</summary>
    public RecordingSpec? Recording { get; init; }
}

/// <summary>One captured game, window or display in a stream, and where it starts on the canvas.</summary>
/// <param name="Key">Names the capture for live changes; the same source in two scenes is one capture.</param>
/// <param name="StartHidden">Starts off the canvas: another scene shows it.</param>
public sealed record CaptureBinding(
    string Key,
    VideoSourceSpec Source,
    Camera.NormalizedRect Placement,
    Camera.NormalizedRect Crop,
    Camera.BaseFit Fit,
    bool StartHidden);

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

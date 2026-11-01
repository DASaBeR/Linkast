// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core.Rendering;

/// <summary>Lifecycle state of a capture or media source.</summary>
public enum SceneSourceState
{
    /// <summary>The source is present and ready to be captured.</summary>
    Available,

    /// <summary>Frames are actively being produced and consumed.</summary>
    Capturing,

    /// <summary>The source is temporarily unavailable (e.g. window is minimized or occluded).</summary>
    TemporarilyUnavailable,

    /// <summary>The source is no longer available (e.g. game closed, process terminated, device unplugged).</summary>
    Lost
}

/// <summary>Timing and dimension metadata for an acquired frame.</summary>
public sealed record SceneFrameMetadata(
    int Width,
    int Height,
    long TimestampQpc,
    bool IsNewFrame);

/// <summary>Common abstraction for capture sources, cameras, and overlays in a scene.</summary>
public interface ISceneSource : IAsyncDisposable
{
    /// <summary>Unique identifier for this source instance.</summary>
    string Id { get; }

    /// <summary>Current lifecycle state of the source.</summary>
    SceneSourceState State { get; }

    /// <summary>Raised when the source transitions between lifecycle states.</summary>
    event EventHandler<SceneSourceState>? StateChanged;
}

// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core.Rendering;
using Vortice.Direct3D11;

namespace Linkast.Platform.Windows.Rendering;

/// <summary>
/// A scene source that renders or delivers its frame as a Direct3D 11 2D texture on the GPU.
/// </summary>
public interface ID3D11SceneSource : ISceneSource
{
    /// <summary>
    /// Acquires the most recent GPU texture for composition.
    /// If no new frame has arrived, returns the latest cached texture (or null if unavailable).
    /// </summary>
    ValueTask<ID3D11Texture2D?> AcquireFrameAsync(CancellationToken ct = default);
}

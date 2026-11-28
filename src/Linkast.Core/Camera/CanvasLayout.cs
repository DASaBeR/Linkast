// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core.Camera;

/// <summary>
/// How one extra canvas arranges the session's shared sources: where each one goes, its trim, and the
/// stacking order (bottom first). Keys are the same as the main canvas uses: capture keys, camera layer
/// IDs, overlay layer IDs and media layer IDs. A source without a placement is not drawn on this canvas.
/// </summary>
public sealed record CanvasLayout(
    IReadOnlyDictionary<string, NormalizedRect> Placements,
    IReadOnlyDictionary<string, NormalizedRect> Crops,
    IReadOnlyList<string> Stack,
    IReadOnlyDictionary<string, BaseFit>? Fits = null);

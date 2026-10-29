// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core.Camera;

/// <summary>
/// The order in which a scene's sources are stacked, bottom first: the game or screen, cameras and
/// overlays, by ID (a camera's or overlay's layer ID, and <see cref="CaptureId"/> for the scene's capture).
/// The scene's background is always below them all. Each scene has its own order; the stream has one
/// running stack for all its scenes (FFmpeg can't restack while running), built by <see cref="Merge"/> so
/// that as many scenes as possible fit it and switching to them stays instant.
/// </summary>
public static class SceneStacking
{
    /// <summary>The scene's game, window or display in <see cref="SceneLayout.StackOrder"/>.</summary>
    public const string CaptureId = "capture";

    /// <summary>
    /// The full order of <paramref name="present"/> (the sources that exist, in their default order) as
    /// <paramref name="stored"/> arranges them. IDs no longer present are dropped; a source the stored
    /// order doesn't have yet goes just above the one it follows by default (or at the bottom).
    /// </summary>
    public static IReadOnlyList<string> Resolve(IReadOnlyList<string>? stored, IReadOnlyList<string> present)
    {
        ArgumentNullException.ThrowIfNull(present);
        var exists = new HashSet<string>(present, StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var id in stored ?? [])
        {
            if (exists.Contains(id) && !result.Contains(id, StringComparer.Ordinal))
            {
                result.Add(id);
            }
        }

        for (var i = 0; i < present.Count; i++)
        {
            var id = present[i];
            if (result.Contains(id, StringComparer.Ordinal))
            {
                continue;
            }

            var after = -1;
            for (var j = i - 1; j >= 0 && after < 0; j--)
            {
                after = result.IndexOf(present[j]);
            }

            result.Insert(after + 1, id);
        }

        return result;
    }

    /// <summary><paramref name="order"/> with <paramref name="id"/> moved to position <paramref name="index"/> (clamped).</summary>
    public static IReadOnlyList<string> Move(IReadOnlyList<string> order, string id, int index)
    {
        ArgumentNullException.ThrowIfNull(order);
        var result = order.ToList();
        if (!result.Remove(id))
        {
            return order;
        }

        result.Insert(Math.Clamp(index, 0, result.Count), id);
        return result;
    }

    /// <summary>True when the IDs of <paramref name="scene"/> that are in <paramref name="stack"/> appear there in the same order.</summary>
    public static bool Fits(IReadOnlyList<string> stack, IReadOnlyList<string> scene)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(scene);
        var last = -1;
        foreach (var id in scene)
        {
            var at = IndexOf(stack, id);
            if (at < 0)
            {
                continue;
            }

            if (at < last)
            {
                return false;
            }

            last = at;
        }

        return true;
    }

    /// <summary>
    /// One stack for several scenes' orders (each listing only what that scene shows). The first, the
    /// active scene, is always kept as it is. Each next one that fits what is built so far adds its own
    /// sources in its order; one that conflicts only adds its sources (switching to it restacks the stream).
    /// </summary>
    public static IReadOnlyList<string> Merge(IReadOnlyList<IReadOnlyList<string>> scenes)
    {
        ArgumentNullException.ThrowIfNull(scenes);
        var result = new List<string>();
        foreach (var scene in scenes)
        {
            var fits = Fits(result, scene);
            for (var i = 0; i < scene.Count; i++)
            {
                var id = scene[i];
                if (result.Contains(id, StringComparer.Ordinal))
                {
                    continue;
                }

                // Just above the nearest source below it in this scene that is placed already, so this
                // scene's order holds; at the bottom when none is. A conflicting scene only appends.
                var at = result.Count;
                if (fits)
                {
                    at = 0;
                    for (var j = i - 1; j >= 0; j--)
                    {
                        if (IndexOf(result, scene[j]) is >= 0 and var below)
                        {
                            at = below + 1;
                            break;
                        }
                    }
                }

                result.Insert(at, id);
            }
        }

        return result;
    }

    private static int IndexOf(IReadOnlyList<string> list, string id)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}

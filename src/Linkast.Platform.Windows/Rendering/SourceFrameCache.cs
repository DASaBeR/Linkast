// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Vortice.Direct3D11;

namespace Linkast.Platform.Windows.Rendering;

/// <summary>
/// Caches the most recent valid GPU texture for each scene source.
/// Enables steady 60 FPS composition even when individual sources produce frames
/// at lower rates (e.g. 30 FPS webcams) or temporarily pause painting.
/// </summary>
public sealed class SourceFrameCache : IDisposable
{
    private sealed class CachedSlot : IDisposable
    {
        public ID3D11Texture2D? Texture { get; set; }
        public long TimestampQpc { get; set; }
        public bool IsFresh { get; set; }

        public void Dispose()
        {
            Texture?.Dispose();
            Texture = null;
        }
    }

    private readonly ConcurrentDictionary<string, CachedSlot> _slots = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Updates or stores the latest texture for a source.</summary>
    public void Store(string sourceId, ID3D11Texture2D texture, long timestampQpc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var slot = _slots.GetOrAdd(sourceId, _ => new CachedSlot());
        lock (slot)
        {
            slot.Texture = texture;
            slot.TimestampQpc = timestampQpc;
            slot.IsFresh = true;
        }
    }

    /// <summary>
    /// Retrieves the latest valid texture for a source, marking freshness as consumed.
    /// Returns null if no frame has ever been received for this source.
    /// </summary>
    public (ID3D11Texture2D? Texture, long TimestampQpc, bool WasFresh) Acquire(string sourceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_slots.TryGetValue(sourceId, out var slot))
        {
            return (null, 0, false);
        }

        lock (slot)
        {
            var wasFresh = slot.IsFresh;
            slot.IsFresh = false;
            return (slot.Texture, slot.TimestampQpc, wasFresh);
        }
    }

    /// <summary>Removes a source and releases any cached texture.</summary>
    public void Remove(string sourceId)
    {
        if (_slots.TryRemove(sourceId, out var slot))
        {
            lock (slot)
            {
                slot.Dispose();
            }
        }
    }

    /// <summary>Clears all cached textures.</summary>
    public void Clear()
    {
        foreach (var key in _slots.Keys.ToArray())
        {
            Remove(key);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Clear();
    }
}

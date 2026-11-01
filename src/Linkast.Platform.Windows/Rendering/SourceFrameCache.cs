// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Vortice.Direct3D11;

namespace Linkast.Platform.Windows.Rendering;

/// <summary>
/// Caches the most recent valid GPU texture and its ShaderResourceView for each scene source.
/// Enables steady 60 FPS composition even when individual sources produce frames
/// at lower rates or temporarily pause painting.
/// SRV lifecycle is managed per source slot to eliminate dangling SRVs when textures are recreated.
/// </summary>
public sealed class SourceFrameCache : IDisposable
{
    private sealed class CachedSlot : IDisposable
    {
        public ID3D11Texture2D? Texture { get; set; }
        public ID3D11ShaderResourceView? Srv { get; set; }
        public long TimestampQpc { get; set; }
        public bool IsFresh { get; set; }

        public void Dispose()
        {
            Srv?.Dispose();
            Srv = null;
            Texture = null;
        }
    }

    private readonly D3D11DeviceManager? _deviceManager;
    private readonly ConcurrentDictionary<string, CachedSlot> _slots = new(StringComparer.Ordinal);
    private bool _disposed;

    public SourceFrameCache(D3D11DeviceManager? deviceManager = null)
    {
        _deviceManager = deviceManager;
    }

    /// <summary>Updates or stores the latest texture for a source, recreating the SRV if the texture instance changed.</summary>
    public void Store(string sourceId, ID3D11Texture2D texture, long timestampQpc, ID3D11Device? device = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var slot = _slots.GetOrAdd(sourceId, _ => new CachedSlot());
        lock (slot)
        {
            if (!ReferenceEquals(slot.Texture, texture))
            {
                slot.Srv?.Dispose();
                var dev = device ?? _deviceManager?.Device;
                slot.Srv = dev?.CreateShaderResourceView(texture);
                slot.Texture = texture;
            }

            slot.TimestampQpc = timestampQpc;
            slot.IsFresh = true;
        }
    }

    /// <summary>
    /// Retrieves the latest valid texture and cached SRV for a source, marking freshness as consumed.
    /// Returns null if no frame has ever been received for this source.
    /// </summary>
    public (ID3D11Texture2D? Texture, ID3D11ShaderResourceView? Srv, long TimestampQpc, bool WasFresh) AcquireWithSrv(string sourceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_slots.TryGetValue(sourceId, out var slot))
        {
            return (null, null, 0, false);
        }

        lock (slot)
        {
            var wasFresh = slot.IsFresh;
            slot.IsFresh = false;
            return (slot.Texture, slot.Srv, slot.TimestampQpc, wasFresh);
        }
    }

    /// <summary>
    /// Retrieves the latest valid texture for a source, marking freshness as consumed.
    /// Returns null if no frame has ever been received for this source.
    /// </summary>
    public (ID3D11Texture2D? Texture, long TimestampQpc, bool WasFresh) Acquire(string sourceId)
    {
        var (tex, _, ts, fresh) = AcquireWithSrv(sourceId);
        return (tex, ts, fresh);
    }

    /// <summary>Removes a source and releases any cached texture and SRV.</summary>
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

    /// <summary>Clears all cached textures and SRVs.</summary>
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

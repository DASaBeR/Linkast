// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Linkast.Core.Camera;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Linkast.Platform.Windows.Camera;

/// <summary>
/// Decodes background images (PNG, JPEG, BMP, ...) with Windows' imaging codecs, off the calling
/// thread, scaled to at most 1920 px wide, and caches them by path and modification time. A missing
/// or unreadable file yields null, which the renderer treats as "use blur".
/// </summary>
public sealed class BackgroundImageCache
{
    private const uint MaxWidth = 1920;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the decoded image if ready; otherwise starts loading it and returns null.</summary>
    public BgraFrame? Get(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        DateTime modified;
        try
        {
            modified = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        var entry = _entries.AddOrUpdate(
            path,
            p => Entry.Start(p, modified),
            (p, existing) => existing.Modified == modified ? existing : Entry.Start(p, modified));
        return entry.Task.IsCompletedSuccessfully ? entry.Task.Result : null;
    }

    /// <summary>True when the file exists but could not be decoded.</summary>
    public bool Failed(string? path) =>
        path is not null && _entries.TryGetValue(path, out var e) && e.Task.IsCompleted && e.Task.Result is null;

    public static async Task<BgraFrame?> DecodeAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var scale = decoder.PixelWidth > MaxWidth ? (double)MaxWidth / decoder.PixelWidth : 1;
            var w = Math.Max(1u, (uint)(decoder.PixelWidth * scale));
            var h = Math.Max(1u, (uint)(decoder.PixelHeight * scale));
            var transform = new BitmapTransform { ScaledWidth = w, ScaledHeight = h, InterpolationMode = BitmapInterpolationMode.Fant };
            var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
            var data = pixels.DetachPixelData();

            // EXIF rotation may swap the dimensions.
            var outW = (int)w;
            var outH = (int)h;
            if (data.Length != outW * outH * 4)
            {
                (outW, outH) = (outH, outW);
            }

            var frame = new BgraFrame(outW, outH);
            Buffer.BlockCopy(data, 0, frame.Pixels, 0, Math.Min(data.Length, frame.Pixels.Length));
            return frame;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException or FileNotFoundException)
        {
            return null;
        }
    }

    private sealed record Entry(DateTime Modified, Task<BgraFrame?> Task)
    {
        public static Entry Start(string path, DateTime modified) => new(modified, System.Threading.Tasks.Task.Run(() => DecodeAsync(path)));
    }
}

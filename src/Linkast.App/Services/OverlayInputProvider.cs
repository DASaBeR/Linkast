// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using Linkast.Core.Camera;
using Linkast.Platform.Windows.Layers;

namespace Linkast.App.Services;

/// <summary>
/// Opens overlay layers for the stream. Pictures and the away card are drawn once (on the UI thread,
/// because they use WPF) and then served as still frames; web pages come from <see cref="WebOverlayHost"/>.
/// </summary>
public sealed class OverlayInputProvider(WebOverlayHost web) : IOverlayInputProvider
{
    public IVideoLayerInput Open(OverlayLayerBinding overlay, int width, int height, int maxFrameRate)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        if (overlay.Source is WebOverlaySource page)
        {
            // Rendered at the page's own size; FFmpeg scales it into place.
            return web.Open(overlay.LayerId, page.Url.Reveal(), page.PageWidth, page.PageHeight, maxFrameRate);
        }

        var frame = Application.Current.Dispatcher.Invoke(() => Render(overlay.Source, width, height));
        return new StaticFramePipeInput(overlay.LayerId, frame, width, height);
    }

    private static byte[] Render(OverlaySource source, int width, int height) => source switch
    {
        ImageOverlaySource image => OverlayRenderer.LoadImage(image.Path) is { } bitmap
            ? OverlayRenderer.ToLayerFrame(bitmap, width, height)
            : new byte[width * height * 4], // missing picture: transparent, never a failed stream
        AwayScreenSource away => OverlayRenderer.LoadImage(away.ImagePath) is { } picture
            ? OverlayRenderer.ToLayerFrame(picture, width, height, cover: true)
            : OverlayRenderer.ToLayerFrame(OverlayRenderer.RenderAwayCard(width, height, away.Title, away.Subtitle), width, height),
        _ => throw new NotSupportedException($"Overlay type {source.GetType().Name} is not supported yet."),
    };
}

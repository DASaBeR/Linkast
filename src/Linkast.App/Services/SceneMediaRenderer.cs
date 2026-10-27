// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Linkast.Core.Camera;

namespace Linkast.App.Services;

/// <summary>
/// Turns a scene's background into what the preview shows and the file the stream reads: the Linkast
/// card is drawn (and, for the stream, saved as a picture), a picture is used as it is, and a video is
/// played by FFmpeg itself. UI thread only (the card is drawn with WPF).
/// </summary>
public static class SceneMediaRenderer
{
    /// <summary>Where drawn cards are saved for the stream.</summary>
    public static string CardFolder => Path.Combine(App.DataDirectory, "scenes");

    /// <summary>A picture of the background for the preview, or null for none (and for a video, which the preview plays itself).</summary>
    public static ImageSource? Preview(SceneMedia? media, double canvasWidth, double canvasHeight)
    {
        switch (media?.Kind)
        {
            case SceneMediaKind.Card:
                // A small drawing is plenty for the preview.
                var (w, h) = canvasWidth >= canvasHeight
                    ? (480, (int)Math.Round(480 * canvasHeight / canvasWidth))
                    : ((int)Math.Round(480 * canvasWidth / canvasHeight), 480);
                return OverlayRenderer.RenderAwayCard(Math.Max(w, 2), Math.Max(h, 2), media.Title, media.Subtitle);
            case SceneMediaKind.Picture:
                return OverlayRenderer.LoadImage(media.Path);
            default:
                return null;
        }
    }

    /// <summary>The video the preview plays on a loop, or null.</summary>
    public static string? PreviewVideo(SceneMedia? media) =>
        media is { Kind: SceneMediaKind.Video, Path: { Length: > 0 } path } && File.Exists(path) ? path : null;

    /// <summary>
    /// The file the stream shows for <paramref name="scene"/>'s background, or null when it has none (or
    /// its picture or video is missing). A card is drawn at the stream's size and saved under its scene.
    /// </summary>
    public static (string Path, bool IsVideo)? StreamFile(SceneLayout scene, int canvasWidth, int canvasHeight)
    {
        ArgumentNullException.ThrowIfNull(scene);
        switch (scene.Media)
        {
            case { Kind: SceneMediaKind.Video, Path: { Length: > 0 } video } when File.Exists(video):
                return (video, true);
            case { Kind: SceneMediaKind.Picture, Path: { Length: > 0 } picture } when File.Exists(picture):
                return (picture, false);
            case { Kind: SceneMediaKind.Card } card:
                Directory.CreateDirectory(CardFolder);
                var file = Path.Combine(CardFolder, $"{scene.Id:N}.png");
                var bitmap = OverlayRenderer.RenderAwayCard(canvasWidth, canvasHeight, card.Title, card.Subtitle);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(file))
                {
                    encoder.Save(stream);
                }

                return (file, false);
            default:
                return null;
        }
    }
}

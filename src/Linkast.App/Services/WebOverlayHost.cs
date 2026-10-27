// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using Linkast.Platform.Windows.Layers;
using Microsoft.Web.WebView2.Core;

namespace Linkast.App.Services;

/// <summary>
/// Keeps one renderer per web overlay, shared by the output preview and every attempt of a stream
/// (a reconnect does not reload the page, so an alert in progress keeps playing). All members except
/// <see cref="Open"/> run on the UI thread.
/// </summary>
public sealed class WebOverlayHost : IAsyncDisposable
{
    private readonly Dictionary<string, (string Url, int Width, int Height, WebOverlayRenderer Renderer)> _renderers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _problems = new(StringComparer.Ordinal);

    /// <summary>Starts (or keeps) the page for <paramref name="layerId"/>. A different link or size reloads it.</summary>
    public async Task<WebOverlayRenderer?> EnsureAsync(string layerId, string url, int width, int height)
    {
        if (_renderers.TryGetValue(layerId, out var current))
        {
            if (current.Url == url && current.Width == width && current.Height == height)
            {
                return current.Renderer;
            }

            await StopAsync(layerId);
        }

        try
        {
            var renderer = await WebOverlayRenderer.CreateAsync(layerId, url, width, height);
            _renderers[layerId] = (url, width, height, renderer);
            _problems.Remove(layerId);
            return renderer;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            _problems[layerId] = "Web overlays need the Microsoft Edge WebView2 Runtime, which is missing on this PC.";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            _problems[layerId] = "The web overlay could not start: " + ex.Message;
        }

        return null;
    }

    public WebOverlayRenderer? Get(string layerId) => _renderers.TryGetValue(layerId, out var r) ? r.Renderer : null;

    /// <summary>Why a web overlay is not showing, if it isn't.</summary>
    public string? ProblemOf(string layerId) =>
        _problems.TryGetValue(layerId, out var p) ? p : Get(layerId)?.Problem;

    public async Task StopAsync(string layerId)
    {
        if (_renderers.Remove(layerId, out var r))
        {
            await r.Renderer.DisposeAsync();
        }
    }

    /// <summary>Stops every page not in <paramref name="keep"/>.</summary>
    public async Task KeepOnlyAsync(IReadOnlyCollection<string> keep)
    {
        foreach (var id in _renderers.Keys.Where(id => !keep.Contains(id)).ToList())
        {
            await StopAsync(id);
        }
    }

    /// <summary>
    /// A pipe input for a stream attempt. Called from the engine's thread; the page is started on the UI
    /// thread. If WebView2 is unavailable the layer is transparent: an overlay never stops the stream.
    /// </summary>
    public Linkast.Core.Camera.IVideoLayerInput Open(string layerId, string url, int width, int height, int frameRate)
    {
        var renderer = Application.Current.Dispatcher.InvokeAsync(() => EnsureAsync(layerId, url, width, height)).Task.Unwrap().GetAwaiter().GetResult();
        return renderer is null
            ? new StaticFramePipeInput(layerId, new byte[width * height * 4], width, height)
            : new WebOverlayPipeInput(layerId, renderer, frameRate);
    }

    public async ValueTask DisposeAsync()
    {
        // At exit the WebView2 processes end with Linkast anyway; closing them is best effort.
        foreach (var id in _renderers.Keys.ToList())
        {
            try
            {
                await StopAsync(id);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
            }
        }
    }}

/// <summary>Serves a web overlay's latest picture to FFmpeg at the page's own size.</summary>
public sealed class WebOverlayPipeInput(string layerId, WebOverlayRenderer renderer, int frameRate)
    : PipeFrameWriter(layerId, "web", renderer.Width, renderer.Height, frameRate)
{
    protected override void ProduceFrame() => renderer.CopyLatest(Frame);
}

// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core.Camera;
using Linkast.Core.Rendering;
using Linkast.Platform.Windows.Rendering;

namespace Linkast.App.Tests;

public class OverlayD3D11SceneSourceTests
{
    [Fact]
    public async Task ImageD3D11SceneSource_uploads_pixels_and_updates_dynamically()
    {
        using var manager = new D3D11DeviceManager();
        int width = 32;
        int height = 32;

        // Create a 32x32 blue test pixel buffer (BGRA)
        byte[] bluePixels = new byte[width * height * 4];
        for (int i = 0; i < bluePixels.Length; i += 4)
        {
            bluePixels[i] = 255;     // B
            bluePixels[i + 1] = 0;   // G
            bluePixels[i + 2] = 0;   // R
            bluePixels[i + 3] = 255; // A
        }

        await using var source = new ImageD3D11SceneSource("logo-overlay", manager, bluePixels, width, height);

        Assert.Equal("logo-overlay", source.Key);
        Assert.Equal("logo-overlay", source.Id);
        Assert.Equal(SceneSourceState.Capturing, source.State);

        var tex = await source.AcquireFrameAsync();
        Assert.NotNull(tex);
        Assert.Equal((uint)width, tex.Description.Width);
        Assert.Equal((uint)height, tex.Description.Height);

        // Update with green pixels
        byte[] greenPixels = new byte[width * height * 4];
        for (int i = 0; i < greenPixels.Length; i += 4)
        {
            greenPixels[i] = 0;       // B
            greenPixels[i + 1] = 255; // G
            greenPixels[i + 2] = 0;   // R
            greenPixels[i + 3] = 255; // A
        }

        source.UpdatePixels(greenPixels, width, height);
        var updatedTex = await source.AcquireFrameAsync();
        Assert.NotNull(updatedTex);
    }

    [Fact]
    public async Task WebOverlayD3D11SceneSource_copies_frames_and_handles_lifecyle()
    {
        using var manager = new D3D11DeviceManager();
        int width = 48;
        int height = 48;

        int copyCount = 0;
        bool isAlive = true;

        bool CopyLatestMock(byte[] destination)
        {
            copyCount++;
            // Fill with red
            for (int i = 0; i < destination.Length; i += 4)
            {
                destination[i] = 0;       // B
                destination[i + 1] = 0;   // G
                destination[i + 2] = 255; // R
                destination[i + 3] = 255; // A
            }
            return true;
        }

        await using var webSource = new WebOverlayD3D11SceneSource(
            "alert-box",
            manager,
            width,
            height,
            CopyLatestMock,
            isAlive: () => isAlive);

        Assert.Equal("alert-box", webSource.Key);
        Assert.Equal(SceneSourceState.Capturing, webSource.State);

        var tex = await webSource.AcquireFrameAsync();
        Assert.NotNull(tex);
        Assert.True(copyCount > 0);
        Assert.Equal((uint)width, tex.Description.Width);

        // When alive returns false, state becomes Lost
        isAlive = false;
        Assert.Equal(SceneSourceState.Lost, webSource.State);
        var lostTex = await webSource.AcquireFrameAsync();
        Assert.Null(lostTex);
    }
}

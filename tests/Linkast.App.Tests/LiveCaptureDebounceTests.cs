// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Platform.Windows.Capture;

namespace Linkast.App.Tests;

public class LiveCaptureDebounceTests
{
    [Fact]
    public void Nonexistent_window_handle_is_not_capturable()
    {
        Assert.False(VideoSourceCatalog.WindowExists(0));
        Assert.False(VideoSourceCatalog.IsWindowCapturable(0));
    }

    [Fact]
    public void Invalid_handle_reports_not_minimized()
    {
        // 0 handle is not a window, so IsIconic returns false
        Assert.False(VideoSourceCatalog.IsWindowMinimized(0));
    }

    [Fact]
    public void Client_size_for_invalid_handle_is_null()
    {
        Assert.Null(VideoSourceCatalog.ClientSize(0));
        Assert.Null(VideoSourceCatalog.ClientSize(-1));
    }
}

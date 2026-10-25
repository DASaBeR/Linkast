// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Windows;
using Linkast.App.Services;

namespace Linkast.App.Views;

/// <summary>The GPL's "appropriate legal notices": copyright, no warranty, license and source.</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        ProductText.Text = "Linkast " + UpdateService.CurrentVersion;
        SourceButton.IsEnabled = UpdateService.RepositoryUrl is not null;
    }

    private void OnLicense(object sender, RoutedEventArgs e) => OpenLocal("LICENSE", "https://www.gnu.org/licenses/gpl-3.0.html");

    private void OnNotices(object sender, RoutedEventArgs e) => OpenLocal("THIRD-PARTY-NOTICES.md", UpdateService.RepositoryUrl + "/blob/main/THIRD-PARTY-NOTICES.md");

    private void OnFfmpeg(object sender, RoutedEventArgs e) => OpenLocal(Path.Combine("ffmpeg", "BUILD-INFO.txt"), UpdateService.RepositoryUrl + "/releases");

    private void OnSource(object sender, RoutedEventArgs e) => Open(UpdateService.RepositoryUrl!);

    /// <summary>Opens the file shipped next to Linkast.exe, or the web copy in a development build.</summary>
    private static void OpenLocal(string relative, string? fallbackUrl)
    {
        var path = Path.Combine(AppContext.BaseDirectory, relative);
        if (File.Exists(path))
        {
            Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { path }, UseShellExecute = false });
        }
        else if (fallbackUrl is { Length: > 0 } && Uri.TryCreate(fallbackUrl, UriKind.Absolute, out _))
        {
            Open(fallbackUrl);
        }
    }

    private static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}

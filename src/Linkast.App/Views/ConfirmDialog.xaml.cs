// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;

namespace Linkast.App.Views;

/// <summary>How a confirmation looks: a plain question, a privacy or safety warning, or a destructive action.</summary>
public enum ConfirmKind
{
    Question,
    Warning,
    Destructive,
}

/// <summary>
/// A yes/no question in the Linkast look, used instead of the Windows message box. Enter confirms and
/// Escape cancels; for warnings and destructive actions the safe button has the focus first.
/// </summary>
public partial class ConfirmDialog : Window
{
    private ConfirmDialog()
    {
        InitializeComponent();
    }

    /// <summary>Asks the question and returns true only when the user pressed the confirm button.</summary>
    public static bool Ask(string title, string message, string confirmText, string cancelText = "Cancel", ConfirmKind kind = ConfirmKind.Question)
    {
        var owner = Application.Current?.MainWindow;
        var dialog = new ConfirmDialog
        {
            Title = title,
            Owner = owner is { IsVisible: true } ? owner : null,
        };
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.ConfirmButton.Content = confirmText;
        dialog.CancelButton.Content = cancelText;

        if (kind == ConfirmKind.Question)
        {
            dialog.ConfirmButton.IsDefault = true;
            dialog.Loaded += (_, _) => dialog.ConfirmButton.Focus();
        }
        else
        {
            var (glyph, brush) = kind == ConfirmKind.Destructive ? ("", "Linkast.Live") : ("", "Linkast.Warning");
            dialog.IconGlyph.Text = glyph;
            dialog.IconGlyph.SetResourceReference(ForegroundProperty, brush);
            dialog.IconBadge.SetResourceReference(BackgroundProperty, brush + "Soft");
            if (kind == ConfirmKind.Destructive)
            {
                dialog.ConfirmButton.Style = (Style)dialog.FindResource("Linkast.Button.Destructive");
            }

            dialog.Loaded += (_, _) => dialog.CancelButton.Focus();
        }

        return dialog.ShowDialog() == true;
    }

    /// <summary>Tells the user something that needs no decision: one OK button.</summary>
    public static void Inform(string title, string message)
    {
        var owner = Application.Current?.MainWindow;
        var dialog = new ConfirmDialog
        {
            Title = title,
            Owner = owner is { IsVisible: true } ? owner : null,
        };
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.IconGlyph.Text = "";
        dialog.ConfirmButton.Content = "OK";
        dialog.ConfirmButton.IsDefault = true;
        dialog.CancelButton.Visibility = Visibility.Collapsed;
        dialog.Loaded += (_, _) => dialog.ConfirmButton.Focus();
        dialog.ShowDialog();
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}

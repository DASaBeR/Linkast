// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Linkast.App.ViewModels;

namespace Linkast.App.Views;

public partial class WebcamPanel : UserControl
{
    public WebcamPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged old)
            {
                old.PropertyChanged -= OnStudioChanged;
            }

            if (e.NewValue is INotifyPropertyChanged now)
            {
                now.PropertyChanged += OnStudioChanged;
            }

            if (e.OldValue is WebcamStudioViewModel was)
            {
                was.RemoveCameraCommand.CanExecuteChanged -= OnRemoveCanExecuteChanged;
            }

            if (e.NewValue is WebcamStudioViewModel studio)
            {
                studio.RemoveCameraCommand.CanExecuteChanged += OnRemoveCanExecuteChanged;
                OnRemoveCanExecuteChanged(this, EventArgs.Empty);
            }
        };
    }

    // Remove asks first, so it is a click handler; it stays enabled exactly when the command can run.
    private void OnRemoveCanExecuteChanged(object? sender, EventArgs e) =>
        RemoveCameraButton.IsEnabled = DataContext is WebcamStudioViewModel studio && studio.RemoveCameraCommand.CanExecute(null);

    private void OnRemoveCamera(object sender, RoutedEventArgs e)
    {
        if (DataContext is WebcamStudioViewModel { Selected: { } layer } studio
            && studio.RemoveCameraCommand.CanExecute(null)
            && ConfirmDialog.Ask(
                $"Remove \"{layer.Name}\"?",
                "The camera layer and its look are removed from every scene. To hide it in one scene only, use its eye on the Stream page or Shown under Layout.",
                "Remove camera",
                kind: ConfirmKind.Destructive))
        {
            studio.RemoveCameraCommand.Execute(null);
        }
    }

    /// <summary>Opens the Layout section (used by "Edit layout" on the Stream page).</summary>
    public void ShowLayout() => TabLayout.IsSelected = true;

    /// <summary>Opens the selected camera's settings (used by a camera row on the Stream page).</summary>
    public void ShowCamera() => TabStudio.IsSelected = true;

    private void OnStudioChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WebcamStudioViewModel.Selected))
        {
            // After layout, so the newly visible panel is in the visual tree.
            Dispatcher.BeginInvoke(LayerPanels.RefreshAutomation, DispatcherPriority.Loaded);
        }
    }
}

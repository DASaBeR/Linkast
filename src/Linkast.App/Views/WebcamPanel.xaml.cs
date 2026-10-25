// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
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
        };
    }

    /// <summary>Opens the Layout section (used by "Edit layout" on the Stream page).</summary>
    public void ShowLayout() => TabLayout.IsSelected = true;

    private void OnStudioChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WebcamStudioViewModel.Selected))
        {
            // After layout, so the newly visible panel is in the visual tree.
            Dispatcher.BeginInvoke(LayerPanels.RefreshAutomation, DispatcherPriority.Loaded);
        }
    }
}

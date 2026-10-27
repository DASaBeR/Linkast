// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Linkast.App.ViewModels;
using Linkast.Core;

namespace Linkast.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _syncingKey;
    private bool _closingAfterStop;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = viewModel;
        viewModel.IsPreviewVisible = true;
        viewModel.IsStreamPageVisible = true;
        viewModel.StreamKeyLoaded += (_, key) =>
        {
            _syncingKey = true;
            StreamKeyBox.Password = key.Reveal();
            _syncingKey = false;
        };
        ((INotifyCollectionChanged)LogList.Items).CollectionChanged += (_, _) =>
        {
            if (LogList.Items.Count > 0)
            {
                LogList.ScrollIntoView(LogList.Items[^1]);
            }
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _vm.AttachHotkeys(this);
    }

    // The stream key lives in a PasswordBox and reaches the view model only as a Secret.
    private void OnStreamKeyChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingKey)
        {
            return;
        }

        _vm.StreamKey = new Secret(StreamKeyBox.Password.Trim());
    }

    private void OnStreamKeyPlainChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingKey)
        {
            return;
        }

        _syncingKey = true;
        StreamKeyBox.Password = StreamKeyPlain.Text;
        _syncingKey = false;
        _vm.StreamKey = new Secret(StreamKeyPlain.Text.Trim());
    }

    private void OnShowKeyChanged(object sender, RoutedEventArgs e)
    {
        var show = ShowKeyToggle.IsChecked == true;
        _syncingKey = true;
        if (show)
        {
            StreamKeyPlain.Text = StreamKeyBox.Password;
        }
        else
        {
            StreamKeyPlain.Text = string.Empty;
        }

        _syncingKey = false;
        StreamKeyPlain.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        StreamKeyBox.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        ShowKeyToggle.Content = show ? "Hide" : "Show";
        (show ? (Control)StreamKeyPlain : StreamKeyBox).Focus();
    }

    // The capture snapshots behind the output preview and the layout editor only run while one of them is on screen.
    private void OnPageChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.IsPreviewVisible = NavStream.IsChecked == true || NavWebcam.IsChecked == true;
            vm.IsStreamPageVisible = NavStream.IsChecked == true;
        }
    }

    private void OnEditLayout(object sender, RoutedEventArgs e)
    {
        NavWebcam.IsChecked = true;
        Studio.ShowLayout();
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) => NavSettings.IsChecked = true;

    // A right-click on a scene tile only opens its menu; selecting the tile would switch to the scene.
    private void OnSceneTileRightButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    // "+" in the scene strip: a menu of ready-made scenes.
    private void OnAddSceneMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var kind in _vm.Webcam.SceneKinds)
        {
            menu.Items.Add(new MenuItem
            {
                Header = Converters.EnumLabel.Convert(kind, typeof(string), null, System.Globalization.CultureInfo.CurrentUICulture),
                Command = _vm.Webcam.AddSceneCommand,
                CommandParameter = kind,
            });
        }

        menu.IsOpen = true;
    }

    // Clicking an overlay's card selects it in the Output preview (the preview flashes it). The
    // event is not handled, so the card's text boxes and buttons still work as usual.
    private void OnOverlayCardPressed(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OverlayItemViewModel item })
        {
            _vm.Overlays.Selected = item;
        }
    }

    // Scrolls the Output preview into view and flashes the overlay there, even if it was already selected.
    private void OnLocateOverlay(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OverlayItemViewModel item })
        {
            _vm.Overlays.Selected = item;
            OutputPreview.BringIntoView();
            OutputPreview.Locate(item);
        }
    }

    private void OnLogToggled(object sender, RoutedEventArgs e)
    {
        var open = LogToggle.IsChecked == true;
        LogToggleText.Text = open ? "Hide log" : "Show log";
        LogToggleGlyph.Text = open ? "" : ""; // chevron up / down
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closingAfterStop || !_vm.IsActive)
        {
            return;
        }

        // Stop gracefully so the server receives a clean end of stream.
        e.Cancel = true;
        IsEnabled = false;
        await _vm.StopAsync();
        _closingAfterStop = true;
        Close();
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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

    private void OnResetSystemVolume(object sender, RoutedEventArgs e) => _vm.SystemAudioVolume = 100;

    private void OnResetMicrophoneVolume(object sender, RoutedEventArgs e) => _vm.MicrophoneVolume = 100;

    private void OnLogToggled(object sender, RoutedEventArgs e) => LogToggle.Content = LogToggle.IsChecked == true ? "Hide log" : "Show log";

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

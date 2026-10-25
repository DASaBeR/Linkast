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

    private void OnAbout(object sender, RoutedEventArgs e) => new Views.AboutWindow { Owner = this }.ShowDialog();

    private void OnSettingsTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, SettingsTabs))
        {
            _vm.IsWebcamTabVisible = SettingsTabs.SelectedItem == WebcamTab;
        }
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

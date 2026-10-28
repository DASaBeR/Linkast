// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Linkast.App.ViewModels;
using Linkast.App.Views;
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
        FitToWorkArea();
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

        if (HeaderPage is null)
        {
            return;
        }

        (HeaderPage.Text, HeaderHint.Text) = sender switch
        {
            _ when sender == NavWebcam => ("Webcam Studio", "Shape your cameras, add effects and place them on your stream"),
            _ when sender == NavSettings => ("Settings", "Appearance, updates, hotkeys, OBS import and diagnostics"),
            _ when sender == NavAbout => ("About", "Version, licence and open-source notices"),
            _ => ("Stream", "Set up your scenes, then go live"),
        };
    }

    // The widths the user chose for the side panels (by dragging their edges). Both start at their widest.
    private double _leftWidth = 380;
    private double _rightWidth = 440;

    private void OnPanelResized(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _leftWidth = LeftColumn.ActualWidth;
        _rightWidth = RightColumn.ActualWidth;
    }

    // The chosen widths stay through maximize and restore. Only when the window is too narrow for them and the
    // preview do the side panels give up width (the status panel first), and they get it back when there is room.
    private void OnStreamPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged)
        {
            return;
        }

        const double Splitters = 24;
        var room = e.NewSize.Width - Splitters - StreamCenterMinWidth;
        var right = Math.Max(RightColumn.MinWidth, Math.Min(_rightWidth, room - _leftWidth));
        var left = Math.Max(LeftColumn.MinWidth, Math.Min(_leftWidth, room - right));
        LeftColumn.Width = new GridLength(left);
        RightColumn.Width = new GridLength(right);
    }

    private const double StreamCenterMinWidth = 320;

    // The mixer row scrolls sideways only, but its ScrollViewer still swallows the wheel. Pass a plain wheel on to
    // the settings page so it scrolls as usual; Shift + wheel scrolls the faders sideways.
    private void OnMixerWheel(object sender, MouseWheelEventArgs e)
    {
        var mixer = (ScrollViewer)sender;
        e.Handled = true;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            mixer.ScrollToHorizontalOffset(mixer.HorizontalOffset - e.Delta);
            return;
        }

        SettingsScroll.ScrollToVerticalOffset(SettingsScroll.VerticalOffset - e.Delta);
    }

    // The window opens maximized; restoring it gives 1440 × 900, or smaller on a small screen, never beyond the work area.
    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width - 40);
        Height = Math.Min(Height, area.Height - 40);
    }

    /// <summary>Shows one of the settings tabs under the preview (on the Stream page) and scrolls it to the top.</summary>
    private void ShowStreamTab(ListBoxItem tab)
    {
        NavStream.IsChecked = true;
        tab.IsSelected = true;
        SettingsScroll.ScrollToTop();
        tab.Focus();
    }

    private void OnOpenDestination(object sender, RoutedEventArgs e) => ShowStreamTab(TabDestination);

    private void OnOpenQuality(object sender, RoutedEventArgs e) => ShowStreamTab(TabQuality);

    private void OnOpenNetwork(object sender, RoutedEventArgs e) => ShowStreamTab(TabNetwork);

    // The error banner's fix button opens the tab where the problem can be solved (see Converters.ErrorFixTab).
    private void OnFixError(object sender, RoutedEventArgs e)
    {
        var tab = Converters.ErrorFixTab(_vm.LastError) switch
        {
            "Destination" => TabDestination,
            "Quality" => TabQuality,
            "Network" => TabNetwork,
            "Capture" => TabSource,
            "Audio" => TabAudio,
            _ => null,
        };
        if (tab is not null)
        {
            ShowStreamTab(tab);
        }
    }

    private void OnEditLayout(object sender, RoutedEventArgs e)
    {
        NavWebcam.IsChecked = true;
        Studio.ShowLayout();
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) => NavSettings.IsChecked = true;

    // ---- Scenes -------------------------------------------------------------------------------

    // A right-click on a scene row only opens its menu; selecting the row would switch to the scene.
    private void OnSceneTileRightButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    // "New" above the scenes: a menu of ready-made scenes.
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

    // "⋯" on a scene row opens the same menu as a right-click, without switching to the scene.
    private void OnSceneMore(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject d && ItemsControl.ContainerFromElement(SceneList, d) is ListBoxItem { ContextMenu: { } menu } item)
        {
            menu.PlacementTarget = item;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    // "Scene settings…" switches to that scene first: the Scene tab always shows the scene that is on.
    private void OnSceneSettings(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LayoutOption scene })
        {
            _vm.Webcam.SelectedLayout = scene;
        }

        ShowStreamTab(TabScenes);
    }

    private void OnDeleteScene(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LayoutOption scene })
        {
            return;
        }

        var onAir = scene.IsActive ? " It is the scene that is on, so Linkast switches to the one before it first." : string.Empty;
        if (ConfirmDialog.Ask(
            $"Delete \"{scene.Name}\"?",
            "Its layout, background, capture and sound settings are removed. Your cameras and overlays stay available for the other scenes." + onAir,
            "Delete scene",
            kind: ConfirmKind.Destructive))
        {
            _vm.Webcam.DeleteSceneCommand.Execute(scene);
        }
    }

    // ---- Sources ------------------------------------------------------------------------------

    // "Add" above the sources: everything a scene can show, each going to its real place.
    private void OnAddSourceMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = "Camera", Command = _vm.Webcam.AddCameraCommand, ToolTip = "Adds a camera layer (up to 4); choose its device in Webcam Studio" });
        menu.Items.Add(new MenuItem { Header = "Alerts or web page…", Command = _vm.Overlays.AddWebCommand, ToolTip = "The overlay link from your alert service" });
        menu.Items.Add(new MenuItem { Header = "Picture…", Command = _vm.Overlays.AddImageCommand, ToolTip = "A logo or another picture" });
        menu.Items.Add(new Separator());
        var capture = new MenuItem { Header = "Change the game or screen…" };
        capture.Click += (_, _) => ShowStreamTab(TabSource);
        menu.Items.Add(capture);
        var background = new MenuItem { Header = "Background…" };
        background.Click += (_, _) => ShowStreamTab(TabScenes);
        menu.Items.Add(background);
        menu.IsOpen = true;
    }

    private void OnCaptureRow(object sender, RoutedEventArgs e) => ShowStreamTab(TabSource);

    // A camera row selects that camera in the preview; double-click (or its menu) opens its settings.
    private void OnCameraRow(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CameraLayerViewModel layer })
        {
            _vm.Webcam.Selected = layer;
        }
    }

    private void OnCameraRowDoubleClick(object sender, MouseButtonEventArgs e) => OnCameraSettings(sender, e);

    private void OnCameraSettings(object sender, RoutedEventArgs e)
    {
        OnCameraRow(sender, e);
        NavWebcam.IsChecked = true;
        Studio.ShowCamera();
    }

    // Stacking order is the studio's: it moves the selected camera, so select it first.
    private void OnCameraForward(object sender, RoutedEventArgs e)
    {
        OnCameraRow(sender, e);
        _vm.Webcam.MoveSelectedUpCommand.Execute(null);
    }

    private void OnCameraBack(object sender, RoutedEventArgs e)
    {
        OnCameraRow(sender, e);
        _vm.Webcam.MoveSelectedDownCommand.Execute(null);
    }

    private void OnRemoveCamera(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CameraLayerViewModel layer })
        {
            return;
        }

        _vm.Webcam.Selected = layer;
        if (!_vm.Webcam.RemoveCameraCommand.CanExecute(null))
        {
            ConfirmDialog.Inform("Keep at least one camera", "Linkast needs one camera layer. To take it off this scene, use its eye; to stop the camera, turn it off in Webcam Studio.");
            return;
        }

        if (ConfirmDialog.Ask(
            $"Remove \"{layer.Name}\"?",
            "The camera layer and its look are removed from every scene. To hide it in this scene only, use the eye instead.",
            "Remove camera",
            kind: ConfirmKind.Destructive))
        {
            _vm.Webcam.RemoveCameraCommand.Execute(null);
        }
    }

    // An overlay row selects it in the preview (the preview flashes it); double-click opens the Overlays tab.
    private void OnOverlayRow(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OverlayItemViewModel item })
        {
            _vm.Overlays.Selected = item;
            OutputPreview.Locate(item);
        }
    }

    private void OnOverlayRowDoubleClick(object sender, MouseButtonEventArgs e) => OnOverlaySettings(sender, e);

    private void OnOverlaySettings(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OverlayItemViewModel item })
        {
            _vm.Overlays.Selected = item;
        }

        ShowStreamTab(TabOverlays);
    }

    private void OnRemoveOverlay(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OverlayItemViewModel item }
            && ConfirmDialog.Ask(
                $"Remove \"{item.Name}\" from this scene?",
                "If no other scene uses it, its link or picture is deleted too. To hide it for now, use the eye instead.",
                "Remove",
                kind: ConfirmKind.Destructive))
        {
            _vm.Overlays.RemoveCommand.Execute(item);
        }
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

    // Flashes the overlay in the Output preview, even if it was already selected.
    private void OnLocateOverlay(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OverlayItemViewModel item })
        {
            _vm.Overlays.Selected = item;
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

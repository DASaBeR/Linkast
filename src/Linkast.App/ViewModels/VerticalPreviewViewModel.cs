// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linkast.App.Services;
using Linkast.Core.Camera;

namespace Linkast.App.ViewModels;

public enum VerticalPreviewState
{
    Normal,
    NoScene,
    StreamIsVertical,
    Hidden,
    TooSmall,
}

/// <summary>
/// State and editing surface for the editable 9:16 vertical recording preview next to the main preview.
/// </summary>
public sealed partial class VerticalPreviewViewModel : ObservableObject
{
    private readonly WebcamStudioViewModel _webcam;
    private readonly OverlaysViewModel _overlays;
    private readonly Action _scheduleSave;
    private Guid? _sceneId;
    private bool _verticalEnabled;
    private bool _activeIsVertical;
    private int _canvasWidth = 1080;
    private int _canvasHeight = 1920;
    private bool _isRecordingMain;
    private bool _isRecordingVertical;
    private double _previewHostWidth;

    public VerticalPreviewViewModel(
        WebcamStudioViewModel webcam,
        OverlaysViewModel overlays,
        Action scheduleSave)
    {
        _webcam = webcam ?? throw new ArgumentNullException(nameof(webcam));
        _overlays = overlays ?? throw new ArgumentNullException(nameof(overlays));
        _scheduleSave = scheduleSave ?? throw new ArgumentNullException(nameof(scheduleSave));
    }

    public ObservableCollection<VerticalSceneItem> Items { get; } = [];

    public ObservableCollection<VerticalSceneItem> OverlayItems { get; } = [];

    [ObservableProperty]
    public partial ISceneItem? SelectedItem { get; set; }

    partial void OnSelectedItemChanged(ISceneItem? value)
    {
        foreach (var item in Items)
        {
            item.IsSelected = ReferenceEquals(item, value);
        }

        if (value is not null)
        {
            SelectedOverlay = null;
            IsEditing = true;
        }
    }

    [ObservableProperty]
    public partial ISceneItem? SelectedOverlay { get; set; }

    partial void OnSelectedOverlayChanged(ISceneItem? value)
    {
        foreach (var overlay in OverlayItems)
        {
            overlay.IsSelected = ReferenceEquals(overlay, value);
        }

        if (value is not null)
        {
            IsEditing = true;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewHintText))]
    public partial bool IsEditing { get; set; }

    public string PreviewHintText => IsEditing
        ? "Editing the vertical recording · drag to move · corner to resize · Alt + drag to trim"
        : "Drag to move · drag a corner to resize · Alt + drag an edge to trim · the screen picture refreshes every 2 s";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State), nameof(IsVisible), nameof(ShowHiddenRestoreChip), nameof(ShowSideBySidePanel), nameof(ShowFullTooSmallPanel), nameof(ShowTooSmallBar), nameof(ShowNormalEditor), nameof(ShowMainPreview))]
    public partial bool ShowPreview { get; set; } = true;

    partial void OnShowPreviewChanged(bool value) => _scheduleSave();

    [ObservableProperty]
    public partial bool ShowSafeZones { get; set; }

    partial void OnShowSafeZonesChanged(bool value) => _scheduleSave();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State), nameof(ShowMainPreview), nameof(ShowSideBySidePanel), nameof(ShowFullTooSmallPanel), nameof(ShowTooSmallBar), nameof(TooSmallButtonText))]
    public partial bool IsExpandedInSmallWindow { get; set; }

    [ObservableProperty]
    public partial ImageSource? GamePicture { get; set; }

    partial void OnGamePictureChanged(ImageSource? value) => OnPropertyChanged(nameof(BasePlaceholderText));

    [ObservableProperty]
    public partial ImageSource? SceneBackground { get; set; }

    [ObservableProperty]
    public partial string? SceneVideo { get; set; }

    [ObservableProperty]
    public partial string? LiveSourceNote { get; set; }

    public double CanvasWidth => _canvasWidth;

    public double CanvasHeight => _canvasHeight;

    public SceneLayout? Scene => _sceneId is { } id ? _webcam.FindScene(id) : null;

    public bool IsVisible => _verticalEnabled && ShowPreview && !IsTooSmall;

    public bool IsThumbnailMode => _previewHostWidth is >= 700 and < 900;

    public bool IsTooSmall => _previewHostWidth is > 0 and < 700;

    public bool ShowHiddenRestoreChip => _verticalEnabled && !ShowPreview;

    public bool ShowTooSmallBar => _verticalEnabled && ShowPreview && IsTooSmall;

    public string TooSmallButtonText => IsExpandedInSmallWindow
        ? "Back to stream preview"
        : "Vertical preview hidden — window too small";

    public bool ShowSideBySidePanel => _verticalEnabled && ShowPreview && !IsTooSmall;

    public bool ShowFullTooSmallPanel => _verticalEnabled && ShowPreview && IsTooSmall && IsExpandedInSmallWindow;

    public bool ShowPanel => ShowSideBySidePanel || ShowFullTooSmallPanel;

    public bool ShowMainPreview => !ShowFullTooSmallPanel;

    public VerticalPreviewState State
    {
        get
        {
            if (!_verticalEnabled || !ShowPreview)
            {
                return VerticalPreviewState.Hidden;
            }

            if (IsTooSmall && !IsExpandedInSmallWindow)
            {
                return VerticalPreviewState.TooSmall;
            }

            if (_activeIsVertical)
            {
                return VerticalPreviewState.StreamIsVertical;
            }

            return Scene is null ? VerticalPreviewState.NoScene : VerticalPreviewState.Normal;
        }
    }

    public bool ShowNormalEditor => State == VerticalPreviewState.Normal;

    public bool ShowNoSceneCard => State == VerticalPreviewState.NoScene;

    public bool ShowStreamIsVerticalNotice => State == VerticalPreviewState.StreamIsVertical;

    public NormalizedRect BasePlacement
    {
        get => Scene?.BasePlacement ?? NormalizedRect.Full;
        set
        {
            if (_sceneId is { } id)
            {
                _webcam.UpdateScene(id, s => s with { BasePlacement = value.Clamped() });
                OnPropertyChanged();
            }
        }
    }

    public NormalizedRect BaseCrop
    {
        get => Scene?.BaseCrop ?? NormalizedRect.Full;
        set
        {
            if (_sceneId is { } id)
            {
                _webcam.UpdateScene(id, s => s with { BaseCrop = value.Clamped(SceneLayout.MinCrop) });
                OnPropertyChanged();
            }
        }
    }

    public BaseFit BaseFit => Scene?.BaseFit ?? BaseFit.Fill;

    public bool ShowBase => Scene?.ShowCapture ?? false;

    public string? BasePlaceholderText => GamePicture is null && ShowBase ? "Game not running" : null;

    public IReadOnlyList<string> StackOrder
    {
        get
        {
            if (Scene is not { } scene)
            {
                return [];
            }

            var media = SceneMediaRenderer.StreamFile(scene, _canvasWidth, _canvasHeight) is not null
                ? new[] { "scene-" + scene.Id.ToString("N") }
                : Array.Empty<string>();
            return SceneStacking.BuildStack(media, [scene.StackOrder]);
        }
    }

    public string MainChipText => string.Create(CultureInfo.InvariantCulture,
        $"Stream · {_webcam.CanvasWidth:0}×{_webcam.CanvasHeight:0}");

    public string VerticalChipText => Scene is { } s
        ? string.Create(CultureInfo.InvariantCulture, $"Vertical recording · {_canvasWidth}×{_canvasHeight} · {s.Name}")
        : string.Create(CultureInfo.InvariantCulture, $"Vertical recording · {_canvasWidth}×{_canvasHeight}");

    public bool IsMainRecording => _isRecordingMain;

    public bool IsRecording => _isRecordingVertical;

    [RelayCommand]
    private void HidePreview() => ShowPreview = false;

    [RelayCommand]
    private void ShowPreviewAgain() => ShowPreview = true;

    [RelayCommand]
    private void ToggleExpandedSmallPreview()
    {
        if (IsTooSmall)
        {
            IsExpandedInSmallWindow = !IsExpandedInSmallWindow;
            NotifyPanelLayoutChanged();
        }
    }

    [RelayCommand]
    private void ApplyQuickLayout(string? layoutKey)
    {
        if (_sceneId is not { } id || Scene is not { } scene)
        {
            return;
        }

        var (baseRect, camRect) = layoutKey switch
        {
            "CameraTop" => (SceneLayout.VerticalCameraTopBase, SceneLayout.VerticalCameraTopCamera),
            "FullGame" => (SceneLayout.VerticalFullGameBase, SceneLayout.VerticalFullGameCornerCamera),
            _ => (SceneLayout.VerticalGameTopBase, SceneLayout.VerticalGameTopCamera),
        };

        var targetCamera = Items.FirstOrDefault(i => i.IsShown)?.LayerId
            ?? _webcam.Layers.FirstOrDefault()?.LayerId;

        _webcam.UpdateScene(id, s =>
        {
            var next = s with
            {
                ShowCapture = true,
                BaseFit = BaseFit.Fill,
                BasePlacement = baseRect,
            };
            return targetCamera is not null ? next.WithPlacement(targetCamera, camRect) : next;
        });
        IsEditing = true;
    }

    public void UpdatePreviewHostWidth(double width)
    {
        if (double.IsNaN(width) || width <= 0 || Math.Abs(_previewHostWidth - width) < 1)
        {
            return;
        }

        _previewHostWidth = width;
        if (!IsTooSmall && IsExpandedInSmallWindow)
        {
            IsExpandedInSmallWindow = false;
        }

        NotifyPanelLayoutChanged();
    }

    public void RefreshState(
        bool verticalEnabled,
        bool activeIsVertical,
        Guid? verticalSceneId,
        int canvasWidth,
        int canvasHeight,
        bool isRecordingMain,
        bool isRecordingVertical,
        bool rebuildItems = false)
    {
        var sceneChanged = _sceneId != verticalSceneId || _canvasWidth != canvasWidth || _canvasHeight != canvasHeight;
        _verticalEnabled = verticalEnabled;
        _activeIsVertical = activeIsVertical;
        _sceneId = verticalSceneId;
        _canvasWidth = canvasWidth;
        _canvasHeight = canvasHeight;
        _isRecordingMain = isRecordingMain;
        _isRecordingVertical = isRecordingVertical;

        if (rebuildItems || sceneChanged)
        {
            RebuildItems();
        }
        else
        {
            foreach (var item in Items)
            {
                item.NotifySceneUpdated();
            }

            foreach (var ov in OverlayItems)
            {
                ov.NotifySceneUpdated();
            }
        }

        RefreshMedia();
        NotifyAllProperties();
    }

    public void NotifySceneEdited(Guid sceneId)
    {
        if (sceneId != _sceneId)
        {
            return;
        }

        foreach (var item in Items)
        {
            item.NotifySceneUpdated();
        }

        foreach (var ov in OverlayItems)
        {
            ov.NotifySceneUpdated();
        }

        RefreshMedia();
        NotifySceneProperties();
    }

    public void RebuildItems()
    {
        var selectedLayerId = SelectedItem?.LayerId;
        var selectedOverlayId = SelectedOverlay?.LayerId;

        foreach (var old in Items)
        {
            old.Dispose();
        }

        foreach (var old in OverlayItems)
        {
            old.Dispose();
        }

        Items.Clear();
        OverlayItems.Clear();

        foreach (var cam in _webcam.Layers)
        {
            var item = new VerticalSceneItem(
                cam,
                isOverlay: false,
                () => Scene,
                change =>
                {
                    if (_sceneId is { } id)
                    {
                        _webcam.UpdateScene(id, change);
                    }
                },
                () => _canvasWidth,
                () => _canvasHeight);
            Items.Add(item);
        }

        foreach (var overlay in _overlays.Items)
        {
            var item = new VerticalSceneItem(
                overlay,
                isOverlay: true,
                () => Scene,
                change =>
                {
                    if (_sceneId is { } id)
                    {
                        _webcam.UpdateScene(id, change);
                    }
                },
                () => _canvasWidth,
                () => _canvasHeight);
            OverlayItems.Add(item);
        }

        SelectedItem = selectedLayerId is null ? null : Items.FirstOrDefault(i => i.LayerId == selectedLayerId);
        SelectedOverlay = selectedOverlayId is null ? null : OverlayItems.FirstOrDefault(o => o.LayerId == selectedOverlayId);
        NotifySceneProperties();
    }

    private void RefreshMedia()
    {
        var media = Scene?.Media;
        SceneBackground = SceneMediaRenderer.Preview(media, _canvasWidth, _canvasHeight);
        SceneVideo = SceneMediaRenderer.PreviewVideo(media);
    }

    private void NotifyPanelLayoutChanged()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(IsThumbnailMode));
        OnPropertyChanged(nameof(IsTooSmall));
        OnPropertyChanged(nameof(ShowHiddenRestoreChip));
        OnPropertyChanged(nameof(ShowTooSmallBar));
        OnPropertyChanged(nameof(TooSmallButtonText));
        OnPropertyChanged(nameof(ShowSideBySidePanel));
        OnPropertyChanged(nameof(ShowFullTooSmallPanel));
        OnPropertyChanged(nameof(ShowPanel));
        OnPropertyChanged(nameof(ShowMainPreview));
        OnPropertyChanged(nameof(ShowNormalEditor));
        OnPropertyChanged(nameof(ShowNoSceneCard));
        OnPropertyChanged(nameof(ShowStreamIsVerticalNotice));
    }

    private void NotifySceneProperties()
    {
        OnPropertyChanged(nameof(BasePlacement));
        OnPropertyChanged(nameof(BaseCrop));
        OnPropertyChanged(nameof(BaseFit));
        OnPropertyChanged(nameof(ShowBase));
        OnPropertyChanged(nameof(BasePlaceholderText));
        OnPropertyChanged(nameof(StackOrder));
        OnPropertyChanged(nameof(VerticalChipText));
    }

    private void NotifyAllProperties()
    {
        OnPropertyChanged(nameof(CanvasWidth));
        OnPropertyChanged(nameof(CanvasHeight));
        OnPropertyChanged(nameof(MainChipText));
        OnPropertyChanged(nameof(IsMainRecording));
        OnPropertyChanged(nameof(IsRecording));
        NotifySceneProperties();
        NotifyPanelLayoutChanged();
    }
}

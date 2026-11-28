// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    private bool _sameCaptureAsMain = true;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewHintText))]
    public partial bool IsPanningGame { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMainCropFrameVisible))]
    public partial bool ShowCropFrameOnMain { get; set; }

    partial void OnShowCropFrameOnMainChanged(bool value) => _scheduleSave();

    public string PreviewHintText => IsPanningGame
        ? "Panning vertical game view · drag the game picture to slide your FOV · scroll wheel to zoom"
        : IsEditing
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

    partial void OnGamePictureChanged(ImageSource? value)
    {
        OnPropertyChanged(nameof(BasePlaceholderText));
        OnPropertyChanged(nameof(MainCropFrame));
    }

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

        var fullFov = string.Equals(layoutKey, "FullFov", StringComparison.Ordinal);
        var (baseRect, camRect, fit) = layoutKey switch
        {
            "CameraTop" => (SceneLayout.VerticalCameraTopBase, SceneLayout.VerticalCameraTopCamera, BaseFit.Fill),
            "FullGame" => (SceneLayout.VerticalFullGameBase, SceneLayout.VerticalFullGameCornerCamera, BaseFit.Fill),
            "FullFov" => (new NormalizedRect(0, 0.34, 1, 0.32), new NormalizedRect(0.18, 0.03, 0.64, 0.28), BaseFit.Fit),
            _ => (SceneLayout.VerticalGameTopBase, SceneLayout.VerticalGameTopCamera, BaseFit.Fill),
        };

        var targetCamera = Items.FirstOrDefault(i => i.IsShown)?.LayerId
            ?? _webcam.Layers.FirstOrDefault()?.LayerId;

        _webcam.UpdateScene(id, s =>
        {
            var next = s with
            {
                ShowCapture = true,
                BaseFit = fit,
                BasePlacement = baseRect,
                BaseCrop = NormalizedRect.Full,
            };
            return targetCamera is not null && (fullFov || layoutKey is not null) ? next.WithPlacement(targetCamera, camRect) : next;
        });
        IsEditing = true;
    }

    [RelayCommand]
    private void ToggleGameFit()
    {
        if (_sceneId is not { } id)
        {
            return;
        }

        _webcam.UpdateScene(id, s => s with { BaseFit = s.BaseFit == BaseFit.Fit ? BaseFit.Fill : BaseFit.Fit });
        IsEditing = true;
    }

    [RelayCommand]
    private void ResetGameCrop()
    {
        if (_sceneId is not { } id)
        {
            return;
        }

        _webcam.UpdateScene(id, s => s with { BaseCrop = NormalizedRect.Full });
        IsEditing = true;
    }

    public bool IsMainCropFrameVisible =>
        _verticalEnabled && !_activeIsVertical && Scene is { ShowCapture: true } && ShowCropFrameOnMain && _sameCaptureAsMain;

    public double MainCropFrameAspect
    {
        get
        {
            if (Scene is not { } s)
            {
                return 9.0 / 16.0;
            }

            var w = Math.Max(1.0, s.BasePlacement.Width * _canvasWidth);
            var h = Math.Max(1.0, s.BasePlacement.Height * _canvasHeight);
            return Math.Clamp(w / h, 0.1, 10.0);
        }
    }

    public NormalizedRect MainCropFrame
    {
        get
        {
            if (Scene is not { } s)
            {
                return NormalizedRect.Full;
            }

            var (sw, sh) = GamePicture is BitmapSource { PixelWidth: > 0, PixelHeight: > 0 } bmp
                ? (bmp.PixelWidth, bmp.PixelHeight)
                : (_webcam.CanvasWidth, _webcam.CanvasHeight);
            return SceneLayout.VisibleSourceCrop(s.BasePlacement, s.BaseCrop, s.BaseFit, sw, sh, _canvasWidth, _canvasHeight);
        }
        set
        {
            if (_sceneId is not { } id)
            {
                return;
            }

            _webcam.UpdateScene(id, s => s with { BaseCrop = value.Clamped(SceneLayout.MinCrop) });
            IsEditing = true;
            OnPropertyChanged();
        }
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
        bool rebuildItems = false,
        bool sameCaptureAsMain = true)
    {
        var sceneChanged = _sceneId != verticalSceneId || _canvasWidth != canvasWidth || _canvasHeight != canvasHeight;
        _verticalEnabled = verticalEnabled;
        _activeIsVertical = activeIsVertical;
        _sceneId = verticalSceneId;
        _canvasWidth = canvasWidth;
        _canvasHeight = canvasHeight;
        _isRecordingMain = isRecordingMain;
        _isRecordingVertical = isRecordingVertical;
        _sameCaptureAsMain = sameCaptureAsMain;

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
        OnPropertyChanged(nameof(IsMainCropFrameVisible));
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
        OnPropertyChanged(nameof(MainCropFrame));
        OnPropertyChanged(nameof(MainCropFrameAspect));
        OnPropertyChanged(nameof(IsMainCropFrameVisible));
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

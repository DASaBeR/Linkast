// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linkast.App.Services;
using Linkast.Core;
using Linkast.Core.Camera;

namespace Linkast.App.ViewModels;

public sealed partial class MainViewModel
{
    private Guid? _pendingVerticalSceneId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSoftwareExtraOutputWarning))]
    public partial bool VerticalRecordingEnabled { get; set; }

    public ObservableCollection<SceneChoice> VerticalScenes { get; } = [];

    [ObservableProperty]
    public partial SceneChoice? SelectedVerticalScene { get; set; }

    public bool HasVerticalScene => VerticalScenes.Count > 0;

    public IReadOnlyList<VerticalResolutionOption> VerticalSizes => VerticalResolutionOption.All;

    [ObservableProperty]
    public partial VerticalResolutionOption SelectedVerticalSize { get; set; }

    [ObservableProperty]
    public partial int VerticalRecordingBitrateKbps { get; set; } = 10000;

    [RelayCommand]
    private void AddVerticalScene()
    {
        if (Webcam.AddScene(SceneKind.Gaming, OutputOrientation.Vertical, switchTo: false) is { } id)
        {
            SelectedVerticalScene = VerticalScenes.FirstOrDefault(s => s.Id == id) ?? SelectedVerticalScene;
        }
    }

    private void RebuildVerticalScenes(Guid? preferredId)
    {
        var was = _loading;
        _loading = true;
        try
        {
            VerticalScenes.Clear();
            foreach (var scene in Webcam.Layouts.Where(l => l.Orientation == OutputOrientation.Vertical))
            {
                VerticalScenes.Add(new SceneChoice(scene.Id, scene.Name));
            }

            SelectedVerticalScene = VerticalScenes.FirstOrDefault(s => s.Id == preferredId)
                ?? VerticalScenes.FirstOrDefault();
            OnPropertyChanged(nameof(HasVerticalScene));
            NotifyRecordingSummaryChanged();
            RefreshVerticalPreviewState(rebuildItems: true);
        }
        finally
        {
            _loading = was;
        }
    }

    private System.Windows.Threading.DispatcherTimer? _verticalLayoutTimer;
    private long _lastVerticalLayoutSentTicks;
    private bool _verticalSendQueued;

    private void RefreshVerticalPreviewState(bool rebuildItems = false)
    {
        if (Webcam is null || VerticalPreview is null)
        {
            return;
        }

        var vScene = SelectedVerticalScene?.Id is { } vId ? Webcam.FindScene(vId) : null;
        var sameCapture = vScene is not null
            && (SameCaptureTarget(CaptureOf(vScene), CaptureOf(Webcam.ActiveLayout))
                || (Resolve(CaptureOf(vScene))?.Key is { } vk && Resolve(CaptureOf(Webcam.ActiveLayout))?.Key is { } mk && vk == mk));
        VerticalPreview.RefreshState(
            VerticalRecordingEnabled,
            Webcam.ActiveLayout.Orientation == OutputOrientation.Vertical,
            SelectedVerticalScene?.Id,
            SelectedVerticalSize.Width,
            SelectedVerticalSize.Height,
            IsMainRecording,
            IsActive && _liveVerticalSceneId is not null,
            rebuildItems,
            sameCapture);
        UpdateVerticalLiveSourceNote();
    }

    private void OnSceneChangedForVertical(Guid sceneId)
    {
        if (VerticalPreview is null)
        {
            return;
        }

        VerticalPreview.NotifySceneEdited(sceneId);
        UpdateVerticalLiveSourceNote();

        if (!IsActive || State != StreamState.Live || _liveVerticalSceneId != sceneId || _engine is null)
        {
            return;
        }

        QueueLiveVerticalLayout();
    }

    private void UpdateVerticalLiveSourceNote()
    {
        if (VerticalPreview is null)
        {
            return;
        }

        if (!IsActive || _liveVerticalSceneId is null || VerticalPreview.Scene is not { } vScene)
        {
            VerticalPreview.LiveSourceNote = null;
            return;
        }

        foreach (var layerId in vScene.CameraPlacements.Keys)
        {
            if (Webcam.Layers.FirstOrDefault(l => l.LayerId == layerId)?.HasCamera == true && !_liveCameraIds.Contains(layerId))
            {
                VerticalPreview.LiveSourceNote = "This camera joins the vertical recording next time you go live.";
                return;
            }
        }

        foreach (var overlayId in vScene.Overlays.Keys)
        {
            if (!_liveLayerIds.Contains(overlayId))
            {
                VerticalPreview.LiveSourceNote = "This overlay joins the vertical recording next time you go live.";
                return;
            }
        }

        if (vScene.ShowCapture && Resolve(CaptureOf(vScene)) is { } cap && !_liveCaptures.Any(c => c.Key == cap.Key))
        {
            VerticalPreview.LiveSourceNote = "This game or display joins the vertical recording next time you go live.";
            return;
        }

        VerticalPreview.LiveSourceNote = null;
    }

    private void QueueLiveVerticalLayout()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(_lastVerticalLayoutSentTicks, now).TotalMilliseconds;
        if (_lastVerticalLayoutSentTicks == 0 || elapsedMs >= 50)
        {
            _verticalSendQueued = false;
            _verticalLayoutTimer?.Stop();
            SendVerticalLayoutNow();
            return;
        }

        if (!_verticalSendQueued)
        {
            _verticalSendQueued = true;
            _verticalLayoutTimer ??= new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromMilliseconds(50),
                System.Windows.Threading.DispatcherPriority.Background,
                (_, _) =>
                {
                    _verticalLayoutTimer?.Stop();
                    if (_verticalSendQueued)
                    {
                        _verticalSendQueued = false;
                        SendVerticalLayoutNow();
                    }
                },
                _ui);
            _verticalLayoutTimer.Start();
        }
    }

    private void SendVerticalLayoutNow()
    {
        if (!IsActive || State != StreamState.Live || _engine is null || _liveVerticalSceneId is not { } id
            || Webcam.FindScene(id) is not { } scene)
        {
            return;
        }

        _lastVerticalLayoutSentTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        var layout = BuildSceneCanvasLayout(scene, SelectedVerticalSize.Width, SelectedVerticalSize.Height);
        _ = _engine.SetCanvasLayoutAsync("vertical", layout);
    }

    public string RecordingSummary
    {
        get
        {
            if (Webcam is null)
            {
                return string.Empty;
            }

            var layout = Webcam.ActiveLayout;
            var (sw, sh) = layout.CanvasSize(SelectedResolution.Width, SelectedResolution.Height);
            var (rw, rh) = SelectedRecordingQuality.IsCustom
                ? layout.CanvasSize(SelectedRecordingQuality.Width, SelectedRecordingQuality.Height)
                : (sw, sh);
            return FormatLiveRecordingSummary(
                sw, sh,
                RecordWhenLive, rw, rh,
                VerticalRecordingEnabled,
                layout.Orientation == OutputOrientation.Vertical,
                HasVerticalScene,
                SelectedVerticalSize.Width, SelectedVerticalSize.Height);
        }
    }

    public string RecordButtonSummary
    {
        get
        {
            if (Webcam is null)
            {
                return string.Empty;
            }

            var layout = Webcam.ActiveLayout;
            var (sw, sh) = layout.CanvasSize(SelectedResolution.Width, SelectedResolution.Height);
            var (rw, rh) = SelectedRecordingQuality.IsCustom
                ? layout.CanvasSize(SelectedRecordingQuality.Width, SelectedRecordingQuality.Height)
                : (sw, sh);
            return FormatRecordButtonSummary(
                rw, rh,
                VerticalRecordingEnabled,
                layout.Orientation == OutputOrientation.Vertical,
                HasVerticalScene,
                SelectedVerticalSize.Width, SelectedVerticalSize.Height);
        }
    }

    public string RecordButtonTooltip =>
        $"{RecordButtonSummary}. Save to a file without going live. To record streams too, turn on 'Also record when I go live' in the Recording tab.";

    internal void NotifyRecordingSummaryChanged()
    {
        OnPropertyChanged(nameof(RecordingSummary));
        OnPropertyChanged(nameof(RecordButtonSummary));
        OnPropertyChanged(nameof(RecordButtonTooltip));
    }

    internal static string FormatLiveRecordingSummary(
        int streamWidth, int streamHeight,
        bool recordWhenLive, int recordingWidth, int recordingHeight,
        bool verticalEnabled, bool activeIsVertical, bool hasVerticalScene,
        int verticalWidth, int verticalHeight)
    {
        if (!recordWhenLive && !verticalEnabled)
        {
            return "When you go live: stream only (recording is off)";
        }

        var parts = new List<string>(3) { $"stream {streamWidth}×{streamHeight}" };
        if (recordWhenLive)
        {
            parts.Add($"recording {recordingWidth}×{recordingHeight}");
        }

        if (verticalEnabled)
        {
            parts.Add(FormatVerticalPart(activeIsVertical, hasVerticalScene, verticalWidth, verticalHeight));
        }

        return "When you go live: " + string.Join(" · ", parts);
    }

    internal static string FormatRecordButtonSummary(
        int recordingWidth, int recordingHeight,
        bool verticalEnabled, bool activeIsVertical, bool hasVerticalScene,
        int verticalWidth, int verticalHeight)
    {
        var parts = new List<string>(2) { $"{recordingWidth}×{recordingHeight}" };
        if (verticalEnabled)
        {
            parts.Add(FormatVerticalPart(activeIsVertical, hasVerticalScene, verticalWidth, verticalHeight));
        }

        return "Record button: " + string.Join(" · ", parts);
    }

    private static string FormatVerticalPart(bool activeIsVertical, bool hasVerticalScene, int verticalWidth, int verticalHeight) =>
        activeIsVertical ? "vertical recording off for vertical scenes"
        : hasVerticalScene ? $"vertical {verticalWidth}×{verticalHeight}"
        : "vertical (no scene yet)";

    /// <summary>
    /// Builds the vertical recording specification for a landscape stream/recording session, or returns null
    /// when disabled, skipped (active scene is already vertical), or no vertical scene exists.
    /// </summary>
    private VerticalRecordingSpec? BuildVerticalRecording(SceneLayout mainScene, string? preset, out SceneLayout? verticalScene)
    {
        verticalScene = null;
        if (!VerticalRecordingEnabled)
        {
            return null;
        }

        if (mainScene.Orientation == OutputOrientation.Vertical)
        {
            AppendLog("Vertical recording skipped: the stream is already vertical.");
            return null;
        }

        verticalScene = Webcam.Layouts.FirstOrDefault(l => l.Id == SelectedVerticalScene?.Id && l.Orientation == OutputOrientation.Vertical)
            ?? Webcam.Layouts.FirstOrDefault(l => l.Orientation == OutputOrientation.Vertical);
        if (verticalScene is null)
        {
            var notice = "Vertical recording needs a vertical scene. Add one in the scene strip.";
            WarningText = notice;
            AppendLog(notice);
            return null;
        }

        var w = SelectedVerticalSize.Width;
        var h = SelectedVerticalSize.Height;
        var layout = BuildSceneCanvasLayout(verticalScene, w, h);
        var encoder = new EncoderSettings(
            SelectedEncoder!.Id,
            w,
            h,
            SelectedFrameRate,
            VerticalRecordingBitrateKbps,
            KeyframeIntervalSeconds,
            RateControl.VariableBitrate,
            preset,
            AudioBitrateKbps);

        return new VerticalRecordingSpec(RecordingFolder, encoder, layout)
        {
            RemuxToMp4 = RemuxRecordingsToMp4,
        };
    }

    /// <summary>
    /// Computes the normalized placements, crops and stacking order for <paramref name="scene"/> on a canvas of
    /// <paramref name="canvasWidth"/> × <paramref name="canvasHeight"/>, matching the main canvas's key rules.
    /// </summary>
    private CanvasLayout BuildSceneCanvasLayout(SceneLayout scene, int canvasWidth, int canvasHeight)
    {
        var placements = new Dictionary<string, NormalizedRect>(StringComparer.Ordinal);
        var crops = new Dictionary<string, NormalizedRect>(StringComparer.Ordinal);
        var fits = new Dictionary<string, BaseFit>(StringComparer.Ordinal);

        if (scene.ShowCapture && Resolve(CaptureOf(scene)) is { } cap)
        {
            placements[cap.Key] = scene.BasePlacement;
            crops[cap.Key] = scene.BaseCrop;
            fits[cap.Key] = scene.BaseFit;
        }

        foreach (var (layerId, rect) in scene.CameraPlacements)
        {
            if (Webcam.Layers.FirstOrDefault(l => l.LayerId == layerId && l.HasCamera) is { } cam)
            {
                placements[layerId] = rect;
                crops[layerId] = cam.Crop;
            }
        }

        foreach (var (overlayId, setup) in scene.Overlays)
        {
            if (Overlays.Items.FirstOrDefault(o => o.LayerId == overlayId) is not { } item)
            {
                continue;
            }

            if (BuildOverlaySource(item) is null)
            {
                continue;
            }

            var placement = setup.Position == OverlayPosition.Custom && setup.CustomPlacement is { } custom
                ? custom.Clamped()
                : OverlayGeometry.Place(setup.Position, setup.Size, item.Aspect, canvasWidth, canvasHeight);
            placements[overlayId] = placement;
            crops[overlayId] = setup.Crop;
        }

        var mediaIds = new List<string>();
        if (SceneMediaRenderer.StreamFile(scene, canvasWidth, canvasHeight) is not null)
        {
            var mediaId = SceneMediaLayerId(scene.Id);
            placements[mediaId] = NormalizedRect.Full;
            crops[mediaId] = NormalizedRect.Full;
            mediaIds.Add(mediaId);
        }

        var stack = SceneStacking.BuildStack(mediaIds, [ShownOrder(scene)]);
        return new CanvasLayout(placements, crops, stack, fits);
    }

    private static OverlaySource? BuildOverlaySource(OverlayItemViewModel item) => item switch
    {
        { IsImage: true, ImagePath: { Length: > 0 } path } => new ImageOverlaySource(path),
        { IsWeb: true, HasUrl: true } => new WebOverlaySource(new Secret(item.Url!), item.PageWidth, item.PageHeight),
        _ => null,
    };

    /// <summary>
    /// Adds the vertical scene's cameras, overlays and media background to the session hidden on the main
    /// canvas so both canvases share the same underlying sources. Captures are added by <see cref="BuildCaptures"/>.
    /// </summary>
    private void AppendVerticalSources(
        SceneLayout verticalScene,
        VerticalRecordingSpec verticalSpec,
        List<CameraLayerBinding> cameras,
        List<OverlayLayerBinding> overlays,
        List<MediaLayerBinding> media)
    {
        var vw = verticalSpec.Encoder.Width;
        var vh = verticalSpec.Encoder.Height;

        foreach (var (layerId, rect) in verticalScene.CameraPlacements)
        {
            if (cameras.Any(c => string.Equals(c.Layer.LayerId, layerId, StringComparison.Ordinal)))
            {
                continue;
            }

            if (Webcam.Layers.FirstOrDefault(l => l.LayerId == layerId && l.HasCamera) is { } cam)
            {
                cameras.Add(new CameraLayerBinding(cam.Layer, rect, StartHidden: true) { Crop = cam.Crop });
            }
        }

        foreach (var (overlayId, setup) in verticalScene.Overlays)
        {
            if (overlays.Any(o => string.Equals(o.LayerId, overlayId, StringComparison.Ordinal)))
            {
                continue;
            }

            if (Overlays.Items.FirstOrDefault(o => o.LayerId == overlayId) is not { } item
                || BuildOverlaySource(item) is not { } source)
            {
                continue;
            }

            var placement = setup.Position == OverlayPosition.Custom && setup.CustomPlacement is { } custom
                ? custom.Clamped()
                : OverlayGeometry.Place(setup.Position, setup.Size, item.Aspect, vw, vh);
            overlays.Add(new OverlayLayerBinding(overlayId, source, placement, StartHidden: true) { Crop = setup.Crop });
        }

        if (SceneMediaRenderer.StreamFile(verticalScene, vw, vh) is { } vFile)
        {
            var mediaId = SceneMediaLayerId(verticalScene.Id);
            if (!media.Any(m => string.Equals(m.LayerId, mediaId, StringComparison.Ordinal)))
            {
                var channel = vFile.IsVideo ? AudioChannels.ForSceneMedia(verticalScene.Id) : null;
                media.Add(new MediaLayerBinding(mediaId, vFile.Path, vFile.IsVideo, StartHidden: true, channel, 0));
            }
        }
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linkast.Core.Audio;
using Linkast.Core.Camera;
using Linkast.Core.Settings;
using Linkast.Platform.Windows.Camera;

namespace Linkast.App.ViewModels;

public sealed record FormatOption(CameraFormatRequest Request, string Label)
{
    public override string ToString() => Label;
}

public sealed record SwatchOption(string Name, Argb Color)
{
    public SolidColorBrush Brush { get; } = new(System.Windows.Media.Color.FromRgb(Color.R, Color.G, Color.B));

    public override string ToString() => Name;
}

/// <summary>Services and state shared by every camera layer in the studio.</summary>
public sealed class CameraStudioContext
{
    public required ICameraService Cameras { get; init; }

    public required Dispatcher Ui { get; init; }

    public ISegmentationService? Segmentation { get; init; }

    public BackgroundImageCache? Images { get; init; }

    public IFramingService? Framing { get; init; }

    public AudioLevelBus? Audio { get; init; }

    /// <summary>All connected cameras; one list shared by every layer's picker.</summary>
    public ObservableCollection<CameraDevice> Devices { get; } = [];

    public Dictionary<string, CameraLayerSettings> LookByDevice { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, Dictionary<HardwareControl, double?>> HardwareByDevice { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<CameraPreset> CustomPresets { get; } = [];
}

/// <summary>
/// One camera layer: camera selection, live preview, look controls, hardware controls, presets,
/// background effects, auto-framing, border effects and audio-reactive effects. Every control writes
/// straight into <see cref="Layer"/>, which the preview and the stream read each frame, so changes are
/// visible immediately and need no restart. The placement shown here is the one in the active layout.
/// </summary>
public sealed partial class CameraLayerViewModel : ObservableObject, IAsyncDisposable
{
    private const int PreviewMaxWidth = 480;

    private readonly CameraStudioContext _ctx;
    private bool _applying;
    private CancellationTokenSource? _previewCts;
    private Task? _previewTask;
    private ICameraFeed? _previewFeed;

    public CameraLayerViewModel(CameraStudioContext context, string layerId, string name)
    {
        _ctx = context;
        Layer = new LiveCameraLayer(layerId);
        Name = name;
        SelectedFormat = Formats[0];
        SelectedBorderColor = BorderColors[0];
        SelectedIndicatorColor = IndicatorColors[0];
        Layer.Enabled = false;
    }

    /// <summary>The camera layer shared by preview and stream.</summary>
    public LiveCameraLayer Layer { get; }

    public string LayerId => Layer.LayerId;

    /// <summary>Raised when anything worth saving changes.</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>Raised when the placement changes (drag, resize, preset), for live moves while streaming.</summary>
    public event EventHandler<NormalizedRect>? PlacementChanged;

    /// <summary>Raised when the layer's shape changes aspect, so placements in other layouts can follow.</summary>
    public event EventHandler? AspectChanged;

    /// <summary>Raised when this layer changed the shared custom preset list.</summary>
    public event EventHandler? PresetsChanged;

    [ObservableProperty]
    public partial string Name { get; set; }

    partial void OnNameChanged(string value) => Changed();

    /// <summary>True while this layer is the one being edited.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public override string ToString() => Name;

    // ---- Device -------------------------------------------------------------------------------

    public ObservableCollection<CameraDevice> Devices => _ctx.Devices;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCamera))]
    public partial CameraDevice? SelectedDevice { get; set; }

    public bool HasCamera => SelectedDevice is not null;

    public ObservableCollection<FormatOption> Formats { get; } = [new(CameraFormatRequest.Auto, "Automatic (recommended)")];

    [ObservableProperty]
    public partial FormatOption SelectedFormat { get; set; }

    /// <summary>Camera on/off (privacy switch). Off releases the device immediately.</summary>
    [ObservableProperty]
    public partial bool CameraOn { get; set; }

    // ---- Status -------------------------------------------------------------------------------

    [ObservableProperty]
    public partial CameraState State { get; set; } = CameraState.Off;

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Camera off";

    [ObservableProperty]
    public partial string? CameraError { get; set; }

    [ObservableProperty]
    public partial string DiagnosticsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial WriteableBitmap? Preview { get; set; }

    // ---- Look ---------------------------------------------------------------------------------

    public IReadOnlyList<LayerShape> Shapes { get; } = Enum.GetValues<LayerShape>();

    public IReadOnlyList<LayerAspect> Aspects { get; } = Enum.GetValues<LayerAspect>();

    public IReadOnlyList<SwatchOption> BorderColors { get; } =
    [
        new("White", Argb.White),
        new("Purple", Argb.Accent),
        new("Black", new Argb(255, 0, 0, 0)),
        new("Red", new Argb(255, 0xE5, 0x48, 0x4D)),
        new("Green", new Argb(255, 0x30, 0xA4, 0x6C)),
        new("Blue", new Argb(255, 0x00, 0x90, 0xFF)),
        new("Yellow", new Argb(255, 0xF5, 0xC5, 0x18)),
    ];

    [ObservableProperty] public partial LayerShape Shape { get; set; } = CameraLayerSettings.Default.Shape;

    [ObservableProperty] public partial LayerAspect Aspect { get; set; } = CameraLayerSettings.Default.Aspect;

    [ObservableProperty] public partial double CornerRadius { get; set; } = CameraLayerSettings.Default.CornerRadius * 100;

    [ObservableProperty] public partial double Zoom { get; set; } = 1;

    [ObservableProperty] public partial double CenterX { get; set; } = 50;

    [ObservableProperty] public partial double CenterY { get; set; } = 50;

    [ObservableProperty] public partial bool Mirror { get; set; }

    [ObservableProperty] public partial double BorderWidth { get; set; }

    [ObservableProperty] public partial SwatchOption SelectedBorderColor { get; set; }

    [ObservableProperty] public partial double Brightness { get; set; }

    [ObservableProperty] public partial double Contrast { get; set; } = 100;

    [ObservableProperty] public partial double Saturation { get; set; } = 100;

    [ObservableProperty] public partial double Sharpen { get; set; }

    [ObservableProperty] public partial double Denoise { get; set; }

    [ObservableProperty] public partial double LowLightBoost { get; set; }

    partial void OnLowLightBoostChanged(double value) => LookChanged();

    public ObservableCollection<HardwareControlViewModel> HardwareControls { get; } = [];

    [ObservableProperty]
    public partial string HardwareNote { get; set; } = "Turn the camera on to see its hardware controls.";

    // ---- Background effects -------------------------------------------------------------------

    public IReadOnlyList<BackgroundMode> BackgroundModes { get; } = Enum.GetValues<BackgroundMode>();

    public IReadOnlyList<SegmentationQuality> Qualities { get; } = Enum.GetValues<SegmentationQuality>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlurMode), nameof(IsImageMode), nameof(HasBackgroundEffect))]
    public partial BackgroundMode BackgroundMode { get; set; }

    public bool IsBlurMode => BackgroundMode == BackgroundMode.Blur;

    public bool IsImageMode => BackgroundMode == BackgroundMode.Image;

    public bool HasBackgroundEffect => BackgroundMode != BackgroundMode.Original;

    [ObservableProperty] public partial double BlurStrength { get; set; } = 60;

    [ObservableProperty] public partial double EdgeSoftness { get; set; } = 30;

    [ObservableProperty] public partial SegmentationQuality SegmentationQuality { get; set; } = SegmentationQuality.Balanced;

    [ObservableProperty] public partial bool PreferGpu { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundImageName))]
    public partial string? BackgroundImagePath { get; set; }

    public string BackgroundImageName => BackgroundImagePath is null ? "No image chosen" : System.IO.Path.GetFileName(BackgroundImagePath);

    [ObservableProperty]
    public partial string BackgroundStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool BackgroundProblem { get; set; }

    partial void OnBackgroundModeChanged(BackgroundMode value)
    {
        LookChanged();
        UpdateBackgroundStatus(null);
    }

    partial void OnBlurStrengthChanged(double value) => LookChanged();

    partial void OnEdgeSoftnessChanged(double value) => LookChanged();

    partial void OnSegmentationQualityChanged(SegmentationQuality value) => LookChanged();

    partial void OnPreferGpuChanged(bool value) => LookChanged();

    partial void OnBackgroundImagePathChanged(string? value) => LookChanged();

    /// <summary>Copies the chosen image into Linkast's data folder, so moving or deleting the original doesn't break the background.</summary>
    [RelayCommand]
    private void ChooseBackgroundImage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a background image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|All files|*.*",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            // A private copy, so moving or editing the original never breaks the background. The folder is
            // named by content, the file keeps its own name, which is what the panel shows.
            string hash;
            using (var stream = System.IO.File.OpenRead(dialog.FileName))
            {
                hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream))[..16];
            }

            var folder = System.IO.Path.Combine(App.DataDirectory, "backgrounds", hash);
            System.IO.Directory.CreateDirectory(folder);
            var target = System.IO.Path.Combine(folder, System.IO.Path.GetFileName(dialog.FileName));
            if (!System.IO.File.Exists(target))
            {
                System.IO.File.Copy(dialog.FileName, target);
            }

            BackgroundImagePath = target;
            BackgroundMode = BackgroundMode.Image;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            BackgroundProblem = true;
            BackgroundStatus = "That image could not be copied: " + ex.Message;
        }
    }

    private void UpdateBackgroundStatus(SegmentationStatus? status)
    {
        if (BackgroundMode == BackgroundMode.Original)
        {
            BackgroundStatus = "Off";
            BackgroundProblem = false;
            return;
        }

        if (!CameraOn || SelectedDevice is null)
        {
            BackgroundStatus = "Turn the camera on to see the effect.";
            BackgroundProblem = false;
            return;
        }

        var imageNote = BackgroundMode == BackgroundMode.Image && (BackgroundImagePath is null || !System.IO.File.Exists(BackgroundImagePath))
            ? " No image chosen (or it was removed), so blur is used instead."
            : string.Empty;
        (BackgroundStatus, BackgroundProblem) = status switch
        {
            null or { State: SegmentationState.Off } or { State: SegmentationState.Loading } => ("Loading background model…" + imageNote, false),
            { State: SegmentationState.Unavailable } s => (s.Message ?? "Background effects are unavailable.", true),
            { } s => (string.Create(CultureInfo.InvariantCulture,
                $"Running on {s.Provider} · {s.InferenceMs:0} ms per frame · {s.MasksPerSecond:0} masks/s.{(s.Message is null ? string.Empty : " " + s.Message)}{imageNote}"),
                s.Message is not null || imageNote.Length > 0),
        };
    }

    // ---- Auto-framing -------------------------------------------------------------------------

    [ObservableProperty] public partial bool FramingEnabled { get; set; }

    /// <summary>Face height in percent of the picture (15 = wide, 45 = close-up).</summary>
    [ObservableProperty] public partial double FaceSize { get; set; } = FramingSettings.Off.FaceSize * 100;

    [ObservableProperty] public partial double Headroom { get; set; } = FramingSettings.Off.Headroom * 100;

    [ObservableProperty] public partial double MaxZoom { get; set; } = FramingSettings.Off.MaxZoom;

    [ObservableProperty] public partial double FramingSpeed { get; set; } = FramingSettings.Off.Speed * 100;

    [ObservableProperty]
    public partial string FramingStatus { get; set; } = "Off";

    [ObservableProperty]
    public partial bool FramingProblem { get; set; }

    partial void OnFramingEnabledChanged(bool value)
    {
        LookChanged();
        UpdateFramingStatus(false, null);
    }

    partial void OnFaceSizeChanged(double value) => LookChanged();

    partial void OnHeadroomChanged(double value) => LookChanged();

    partial void OnMaxZoomChanged(double value) => LookChanged();

    partial void OnFramingSpeedChanged(double value) => LookChanged();

    private void UpdateFramingStatus(bool tracking, string? problem)
    {
        (FramingStatus, FramingProblem) = (FramingEnabled, CameraOn && SelectedDevice is not null, problem, tracking) switch
        {
            (false, _, _, _) => ("Off", false),
            (true, false, _, _) => ("Turn the camera on to use auto-framing.", false),
            (true, true, { } p, _) => (p, true),
            (true, true, null, true) => ("Following your face.", false),
            _ => ("Looking for a face… The picture stays as it is until one is found.", false),
        };
    }

    // ---- Border effects -----------------------------------------------------------------------

    public IReadOnlyList<BorderAnimation> Animations { get; } = Enum.GetValues<BorderAnimation>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnimation))]
    public partial BorderAnimation BorderAnimation { get; set; }

    public bool HasAnimation => BorderAnimation != BorderAnimation.None;

    [ObservableProperty] public partial double AnimationSpeed { get; set; } = 40;

    [ObservableProperty] public partial double Glow { get; set; }

    partial void OnBorderAnimationChanged(BorderAnimation value) => LookChanged();

    partial void OnAnimationSpeedChanged(double value) => LookChanged();

    partial void OnGlowChanged(double value) => LookChanged();

    // ---- Audio-reactive -----------------------------------------------------------------------

    public IReadOnlyList<AudioReactiveStyle> AudioStyles { get; } = Enum.GetValues<AudioReactiveStyle>();

    public IReadOnlyList<SwatchOption> IndicatorColors { get; } =
    [
        new("Green", new Argb(255, 0x30, 0xD1, 0x58)),
        new("Purple", Argb.Accent),
        new("White", Argb.White),
        new("Red", new Argb(255, 0xE5, 0x48, 0x4D)),
        new("Blue", new Argb(255, 0x00, 0x90, 0xFF)),
        new("Yellow", new Argb(255, 0xF5, 0xC5, 0x18)),
    ];

    [ObservableProperty] public partial bool AudioReactive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesIndicatorColor))]
    public partial AudioReactiveStyle AudioStyle { get; set; }

    public bool UsesIndicatorColor => AudioStyle == AudioReactiveStyle.SpeakingIndicator;

    [ObservableProperty] public partial double ThresholdDb { get; set; } = -40;

    [ObservableProperty] public partial double AudioIntensity { get; set; } = 60;

    [ObservableProperty] public partial SwatchOption SelectedIndicatorColor { get; set; }

    partial void OnAudioReactiveChanged(bool value) => LookChanged();

    partial void OnAudioStyleChanged(AudioReactiveStyle value) => LookChanged();

    partial void OnThresholdDbChanged(double value) => LookChanged();

    partial void OnAudioIntensityChanged(double value) => LookChanged();

    partial void OnSelectedIndicatorColorChanged(SwatchOption value) => LookChanged();

    // ---- Presets ------------------------------------------------------------------------------

    public ObservableCollection<CameraPreset> Presets { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditPreset))]
    public partial CameraPreset? SelectedPreset { get; set; }

    public bool CanEditPreset => SelectedPreset is { IsBuiltIn: false };

    [ObservableProperty]
    public partial string NewPresetName { get; set; } = string.Empty;

    // ---- Placement (in the active layout) -----------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlacementSummary))]
    public partial NormalizedRect Placement { get; set; } = SceneLayout.DefaultLandscape("x").CameraPlacements["x"];

    /// <summary>True when the active layout shows this camera.</summary>
    [ObservableProperty]
    public partial bool IsShown { get; set; } = true;

    /// <summary>Raised when the user shows or hides this camera in the active layout.</summary>
    public event EventHandler? ShownChanged;

    partial void OnIsShownChanged(bool value)
    {
        if (!_applying)
        {
            ShownChanged?.Invoke(this, EventArgs.Empty);
            Changed();
        }
    }

    /// <summary>Output canvas of the active layout, in pixels.</summary>
    public int CanvasWidth { get; private set; } = 1920;

    public int CanvasHeight { get; private set; } = 1080;

    public bool IsLandscapeCanvas => CanvasWidth >= CanvasHeight;

    /// <summary>Position and size in output pixels, for the layout editor.</summary>
    public string PlacementSummary => string.Create(CultureInfo.InvariantCulture,
        $"X {Placement.X * CanvasWidth:0}  ·  Y {Placement.Y * CanvasHeight:0}  ·  {Placement.Width * CanvasWidth:0} × {Placement.Height * CanvasHeight:0}");

    /// <summary>Layer aspect ratio for the editor (circle = 1).</summary>
    public double LayerAspectRatio => Layer.Settings.AspectRatio;

    /// <summary>Called by the studio when the active layout changes. Never raises save or live-move events.</summary>
    public void ApplyLayout(int canvasWidth, int canvasHeight, NormalizedRect? placement)
    {
        var was = _applying;
        _applying = true;
        try
        {
            CanvasWidth = canvasWidth;
            CanvasHeight = canvasHeight;
            IsShown = placement is not null;
            if (placement is { } p)
            {
                Placement = p;
            }
        }
        finally
        {
            _applying = was;
        }

        OnPropertyChanged(nameof(IsLandscapeCanvas));
    }

    // ---- Persistence --------------------------------------------------------------------------

    private string? _pendingDeviceId;
    private CameraFormatRequest? _pendingFormat;

    public void Load(CameraLayerConfig c)
    {
        ArgumentNullException.ThrowIfNull(c);
        _applying = true;
        try
        {
            Name = c.Name;
            RebuildPresets(c.PresetId);
            ApplyLook(c.Look);
            Layer.Format = c.Format;
            _pendingDeviceId = c.DeviceId;
            _pendingFormat = c.Format;
            CameraOn = c.Enabled;
        }
        finally
        {
            _applying = false;
        }

        UpdateLayer();
        Layer.Enabled = CameraOn;
    }

    public CameraLayerConfig ToConfig()
    {
        if (SelectedDevice is { } d)
        {
            _ctx.LookByDevice[d.Id] = Layer.Settings;
        }

        return new CameraLayerConfig
        {
            LayerId = LayerId,
            Name = Name,
            Enabled = CameraOn,
            DeviceId = SelectedDevice?.Id ?? _pendingDeviceId,
            DeviceName = SelectedDevice?.Name,
            Format = SelectedFormat.Request,
            Look = Layer.Settings,
            PresetId = SelectedPreset?.Id,
        };
    }

    /// <summary>Device ID to keep across a device list refresh.</summary>
    internal string? WantedDeviceId => SelectedDevice?.Id ?? _pendingDeviceId;

    /// <summary>Called after the shared device list changed.</summary>
    internal void ReconcileDevice(string? keep, CameraDevice? fallback)
    {
        // Automatic picks keep this layer's own look; only a user's switch restores a device's remembered look.
        var was = _applying;
        _applying = true;
        try
        {
            ReconcileDeviceCore(keep, fallback);
        }
        finally
        {
            _applying = was;
        }
    }

    private void ReconcileDeviceCore(string? keep, CameraDevice? fallback)
    {
        var match = Devices.FirstOrDefault(d => string.Equals(d.Id, keep, StringComparison.OrdinalIgnoreCase));
        if (match is not null && !ReferenceEquals(match, SelectedDevice))
        {
            SelectedDevice = match;
        }
        else if (match is null && keep is null && SelectedDevice is null && fallback is not null)
        {
            SelectedDevice = fallback;
        }
        else if (match is null && keep is not null)
        {
            // Keep remembering the saved camera so it is picked again when it is plugged back in.
            if (SelectedDevice is not null)
            {
                SelectedDevice = null;
            }

            _pendingDeviceId = keep;
            StatusText = "This layer's camera is not connected.";
        }
    }

    // ---- Device handling ----------------------------------------------------------------------

    partial void OnSelectedDeviceChanged(CameraDevice? oldValue, CameraDevice? newValue)
    {
        if (oldValue is not null)
        {
            _ctx.LookByDevice[oldValue.Id] = Layer.Settings;
        }

        if (newValue is not null)
        {
            _pendingDeviceId = null;
            if (!_applying && _ctx.LookByDevice.TryGetValue(newValue.Id, out var look))
            {
                ApplyLook(look);
            }

            _ = LoadFormatsAsync(newValue);
        }

        Layer.Device = newValue;
        RestartPreview();
        Changed();
    }

    private async Task LoadFormatsAsync(CameraDevice device)
    {
        var wanted = _pendingFormat ?? SelectedFormat.Request;
        _pendingFormat = null;
        IReadOnlyList<CameraFormat> formats;
        try
        {
            formats = await _ctx.Cameras.GetFormatsAsync(device.Id);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            formats = [];
        }

        if (SelectedDevice?.Id != device.Id)
        {
            return;
        }

        _applying = true;
        try
        {
            Formats.Clear();
            Formats.Add(new FormatOption(CameraFormatRequest.Auto, "Automatic (recommended)"));
            foreach (var f in formats
                .GroupBy(f => (f.Width, f.Height, Math.Round(f.FrameRate)))
                .Select(g => g.OrderBy(f => f.Subtype is "NV12" or "YUY2" ? 0 : 1).First()))
            {
                Formats.Add(new FormatOption(
                    new CameraFormatRequest(f.Width, f.Height, Math.Round(f.FrameRate, 2), f.Subtype),
                    string.Create(CultureInfo.InvariantCulture, $"{f.Width} × {f.Height} at {f.FrameRate:0.##} fps")));
            }

            SelectedFormat = Formats.FirstOrDefault(o => o.Request == wanted) ?? Formats[0];
        }
        finally
        {
            _applying = false;
        }
    }

    partial void OnSelectedFormatChanged(FormatOption value)
    {
        if (value is null)
        {
            return;
        }

        Layer.Format = value.Request;
        if (!_applying)
        {
            RestartPreview();
            Changed();
        }
    }

    partial void OnCameraOnChanged(bool value)
    {
        Layer.Enabled = value;
        RestartPreview();
        UpdateBackgroundStatus(null);
        UpdateFramingStatus(false, null);
        Changed();
    }

    // ---- Look handling ------------------------------------------------------------------------

    partial void OnShapeChanged(LayerShape value) => LookChanged();

    partial void OnAspectChanged(LayerAspect value) => LookChanged();

    partial void OnCornerRadiusChanged(double value) => LookChanged();

    partial void OnZoomChanged(double value) => LookChanged();

    partial void OnCenterXChanged(double value) => LookChanged();

    partial void OnCenterYChanged(double value) => LookChanged();

    partial void OnMirrorChanged(bool value) => LookChanged();

    partial void OnBorderWidthChanged(double value) => LookChanged();

    partial void OnSelectedBorderColorChanged(SwatchOption value) => LookChanged();

    partial void OnBrightnessChanged(double value) => LookChanged();

    partial void OnContrastChanged(double value) => LookChanged();

    partial void OnSaturationChanged(double value) => LookChanged();

    partial void OnSharpenChanged(double value) => LookChanged();

    partial void OnDenoiseChanged(double value) => LookChanged();

    private void LookChanged()
    {
        if (_applying)
        {
            return;
        }

        var aspectBefore = Layer.Settings.AspectRatio;
        UpdateLayer();
        if (Math.Abs(aspectBefore - Layer.Settings.AspectRatio) > 1e-6)
        {
            // Keep the on-screen width, adapt the height to the new shape.
            Placement = LayerGeometry.FitAspect(Placement, Layer.Settings.AspectRatio, CanvasWidth, CanvasHeight);
            AspectChanged?.Invoke(this, EventArgs.Empty);
        }

        Changed();
    }

    private void UpdateLayer()
    {
        Layer.Settings = new CameraLayerSettings
        {
            Shape = Shape,
            Aspect = Aspect,
            CornerRadius = CornerRadius / 100,
            Zoom = Zoom,
            CenterX = CenterX / 100,
            CenterY = CenterY / 100,
            Mirror = Mirror,
            BorderWidth = BorderWidth / 100,
            BorderColor = SelectedBorderColor?.Color ?? Argb.White,
            Adjustments = new CameraAdjustments
            {
                Brightness = Brightness / 100,
                Contrast = Contrast / 100,
                Saturation = Saturation / 100,
                Sharpen = Sharpen / 100,
                Denoise = Denoise / 100,
                LowLightBoost = LowLightBoost / 100,
            },
            Background = new BackgroundSettings
            {
                Mode = BackgroundMode,
                BlurStrength = BlurStrength / 100,
                EdgeSoftness = EdgeSoftness / 100,
                Quality = SegmentationQuality,
                PreferGpu = PreferGpu,
                ImagePath = BackgroundImagePath,
            },
            Framing = new FramingSettings
            {
                Enabled = FramingEnabled,
                FaceSize = FaceSize / 100,
                Headroom = Headroom / 100,
                MaxZoom = MaxZoom,
                Speed = FramingSpeed / 100,
            },
            Effects = new LayerEffectSettings
            {
                Animation = BorderAnimation,
                AnimationSpeed = AnimationSpeed / 100,
                Glow = Glow / 100,
                Audio = new AudioReactiveSettings
                {
                    Enabled = AudioReactive,
                    Style = AudioStyle,
                    ThresholdDb = ThresholdDb,
                    Intensity = AudioIntensity / 100,
                    IndicatorColor = SelectedIndicatorColor?.Color ?? AudioReactiveSettings.Off.IndicatorColor,
                },
            },
        };
        OnPropertyChanged(nameof(LayerAspectRatio));
    }

    private void ApplyLook(CameraLayerSettings look)
    {
        var wasApplying = _applying;
        _applying = true;
        try
        {
            var l = look.Clamped();
            Shape = l.Shape;
            Aspect = l.Aspect;
            CornerRadius = l.CornerRadius * 100;
            Zoom = l.Zoom;
            CenterX = l.CenterX * 100;
            CenterY = l.CenterY * 100;
            Mirror = l.Mirror;
            BorderWidth = l.BorderWidth * 100;
            SelectedBorderColor = BorderColors.FirstOrDefault(c => c.Color == l.BorderColor)
                ?? new SwatchOption("Custom", l.BorderColor);
            Brightness = l.Adjustments.Brightness * 100;
            Contrast = l.Adjustments.Contrast * 100;
            Saturation = l.Adjustments.Saturation * 100;
            Sharpen = l.Adjustments.Sharpen * 100;
            Denoise = l.Adjustments.Denoise * 100;
            LowLightBoost = l.Adjustments.LowLightBoost * 100;
            BackgroundMode = l.Background.Mode;
            BlurStrength = l.Background.BlurStrength * 100;
            EdgeSoftness = l.Background.EdgeSoftness * 100;
            SegmentationQuality = l.Background.Quality;
            PreferGpu = l.Background.PreferGpu;
            BackgroundImagePath = l.Background.ImagePath;
            FramingEnabled = l.Framing.Enabled;
            FaceSize = l.Framing.FaceSize * 100;
            Headroom = l.Framing.Headroom * 100;
            MaxZoom = l.Framing.MaxZoom;
            FramingSpeed = l.Framing.Speed * 100;
            BorderAnimation = l.Effects.Animation;
            AnimationSpeed = l.Effects.AnimationSpeed * 100;
            Glow = l.Effects.Glow * 100;
            AudioReactive = l.Effects.Audio.Enabled;
            AudioStyle = l.Effects.Audio.Style;
            ThresholdDb = l.Effects.Audio.ThresholdDb;
            AudioIntensity = l.Effects.Audio.Intensity * 100;
            SelectedIndicatorColor = IndicatorColors.FirstOrDefault(c => c.Color == l.Effects.Audio.IndicatorColor)
                ?? new SwatchOption("Custom", l.Effects.Audio.IndicatorColor);
        }
        finally
        {
            _applying = wasApplying;
        }

        UpdateLayer();
    }

    [RelayCommand]
    private void ResetImage()
    {
        var neutral = CameraAdjustments.Neutral;
        _applying = true;
        Brightness = neutral.Brightness * 100;
        Contrast = neutral.Contrast * 100;
        Saturation = neutral.Saturation * 100;
        Sharpen = 0;
        Denoise = 0;
        LowLightBoost = 0;
        _applying = false;
        LookChanged();
    }

    // ---- Presets ------------------------------------------------------------------------------

    partial void OnSelectedPresetChanged(CameraPreset? value)
    {
        if (_applying || value is null)
        {
            return;
        }

        ApplyPreset(value);
    }

    private void ApplyPreset(CameraPreset preset)
    {
        var aspectBefore = Layer.Settings.AspectRatio;
        ApplyLook(preset.Look);

        // Preset positions are designed for landscape; in a vertical layout only the look is applied.
        if (preset.Placement is { } p && IsLandscapeCanvas && IsShown)
        {
            Placement = LayerGeometry.FitAspect(p, Layer.Settings.AspectRatio, CanvasWidth, CanvasHeight);
        }
        else if (Math.Abs(aspectBefore - Layer.Settings.AspectRatio) > 1e-6)
        {
            Placement = LayerGeometry.FitAspect(Placement, Layer.Settings.AspectRatio, CanvasWidth, CanvasHeight);
        }

        if (Math.Abs(aspectBefore - Layer.Settings.AspectRatio) > 1e-6)
        {
            AspectChanged?.Invoke(this, EventArgs.Empty);
        }

        Changed();
    }

    [RelayCommand]
    private void ResetToPreset()
    {
        if (SelectedPreset is { } p)
        {
            ApplyPreset(p);
        }
    }

    [RelayCommand]
    private void SaveAsPreset()
    {
        var name = string.IsNullOrWhiteSpace(NewPresetName) ? "My look" : NewPresetName.Trim();
        var preset = CameraPresets.Duplicate(
            new CameraPreset { Name = name, Look = Layer.Settings, Placement = IsLandscapeCanvas ? Placement : null },
            CameraPresets.All(_ctx.CustomPresets),
            name);
        _ctx.CustomPresets.Add(preset);
        RebuildPresets(preset.Id);
        NewPresetName = string.Empty;
        PresetsChanged?.Invoke(this, EventArgs.Empty);
        Changed();
    }

    [RelayCommand]
    private void UpdatePreset()
    {
        if (SelectedPreset is not { IsBuiltIn: false } p)
        {
            return;
        }

        var i = _ctx.CustomPresets.FindIndex(x => x.Id == p.Id);
        _ctx.CustomPresets[i] = p with { Look = Layer.Settings, Placement = IsLandscapeCanvas ? Placement : p.Placement };
        RebuildPresets(p.Id);
        PresetsChanged?.Invoke(this, EventArgs.Empty);
        Changed();
    }

    [RelayCommand]
    private void DuplicatePreset()
    {
        if (SelectedPreset is not { } p)
        {
            return;
        }

        var copy = CameraPresets.Duplicate(p, CameraPresets.All(_ctx.CustomPresets));
        _ctx.CustomPresets.Add(copy);
        RebuildPresets(copy.Id);
        PresetsChanged?.Invoke(this, EventArgs.Empty);
        Changed();
    }

    [RelayCommand]
    private void RenamePreset()
    {
        if (SelectedPreset is not { IsBuiltIn: false } p || string.IsNullOrWhiteSpace(NewPresetName))
        {
            return;
        }

        var i = _ctx.CustomPresets.FindIndex(x => x.Id == p.Id);
        _ctx.CustomPresets[i] = p with { Name = NewPresetName.Trim() };
        NewPresetName = string.Empty;
        RebuildPresets(p.Id);
        PresetsChanged?.Invoke(this, EventArgs.Empty);
        Changed();
    }

    [RelayCommand]
    private void DeletePreset()
    {
        if (SelectedPreset is not { IsBuiltIn: false } p)
        {
            return;
        }

        _ctx.CustomPresets.RemoveAll(x => x.Id == p.Id);
        RebuildPresets(null);
        PresetsChanged?.Invoke(this, EventArgs.Empty);
        Changed();
    }

    /// <summary>Rebuilds the preset list from the shared custom presets, keeping the selection when it still exists.</summary>
    internal void RebuildPresets(Guid? select)
    {
        var was = _applying;
        _applying = true;
        Presets.Clear();
        foreach (var p in CameraPresets.All(_ctx.CustomPresets))
        {
            Presets.Add(p);
        }

        SelectedPreset = Presets.FirstOrDefault(p => p.Id == select);
        _applying = was;
    }

    internal void RefreshPresets() => RebuildPresets(SelectedPreset?.Id);

    // ---- Placement ----------------------------------------------------------------------------

    partial void OnPlacementChanged(NormalizedRect value)
    {
        if (_applying)
        {
            return;
        }

        PlacementChanged?.Invoke(this, value);
        Changed();
    }

    [RelayCommand]
    private void PlaceAt(string corner)
    {
        var (cw, ch) = (CanvasWidth, CanvasHeight);
        var aspect = Layer.Settings.AspectRatio;
        var p = LayerGeometry.FitAspect(Placement, aspect, cw, ch);
        const double m = 0.025;
        var my = m * cw / ch;
        IsShown = true;
        Placement = corner switch
        {
            "TopLeft" => p with { X = m, Y = my },
            "TopRight" => p with { X = 1 - p.Width - m, Y = my },
            "BottomLeft" => p with { X = m, Y = 1 - p.Height - my },
            "Center" => p with { X = (1 - p.Width) / 2, Y = (1 - p.Height) / 2 },
            "Full" => LayerGeometry.FitAspect(new NormalizedRect(0, 0, 1, 1), aspect, cw, ch) is var f && f.Height <= 1
                ? f with { Y = (1 - f.Height) / 2 }
                : new NormalizedRect((1 - (aspect * ch / cw)) / 2, 0, aspect * ch / cw, 1),
            _ => p with { X = 1 - p.Width - m, Y = 1 - p.Height - my },
        };
    }

    // ---- Preview ------------------------------------------------------------------------------

    private void RestartPreview()
    {
        _ = RestartPreviewAsync();
    }

    private readonly SemaphoreSlim _previewGate = new(1, 1);

    private async Task RestartPreviewAsync()
    {
        // Restarts are serialized so overlapping device/format/on-off changes can never leave two feeds open.
        await _previewGate.WaitAsync();
        try
        {
            await RestartPreviewCoreAsync();
        }
        finally
        {
            _previewGate.Release();
        }
    }

    private async Task RestartPreviewCoreAsync()
    {
        await StopPreviewAsync();
        if (_disposed || !CameraOn || SelectedDevice is not { } device)
        {
            State = CameraState.Off;
            StatusText = SelectedDevice is null ? (_pendingDeviceId is null ? "No camera selected" : "This layer's camera is not connected.") : "Camera off";
            CameraError = null;
            HardwareControls.Clear();
            HardwareNote = "Turn the camera on to see its hardware controls.";
            DiagnosticsText = string.Empty;
            Preview = null;
            return;
        }

        var feed = _ctx.Cameras.Open(device, Layer.Format);
        _previewFeed = feed;
        _feedHandler = (_, _) => OnFeedStateChanged(feed);
        feed.StateChanged += _feedHandler;
        OnFeedStateChanged(feed);
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;
        _previewTask = Task.Run(() => PreviewLoopAsync(feed, token), CancellationToken.None);
    }

    private async Task StopPreviewAsync()
    {
        var feed = _previewFeed;
        _previewFeed = null;
        if (_previewCts is not null)
        {
            await _previewCts.CancelAsync();
        }

        if (_previewTask is not null)
        {
            try
            {
                await _previewTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _previewCts?.Dispose();
        _previewCts = null;
        _previewTask = null;
        if (feed is not null)
        {
            if (_feedHandler is not null)
            {
                feed.StateChanged -= _feedHandler;
                _feedHandler = null;
            }

            await feed.DisposeAsync();
        }
    }

    private EventHandler? _feedHandler;

    private void OnFeedStateChanged(ICameraFeed feed)
    {
        _ctx.Ui.InvokeAsync(() =>
        {
            if (!ReferenceEquals(feed, _previewFeed))
            {
                return;
            }

            State = feed.State;
            CameraError = feed.LastError?.UserMessage;
            StatusText = feed.State switch
            {
                CameraState.Starting => "Starting camera…",
                CameraState.Running => "Camera on",
                CameraState.Unavailable => "Camera unavailable",
                CameraState.Error => "Camera can't be used",
                _ => "Camera off",
            };

            if (feed.State == CameraState.Running)
            {
                RebuildHardwareControls(feed);
            }
        });
    }

    private void RebuildHardwareControls(ICameraFeed feed)
    {
        HardwareControls.Clear();
        _ctx.HardwareByDevice.TryGetValue(feed.Device.Id, out var saved);
        foreach (var info in feed.HardwareControls.Where(c => c.Control is HardwareControl.Exposure or HardwareControl.Focus or HardwareControl.WhiteBalance))
        {
            var vm = new HardwareControlViewModel(info, saved is not null && saved.TryGetValue(info.Control, out var v) ? v : null, (control, value) =>
            {
                if (!_ctx.HardwareByDevice.TryGetValue(feed.Device.Id, out var map))
                {
                    _ctx.HardwareByDevice[feed.Device.Id] = map = [];
                }

                map[control] = value;
                Changed();
                return feed.SetHardwareControlAsync(control, value);
            });
            HardwareControls.Add(vm);
            if (info.Supported && saved is not null && saved.TryGetValue(info.Control, out var savedValue))
            {
                _ = feed.SetHardwareControlAsync(info.Control, savedValue);
            }
        }

        HardwareNote = HardwareControls.Any(h => h.Supported)
            ? "These settings change the camera itself."
            : "This camera does not offer exposure, focus or white balance controls. Use the image adjustments above instead.";
    }

    private async Task PreviewLoopAsync(ICameraFeed feed, CancellationToken ct)
    {
        // The preview subscribes to the same shared workers as the stream: one inference and one face
        // detector per camera, however many consumers there are.
        await using var effects = new LayerEffectsBinding(_ctx.Segmentation, _ctx.Images, _ctx.Framing, _ctx.Audio);
        effects.StatusChanged += (_, _) => _ctx.Ui.InvokeAsync(() => UpdateBackgroundStatus(effects.Status));
        await PreviewLoopCoreAsync(feed, effects, ct);
    }

    private async Task PreviewLoopCoreAsync(ICameraFeed feed, LayerEffectsBinding effects, CancellationToken ct)
    {
        var renderer = new CameraLayerRenderer();
        long last = 0;
        byte[]? buffer = null;
        int w = 0, h = 0;
        var uiBusy = 0;
        var lastDiag = DateTime.MinValue;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(33));
        while (await timer.WaitForNextTickAsync(ct))
        {
            var fx = effects.Update(Layer);
            if (feed.State != CameraState.Running || Volatile.Read(ref uiBusy) != 0)
            {
                continue;
            }

            var frame = feed.Frames.TryBeginRead(last);
            var settings = Layer.Settings;
            var animated = settings.Effects.Animation != BorderAnimation.None || settings.Effects.Audio.Enabled || settings.Framing.Enabled;
            if (frame is null && !animated)
            {
                continue;
            }

            frame ??= feed.Frames.TryBeginRead(0);
            if (frame is null)
            {
                continue;
            }

            try
            {
                var aspect = settings.AspectRatio;
                var pw = PreviewMaxWidth;
                var ph = (int)Math.Round(pw / aspect);
                if (ph > 360)
                {
                    ph = 360;
                    pw = (int)Math.Round(ph * aspect);
                }

                pw -= pw & 1;
                ph -= ph & 1;
                if (buffer is null || pw != w || ph != h)
                {
                    w = pw;
                    h = ph;
                    buffer = new byte[w * h * 4];
                }

                renderer.Render(frame, settings, buffer, w, h, fx.Mask, fx.BackgroundImage, fx.Context, fx.Framing);
                last = frame.Sequence;
            }
            finally
            {
                feed.Frames.EndRead(frame);
            }

            Interlocked.Exchange(ref uiBusy, 1);
            var (bw, bh, data) = (w, h, buffer);
            var diag = DateTime.UtcNow - lastDiag > TimeSpan.FromSeconds(1);
            if (diag)
            {
                lastDiag = DateTime.UtcNow;
            }

            var (tracking, framingProblem) = (effects.FramingTracking, effects.FramingProblem);
            _ = _ctx.Ui.InvokeAsync(() =>
            {
                try
                {
                    if (Preview is null || Preview.PixelWidth != bw || Preview.PixelHeight != bh)
                    {
                        Preview = new WriteableBitmap(bw, bh, 96, 96, PixelFormats.Bgra32, null);
                    }

                    Preview.WritePixels(new System.Windows.Int32Rect(0, 0, bw, bh), data, bw * 4, 0);
                    if (diag)
                    {
                        UpdateFramingStatus(tracking, framingProblem);
                        if (feed.ActiveFormat is { } f)
                        {
                            var model = effects.Status is { State: SegmentationState.Running, Provider: { } provider } ? $" · background model: {provider}" : string.Empty;
                            DiagnosticsText = string.Create(CultureInfo.InvariantCulture,
                                $"{f.Width} × {f.Height} {f.Subtype} · {feed.MeasuredFps:0.0} fps · image processing: {CameraLayerRenderer.ProcessingPath}{model}");
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref uiBusy, 0);
                }
            }, DispatcherPriority.Render, CancellationToken.None);
        }
    }

    private void Changed()
    {
        if (!_applying)
        {
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _previewGate.WaitAsync();
        try
        {
            await StopPreviewAsync();
        }
        finally
        {
            _previewGate.Release();
        }
    }
}

/// <summary>One hardware control (exposure, focus, white balance) with an Auto option.</summary>
public sealed partial class HardwareControlViewModel : ObservableObject
{
    private readonly Func<HardwareControl, double?, Task<bool>> _apply;
    private readonly bool _loading;

    public HardwareControlViewModel(HardwareControlInfo info, double? saved, Func<HardwareControl, double?, Task<bool>> apply)
    {
        Info = info;
        _apply = apply;
        _loading = true;
        Auto = saved is null && info.SupportsAuto;
        Value = saved ?? info.Default;
        _loading = false;
    }

    public HardwareControlInfo Info { get; }

    public string Label => Info.Control switch
    {
        HardwareControl.WhiteBalance => "White balance",
        _ => Info.Control.ToString(),
    };

    public bool Supported => Info.Supported;

    public string UnsupportedReason => Supported ? string.Empty : "Not supported by this camera";

    [ObservableProperty]
    public partial bool Auto { get; set; }

    [ObservableProperty]
    public partial double Value { get; set; }

    partial void OnAutoChanged(bool value)
    {
        if (!_loading)
        {
            _ = _apply(Info.Control, value ? null : Value);
        }
    }

    partial void OnValueChanged(double value)
    {
        if (!_loading && !Auto)
        {
            _ = _apply(Info.Control, value);
        }
    }
}

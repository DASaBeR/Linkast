// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linkast.Core.Audio;
using Linkast.Core.Camera;
using Linkast.Core.Settings;
using Linkast.Platform.Windows.Camera;

namespace Linkast.App.ViewModels;

/// <summary>A layout in the picker. Layouts themselves are immutable records kept by the studio.</summary>
public sealed record LayoutOption(Guid Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Asks whether (and how) the active layout may change. Returns false to keep the current layout.</summary>
public delegate Task<bool> LayoutSwitchHandler(SceneLayout from, SceneLayout to);

/// <summary>
/// Webcam Studio: up to <see cref="CameraLayerConfig.MaxLayers"/> camera layers, each with its own
/// camera and look, plus layouts (landscape and vertical) that store where every camera and the
/// gameplay go. The selected layer is edited in the panels; the editor shows all of them.
/// </summary>
public sealed partial class WebcamStudioViewModel : ObservableObject, IAsyncDisposable
{
    private readonly CameraStudioContext _ctx;
    private readonly List<SceneLayout> _layouts = [];
    private readonly DispatcherTimer _meterTimer;
    private bool _applying;
    private int _landscapeWidth = 1920;
    private int _landscapeHeight = 1080;

    public WebcamStudioViewModel(ICameraService cameras, Dispatcher ui, ISegmentationService? segmentation = null,
        BackgroundImageCache? images = null, IFramingService? framing = null, AudioLevelBus? audio = null)
    {
        _ctx = new CameraStudioContext { Cameras = cameras, Ui = ui, Segmentation = segmentation, Images = images, Framing = framing, Audio = audio };
        cameras.DevicesChanged += (_, _) => ui.InvokeAsync(RefreshDevicesAsync);
        _meterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(66), DispatcherPriority.Background, (_, _) => UpdateMeter(), ui) { IsEnabled = false };
        _layouts.AddRange(new WebcamSettings().Layouts);
    }

    /// <summary>Raised when anything worth saving changes.</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>Raised when a camera moves or is shown/hidden in the active layout, for live changes while streaming.</summary>
    public event EventHandler<CameraLayerViewModel>? LayerPlacementChanged;

    /// <summary>Raised when the set of layers or whether any of them needs the audio level changed.</summary>
    public event EventHandler? LayersChanged;

    /// <summary>Set by the main view model to confirm layout switches while live.</summary>
    public LayoutSwitchHandler? LayoutSwitching { get; set; }

    public ObservableCollection<CameraDevice> Devices => _ctx.Devices;

    public IReadOnlyList<CameraPreset> CustomPresets => _ctx.CustomPresets;

    // ---- Layers -------------------------------------------------------------------------------

    public ObservableCollection<CameraLayerViewModel> Layers { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCameraCommand))]
    public partial CameraLayerViewModel? Selected { get; set; }

    public bool HasSelection => Selected is not null;

    public bool CanAddCamera => Layers.Count < CameraLayerConfig.MaxLayers;

    [ObservableProperty]
    public partial string? Notice { get; set; }

    partial void OnSelectedChanged(CameraLayerViewModel? oldValue, CameraLayerViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }

        if (!_applying)
        {
            Changed();
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddCamera))]
    private void AddCamera()
    {
        if (!CanAddCamera)
        {
            return;
        }

        var n = 1;
        while (Layers.Any(l => l.LayerId == $"camera-{n}"))
        {
            n++;
        }

        var id = $"camera-{n}";
        var layer = CreateLayer(new CameraLayerConfig { LayerId = id, Name = $"Camera {n}", Enabled = true });

        // Pick a camera no other layer uses yet, so a second layer shows something useful straight away.
        var used = Layers.Select(l => l.WantedDeviceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Attach(layer);

        // New cameras appear in every landscape layout (in a free corner) and are hidden in vertical ones.
        for (var i = 0; i < _layouts.Count; i++)
        {
            var layout = _layouts[i];
            if (layout.Orientation != OutputOrientation.Landscape)
            {
                continue;
            }

            var (cw, ch) = layout.CanvasSize(_landscapeWidth, _landscapeHeight);
            _layouts[i] = layout.WithPlacement(id, FreeCorner(layout, layer.LayerAspectRatio, cw, ch));
        }

        ApplyActiveLayoutToLayers();
        layer.ReconcileDevice(null, Devices.FirstOrDefault(d => !used.Contains(d.Id)) ?? Devices.FirstOrDefault());
        Selected = layer;
        OnLayersChanged();
        Changed();
    }

    private bool CanRemoveCamera() => Selected is not null && Layers.Count > 1;

    [RelayCommand(CanExecute = nameof(CanRemoveCamera))]
    private async Task RemoveCameraAsync()
    {
        if (Selected is not { } layer || Layers.Count <= 1)
        {
            return;
        }

        var index = Layers.IndexOf(layer);
        layer.IsShown = false;
        Detach(layer);
        for (var i = 0; i < _layouts.Count; i++)
        {
            _layouts[i] = _layouts[i].Without(layer.LayerId);
        }

        Selected = Layers[Math.Clamp(index, 0, Layers.Count - 1)];
        OnLayersChanged();
        Changed();
        await layer.DisposeAsync();
    }

    [RelayCommand]
    private void MoveSelectedUp() => MoveSelected(+1);

    [RelayCommand]
    private void MoveSelectedDown() => MoveSelected(-1);

    /// <summary>Changes the stacking order (later layers are drawn on top). Applies on the next go-live.</summary>
    private void MoveSelected(int delta)
    {
        if (Selected is not { } layer)
        {
            return;
        }

        var from = Layers.IndexOf(layer);
        var to = Math.Clamp(from + delta, 0, Layers.Count - 1);
        if (from != to)
        {
            Layers.Move(from, to);
            OnLayersChanged();
            Changed();
        }
    }

    private CameraLayerViewModel CreateLayer(CameraLayerConfig config)
    {
        var layer = new CameraLayerViewModel(_ctx, config.LayerId, config.Name);
        layer.Load(config);
        return layer;
    }

    private void Attach(CameraLayerViewModel layer)
    {
        layer.SettingsChanged += OnLayerSettingsChanged;
        layer.PlacementChanged += OnLayerPlacementChanged;
        layer.ShownChanged += OnLayerShownChanged;
        layer.AspectChanged += OnLayerAspectChanged;
        layer.PresetsChanged += OnLayerPresetsChanged;
        Layers.Add(layer);
        AddCameraCommand.NotifyCanExecuteChanged();
        RemoveCameraCommand.NotifyCanExecuteChanged();
    }

    private void Detach(CameraLayerViewModel layer)
    {
        layer.SettingsChanged -= OnLayerSettingsChanged;
        layer.PlacementChanged -= OnLayerPlacementChanged;
        layer.ShownChanged -= OnLayerShownChanged;
        layer.AspectChanged -= OnLayerAspectChanged;
        layer.PresetsChanged -= OnLayerPresetsChanged;
        Layers.Remove(layer);
        AddCameraCommand.NotifyCanExecuteChanged();
        RemoveCameraCommand.NotifyCanExecuteChanged();
    }

    private void OnLayerSettingsChanged(object? sender, EventArgs e)
    {
        OnLayersChanged();
        Changed();
    }

    private void OnLayerPlacementChanged(object? sender, NormalizedRect placement)
    {
        if (sender is not CameraLayerViewModel layer || _applying)
        {
            return;
        }

        var i = ActiveIndex();
        if (layer.IsShown)
        {
            _layouts[i] = _layouts[i].WithPlacement(layer.LayerId, placement);
        }

        LayerPlacementChanged?.Invoke(this, layer);
    }

    private void OnLayerShownChanged(object? sender, EventArgs e)
    {
        if (sender is not CameraLayerViewModel layer || _applying)
        {
            return;
        }

        var i = ActiveIndex();
        _layouts[i] = layer.IsShown ? _layouts[i].WithPlacement(layer.LayerId, layer.Placement) : _layouts[i].Without(layer.LayerId);
        LayerPlacementChanged?.Invoke(this, layer);
    }

    private void OnLayerAspectChanged(object? sender, EventArgs e)
    {
        if (sender is not CameraLayerViewModel layer)
        {
            return;
        }

        // Keep every layout's rectangle in the camera's new shape (width kept, height adapted).
        var active = ActiveIndex();
        for (var i = 0; i < _layouts.Count; i++)
        {
            if (i == active || !_layouts[i].CameraPlacements.TryGetValue(layer.LayerId, out var p))
            {
                continue;
            }

            var (cw, ch) = _layouts[i].CanvasSize(_landscapeWidth, _landscapeHeight);
            _layouts[i] = _layouts[i].WithPlacement(layer.LayerId, LayerGeometry.FitAspect(p, layer.LayerAspectRatio, cw, ch));
        }
    }

    private void OnLayerPresetsChanged(object? sender, EventArgs e)
    {
        foreach (var l in Layers.Where(l => !ReferenceEquals(l, sender)))
        {
            l.RefreshPresets();
        }
    }

    private void OnLayersChanged()
    {
        ReconcileVirtualCameraLayer();
        OnPropertyChanged(nameof(CanAddCamera));
        OnPropertyChanged(nameof(NeedsAudioLevel));
        _meterTimer.IsEnabled = NeedsAudioLevel;
        if (!NeedsAudioLevel)
        {
            AudioLevel = 0;
            Speaking = false;
        }

        LayersChanged?.Invoke(this, EventArgs.Empty);
    }

    private static NormalizedRect FreeCorner(SceneLayout layout, double aspect, int cw, int ch)
    {
        var size = LayerGeometry.DefaultPlacement(aspect, cw, ch);
        const double m = 0.025;
        var my = m * cw / ch;
        NormalizedRect[] corners =
        [
            size,
            size with { X = m },
            size with { X = m, Y = my },
            size with { Y = my },
        ];
        var free = Array.FindIndex(corners, c => !layout.CameraPlacements.Values.Any(p => Overlaps(p, c)));
        return corners[Math.Max(0, free)];

        static bool Overlaps(NormalizedRect a, NormalizedRect b) =>
            a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
    }

    // ---- Layouts ------------------------------------------------------------------------------

    public ObservableCollection<LayoutOption> LayoutOptions { get; } = [];

    [ObservableProperty]
    public partial LayoutOption? SelectedLayout { get; set; }

    public IReadOnlyList<BaseFit> BaseFits { get; } = Enum.GetValues<BaseFit>();

    /// <summary>The layout the editor and the next stream use.</summary>
    public SceneLayout ActiveLayout => _layouts[ActiveIndex()];

    /// <summary>All layouts, as saved.</summary>
    public IReadOnlyList<SceneLayout> Layouts => _layouts;

    public bool IsVertical => ActiveLayout.Orientation == OutputOrientation.Vertical;

    public double CanvasWidth => ActiveLayout.CanvasSize(_landscapeWidth, _landscapeHeight).Width;

    public double CanvasHeight => ActiveLayout.CanvasSize(_landscapeWidth, _landscapeHeight).Height;

    public NormalizedRect BasePlacement => ActiveLayout.BasePlacement;

    public string CanvasDescription => string.Create(CultureInfo.InvariantCulture,
        $"{CanvasWidth:0} × {CanvasHeight:0} {(IsVertical ? "vertical" : "landscape")}");

    [ObservableProperty]
    public partial BaseFit BaseFit { get; set; }

    partial void OnBaseFitChanged(BaseFit value)
    {
        if (_applying)
        {
            return;
        }

        var i = ActiveIndex();
        _layouts[i] = _layouts[i] with { BaseFit = value };
        Changed();
    }

    [ObservableProperty]
    public partial bool SnapToGrid { get; set; } = true;

    partial void OnSnapToGridChanged(bool value) => Changed();

    [ObservableProperty]
    public partial ImageSource? EditorBackground { get; set; }

    private Guid _activeLayoutId = SceneLayout.LandscapeId;
    private bool _switching;

    private int ActiveIndex()
    {
        var i = _layouts.FindIndex(l => l.Id == _activeLayoutId);
        return i < 0 ? 0 : i;
    }

    partial void OnSelectedLayoutChanged(LayoutOption? oldValue, LayoutOption? newValue)
    {
        if (_applying || newValue is null || newValue.Id == _activeLayoutId)
        {
            return;
        }

        _ = SwitchLayoutAsync(oldValue, newValue);
    }

    private async Task SwitchLayoutAsync(LayoutOption? oldValue, LayoutOption newValue)
    {
        var from = ActiveLayout;
        var to = _layouts.First(l => l.Id == newValue.Id);
        if (_switching)
        {
            RevertSelection(oldValue);
            return;
        }

        _switching = true;
        try
        {
            if (LayoutSwitching is { } ask && !await ask(from, to))
            {
                RevertSelection(oldValue);
                return;
            }

            _activeLayoutId = to.Id;
            ApplyActiveLayoutToLayers();
            Changed();
        }
        finally
        {
            _switching = false;
        }
    }

    private void RevertSelection(LayoutOption? option)
    {
        // Selection changes are reverted after the binding finishes, or the ComboBox ignores it.
        _ctx.Ui.BeginInvoke(() =>
        {
            _applying = true;
            SelectedLayout = option ?? LayoutOptions.FirstOrDefault(o => o.Id == _activeLayoutId);
            _applying = false;
        }, DispatcherPriority.Background);
    }

    /// <summary>The landscape output resolution; vertical layouts use it rotated.</summary>
    public void SetResolution(int width, int height)
    {
        if (width == _landscapeWidth && height == _landscapeHeight)
        {
            return;
        }

        _landscapeWidth = width;
        _landscapeHeight = height;
        ApplyActiveLayoutToLayers();
    }

    private void ApplyActiveLayoutToLayers()
    {
        var layout = ActiveLayout;
        var (cw, ch) = layout.CanvasSize(_landscapeWidth, _landscapeHeight);
        var was = _applying;
        _applying = true;
        try
        {
            foreach (var l in Layers)
            {
                l.ApplyLayout(cw, ch, layout.CameraPlacements.TryGetValue(l.LayerId, out var p) ? p : null);
            }

            BaseFit = layout.BaseFit;
            SelectedLayout = LayoutOptions.FirstOrDefault(o => o.Id == layout.Id);
        }
        finally
        {
            _applying = was;
        }

        OnPropertyChanged(nameof(ActiveLayout));
        OnPropertyChanged(nameof(IsVertical));
        OnPropertyChanged(nameof(CanvasWidth));
        OnPropertyChanged(nameof(CanvasHeight));
        OnPropertyChanged(nameof(BasePlacement));
        OnPropertyChanged(nameof(CanvasDescription));
    }

    // ---- Audio level --------------------------------------------------------------------------

    /// <summary>True when any camera that is on uses audio-reactive effects (the microphone is metered then).</summary>
    public bool NeedsAudioLevel => Layers.Any(l => l.CameraOn && l.AudioReactive);

    /// <summary>Voice level 0 to 100 for the meter.</summary>
    [ObservableProperty]
    public partial double AudioLevel { get; set; }

    [ObservableProperty]
    public partial bool Speaking { get; set; }

    [ObservableProperty]
    public partial string? AudioProblem { get; set; }

    private void UpdateMeter()
    {
        if (_ctx.Audio is not { } bus)
        {
            return;
        }

        AudioLevel = bus.Active ? bus.Meter.Level * 100 : 0;
        Speaking = bus.Active && bus.Meter.Speaking;
    }

    // ---- Persistence --------------------------------------------------------------------------

    public void Load(WebcamSettings s, IEnumerable<CameraPreset> customPresets)
    {
        ArgumentNullException.ThrowIfNull(s);
        _applying = true;
        try
        {
            foreach (var kv in s.LookByDevice)
            {
                _ctx.LookByDevice[kv.Key] = kv.Value;
            }

            foreach (var kv in s.HardwareByDevice)
            {
                _ctx.HardwareByDevice[kv.Key] = new Dictionary<HardwareControl, double?>(kv.Value);
            }

            _ctx.CustomPresets.Clear();
            _ctx.CustomPresets.AddRange(customPresets);

            _layouts.Clear();
            _layouts.AddRange(s.Layouts.Count > 0 ? s.Layouts : new WebcamSettings().Layouts);
            LayoutOptions.Clear();
            foreach (var l in _layouts)
            {
                LayoutOptions.Add(new LayoutOption(l.Id, l.Name));
            }

            _activeLayoutId = s.ActiveLayout.Id;
            SnapToGrid = s.SnapToGrid;

            foreach (var config in s.Layers.Take(CameraLayerConfig.MaxLayers))
            {
                Attach(CreateLayer(config));
            }

            if (Layers.Count == 0)
            {
                Attach(CreateLayer(new CameraLayerConfig()));
            }

            Selected = Layers.FirstOrDefault(l => l.LayerId == s.SelectedLayerId) ?? Layers[0];
            LoadVirtualCamera(s.VirtualCamera);

            // Every slot gets the camera's own shape, so the editor, preview and stream agree
            // (for example the default vertical slot holds a round camera as a centred circle).
            for (var i = 0; i < _layouts.Count; i++)
            {
                var (cw, ch) = _layouts[i].CanvasSize(_landscapeWidth, _landscapeHeight);
                foreach (var layer in Layers)
                {
                    if (_layouts[i].CameraPlacements.TryGetValue(layer.LayerId, out var p))
                    {
                        _layouts[i] = _layouts[i].WithPlacement(layer.LayerId, LayerGeometry.FitInside(p, layer.LayerAspectRatio, cw, ch));
                    }
                }
            }
        }
        finally
        {
            _applying = false;
        }

        ApplyActiveLayoutToLayers();
        OnLayersChanged();
    }

    public WebcamSettings ToSettings() => new()
    {
        Layers = [.. Layers.Select(l => l.ToConfig())],
        Layouts = [.. _layouts],
        ActiveLayoutId = _activeLayoutId,
        SelectedLayerId = Selected?.LayerId,
        LookByDevice = new Dictionary<string, CameraLayerSettings>(_ctx.LookByDevice),
        HardwareByDevice = _ctx.HardwareByDevice.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<HardwareControl, double?>)kv.Value),
        SnapToGrid = SnapToGrid,
        VirtualCamera = VirtualCameraToSettings(),
    };

    // ---- Devices ------------------------------------------------------------------------------

    [RelayCommand]
    public async Task RefreshDevicesAsync()
    {
        IReadOnlyList<CameraDevice> devices;
        try
        {
            devices = await _ctx.Cameras.ListAsync();
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            Notice = "Cameras could not be listed: " + ex.Message;
            return;
        }

        var keep = Layers.Select(l => (Layer: l, Id: l.WantedDeviceId)).ToList();

        // Update the shared list in place so pickers bound to unchanged cameras keep their selection.
        foreach (var gone in Devices.Where(d => !devices.Any(n => n.Id == d.Id)).ToList())
        {
            Devices.Remove(gone);
        }

        foreach (var d in devices)
        {
            var i = Devices.ToList().FindIndex(x => x.Id == d.Id);
            if (i < 0)
            {
                Devices.Add(d);
            }
            else if (Devices[i] != d)
            {
                Devices[i] = d;
            }
        }

        foreach (var (layer, id) in keep)
        {
            layer.ReconcileDevice(id, ReferenceEquals(layer, Layers.FirstOrDefault()) ? Devices.FirstOrDefault() : null);
        }
    }

    private void Changed()
    {
        if (!_applying)
        {
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _meterTimer.Stop();
        if (_virtualCamera is not null)
        {
            await _virtualCamera.DisposeAsync();
        }

        foreach (var l in Layers.ToList())
        {
            await l.DisposeAsync();
        }
    }
}

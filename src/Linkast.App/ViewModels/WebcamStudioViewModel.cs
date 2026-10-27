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

/// <summary>
/// A scene in the scene strip and the pickers. The scenes themselves are immutable records kept by the
/// studio; this is what the UI shows of one.
/// </summary>
public sealed partial class LayoutOption(Guid id, string name, SceneKind kind, bool isVertical) : ObservableObject
{
    public Guid Id { get; } = id;

    [ObservableProperty]
    public partial string Name { get; set; } = name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glyph))]
    public partial SceneKind Kind { get; set; } = kind;

    [ObservableProperty]
    public partial bool IsVertical { get; set; } = isVertical;

    /// <summary>1 to 9 for the first nine scenes (their hotkey number), else 0.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNumber))]
    public partial int Number { get; set; }

    public bool HasNumber => Number > 0;

    /// <summary>The scene on stream (or, offline, the one the preview shows).</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>Segoe Fluent icon for the kind of scene.</summary>
    public string Glyph => Kind switch
    {
        SceneKind.StartingSoon => "", // stopwatch
        SceneKind.Gaming => "", // game
        SceneKind.JustChatting => "", // chat
        SceneKind.BeRightBack => "", // pause
        SceneKind.Ending => "", // close/wave
        _ => "", // display
    };

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

    /// <summary>Raised when the gameplay area, its trim or fit, or whether it shows is changed in the active scene (not when a scene is switched).</summary>
    public event EventHandler? BaseChanged;

    /// <summary>
    /// Raised after the active scene changed (a switch) or something in it that the stream shows besides
    /// cameras and the game changed (its background, muted sounds or hidden overlays).
    /// </summary>
    public event EventHandler<SceneLayout>? ActiveSceneChanged;

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

        // New cameras appear in every landscape game scene (in a free corner) and are hidden in vertical
        // ones and in scenes that cover the game (Starting soon, Be right back, Ending, Just chatting).
        for (var i = 0; i < _layouts.Count; i++)
        {
            var layout = _layouts[i];
            if (layout.Orientation != OutputOrientation.Landscape || layout.Kind is not (SceneKind.Gaming or SceneKind.Custom))
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

    /// <summary>Where the gameplay goes in the active layout. Set by the editor when the user drags or resizes it.</summary>
    public NormalizedRect BasePlacement
    {
        get => ActiveLayout.BasePlacement;
        set => SetBase(value.Clamped(), BaseCrop);
    }

    /// <summary>The part of the captured picture shown in the active layout. Set by the editor's Alt-drag.</summary>
    public NormalizedRect BaseCrop
    {
        get => ActiveLayout.BaseCrop;
        set => SetBase(BasePlacement, value.Clamped(SceneLayout.MinCrop));
    }

    private void SetBase(NormalizedRect placement, NormalizedRect crop)
    {
        if (_applying || (placement == BasePlacement && crop == BaseCrop))
        {
            return;
        }

        var i = ActiveIndex();
        _layouts[i] = _layouts[i] with { BasePlacement = placement, BaseCrop = crop };
        OnPropertyChanged(nameof(ActiveLayout));
        OnPropertyChanged(nameof(BasePlacement));
        OnPropertyChanged(nameof(BaseCrop));
        BaseChanged?.Invoke(this, EventArgs.Empty);
        Changed();
    }

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
        BaseChanged?.Invoke(this, EventArgs.Empty);
        Changed();
    }

    /// <summary>The game or screen capture shows in the active scene.</summary>
    public bool ShowCapture
    {
        get => ActiveLayout.ShowCapture;
        set
        {
            if (value == ShowCapture)
            {
                return;
            }

            var i = ActiveIndex();
            _layouts[i] = _layouts[i] with { ShowCapture = value };
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActiveLayout));
            BaseChanged?.Invoke(this, EventArgs.Empty);
            Changed();
        }
    }

    // ---- Scenes -------------------------------------------------------------------------------

    /// <summary>The kinds offered by "Add scene", each with what it starts with.</summary>
    public IReadOnlyList<SceneKind> SceneKinds { get; } =
        [SceneKind.Gaming, SceneKind.JustChatting, SceneKind.StartingSoon, SceneKind.BeRightBack, SceneKind.Ending, SceneKind.Custom];

    public const int MaxScenes = 12;

    public bool CanAddScene => _layouts.Count < MaxScenes;

    public bool CanDeleteScene => _layouts.Count > 1;

    /// <summary>Changes the active scene with <paramref name="change"/> (its background, muted sounds, hidden overlays or name).</summary>
    public void UpdateActiveScene(Func<SceneLayout, SceneLayout> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var i = ActiveIndex();
        var updated = change(_layouts[i]).Clamped() with { Id = _layouts[i].Id };
        if (updated == _layouts[i])
        {
            return;
        }

        _layouts[i] = updated;
        if (LayoutOptions.FirstOrDefault(o => o.Id == updated.Id) is { } option)
        {
            option.Name = updated.Name;
            option.Kind = updated.Kind;
        }

        OnPropertyChanged(nameof(ActiveLayout));
        ActiveSceneChanged?.Invoke(this, updated);
        Changed();
    }

    /// <summary>Adds a ready-made scene of <paramref name="kind"/> after the active one, with the active scene's orientation, and switches to it.</summary>
    [RelayCommand]
    private void AddScene(SceneKind kind)
    {
        if (!CanAddScene)
        {
            return;
        }

        var scene = SceneLayout.Create(kind, Layers.FirstOrDefault()?.LayerId, ActiveLayout.Orientation);
        var (cw, ch) = scene.CanvasSize(_landscapeWidth, _landscapeHeight);
        foreach (var layer in Layers)
        {
            if (scene.CameraPlacements.TryGetValue(layer.LayerId, out var p))
            {
                scene = scene.WithPlacement(layer.LayerId, LayerGeometry.FitInside(p, layer.LayerAspectRatio, cw, ch));
            }
        }

        InsertScene(scene with { Name = UniqueName(scene.Name) });
    }

    /// <summary>A copy of the active scene, placed after it.</summary>
    [RelayCommand]
    private void DuplicateScene()
    {
        if (CanAddScene)
        {
            InsertScene(ActiveLayout with { Id = Guid.NewGuid(), Name = UniqueName(ActiveLayout.Name + " copy") });
        }
    }

    [RelayCommand]
    private async Task DeleteSceneAsync(LayoutOption? option)
    {
        var id = option?.Id ?? _activeLayoutId;
        var index = _layouts.FindIndex(l => l.Id == id);
        if (index < 0 || _layouts.Count <= 1)
        {
            return;
        }

        // Leaving the active scene goes through the normal switch (it may need to ask while live).
        if (id == _activeLayoutId)
        {
            var next = _layouts[index == 0 ? 1 : index - 1];
            if (LayoutSwitching is { } ask && !await ask(_layouts[index], next))
            {
                return;
            }

            _activeLayoutId = next.Id;
            _layouts.RemoveAt(index);
            RebuildSceneOptions();
            ApplyActiveLayoutToLayers();
            ActiveSceneChanged?.Invoke(this, ActiveLayout);
        }
        else
        {
            _layouts.RemoveAt(index);
            RebuildSceneOptions();
        }

        SceneListChanged();
    }

    [RelayCommand]
    private void MoveSceneEarlier(LayoutOption? option) => MoveScene(option, -1);

    [RelayCommand]
    private void MoveSceneLater(LayoutOption? option) => MoveScene(option, +1);

    /// <summary>Switches to the scene with this number (1 = first), as the scene hotkeys do.</summary>
    public void SwitchToNumber(int number)
    {
        if (number >= 1 && number <= LayoutOptions.Count)
        {
            SelectedLayout = LayoutOptions[number - 1];
        }
    }

    /// <summary>Switches to <paramref name="id"/> if it exists. Returns false when there is no such scene.</summary>
    public bool SwitchTo(Guid id)
    {
        if (LayoutOptions.FirstOrDefault(o => o.Id == id) is not { } option)
        {
            return false;
        }

        SelectedLayout = option;
        return true;
    }

    public SceneLayout? FindScene(Guid id) => _layouts.Find(l => l.Id == id);

    private void MoveScene(LayoutOption? option, int delta)
    {
        var from = _layouts.FindIndex(l => l.Id == (option?.Id ?? _activeLayoutId));
        var to = Math.Clamp(from + delta, 0, _layouts.Count - 1);
        if (from < 0 || from == to)
        {
            return;
        }

        var scene = _layouts[from];
        _layouts.RemoveAt(from);
        _layouts.Insert(to, scene);
        RebuildSceneOptions();
        SceneListChanged();
    }

    private void InsertScene(SceneLayout scene)
    {
        _layouts.Insert(ActiveIndex() + 1, scene);
        RebuildSceneOptions();
        SceneListChanged();
        SwitchTo(scene.Id);
    }

    private string UniqueName(string name)
    {
        var candidate = name;
        for (var n = 2; _layouts.Any(l => string.Equals(l.Name, candidate, StringComparison.OrdinalIgnoreCase)); n++)
        {
            candidate = string.Create(CultureInfo.InvariantCulture, $"{name} {n}");
        }

        return candidate;
    }

    /// <summary>Recreates the strip's items from the scenes, keeping the selection.</summary>
    private void RebuildSceneOptions()
    {
        var was = _applying;
        _applying = true;
        try
        {
            LayoutOptions.Clear();
            for (var i = 0; i < _layouts.Count; i++)
            {
                var l = _layouts[i];
                LayoutOptions.Add(new LayoutOption(l.Id, l.Name, l.Kind, l.Orientation == OutputOrientation.Vertical)
                {
                    Number = i < 9 ? i + 1 : 0,
                    IsActive = l.Id == _activeLayoutId,
                });
            }

            SelectedLayout = LayoutOptions.FirstOrDefault(o => o.Id == _activeLayoutId);
        }
        finally
        {
            _applying = was;
        }
    }

    private void SceneListChanged()
    {
        OnPropertyChanged(nameof(CanAddScene));
        OnPropertyChanged(nameof(CanDeleteScene));
        AddSceneCommand.NotifyCanExecuteChanged();
        DuplicateSceneCommand.NotifyCanExecuteChanged();
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
            ActiveSceneChanged?.Invoke(this, ActiveLayout);
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
            foreach (var option in LayoutOptions)
            {
                option.IsActive = option.Id == layout.Id;
            }
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
        OnPropertyChanged(nameof(BaseCrop));
        OnPropertyChanged(nameof(ShowCapture));
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
            _activeLayoutId = s.ActiveLayout.Id;
            RebuildSceneOptions();
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
        SceneListChanged();
        ActiveSceneChanged?.Invoke(this, ActiveLayout);
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

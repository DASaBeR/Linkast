// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linkast.App.Services;
using Linkast.Core;
using Linkast.Core.Camera;
using Linkast.Core.Settings;

namespace Linkast.App.ViewModels;

/// <summary>A change to show on a running stream: a new placement, or null to hide.</summary>
public sealed record OverlayLiveChange(string LayerId, NormalizedRect? Placement);

/// <summary>One picture or web overlay. The editor moves it through <see cref="ISceneItem"/>.</summary>
public sealed partial class OverlayItemViewModel : ObservableObject, ISceneItem
{
    public OverlayItemViewModel(OverlayConfig config)
    {
        LayerId = config.LayerId;
        Kind = config.Kind;
        Name = config.Name;
        CustomPlacement = config.CustomPlacement;
        Position = config.Position;
        Size = config.Size;
        Visible = config.Visible;
        ImagePath = config.ImagePath;
        PageWidth = config.PageWidth;
        PageHeight = config.PageHeight;
    }

    public event EventHandler? Changed;

    /// <summary>Raised when only the picture changed (a web page redrew): the preview updates, nothing is saved.</summary>
    public event EventHandler? PreviewChanged;

    /// <summary>Raised when the user asks to load the web page again.</summary>
    public event EventHandler? ReloadRequested;

    public string LayerId { get; }

    public OverlayKind Kind { get; }

    public bool IsWeb => Kind == OverlayKind.Web;

    public bool IsImage => Kind == OverlayKind.Image;

    /// <summary>Page size for a web overlay, in CSS pixels. Alert services expect 1920 × 1080.</summary>
    [ObservableProperty]
    public partial int PageWidth { get; set; }

    [ObservableProperty]
    public partial int PageHeight { get; set; }

    /// <summary>The web overlay's link. Kept out of settings and logs: it usually holds an account token.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUrl), nameof(IsEditingUrl))]
    public partial string? Url { get; set; }

    /// <summary>What the user is typing in the link box.</summary>
    [ObservableProperty]
    public partial string? UrlDraft { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingUrl))]
    public partial bool ChangingUrl { get; set; }

    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);

    public bool IsEditingUrl => IsWeb && (!HasUrl || ChangingUrl);

    /// <summary>Why a web overlay is not showing, if it isn't.</summary>
    [ObservableProperty]
    public partial string? Problem { get; set; }

    [RelayCommand]
    private void SaveUrl()
    {
        var url = UrlDraft?.Trim();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            Url = url;
            UrlDraft = null;
            ChangingUrl = false;
            Problem = null;
        }
        else
        {
            Problem = "Paste the full link that starts with https://, as your alert service shows it.";
        }
    }

    [RelayCommand]
    private void ChangeUrl() => ChangingUrl = true;

    [RelayCommand]
    private void Reload() => ReloadRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Shows a new picture of the web page in the preview.</summary>
    public void SetSnapshot(BitmapSource? snapshot)
    {
        Bitmap = snapshot;
        PreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSizeAdjustable))]
    public partial OverlayPosition Position { get; set; }

    [ObservableProperty]
    public partial double Size { get; set; }

    /// <summary>Where it was dragged to, used while <see cref="Position"/> is Custom.</summary>
    [ObservableProperty]
    public partial NormalizedRect? CustomPlacement { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShown))]
    public partial bool Visible { get; set; }

    /// <summary>True while this overlay is the one being dragged or resized in the editor.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial string? ImagePath { get; set; }

    /// <summary>The picture, for the thumbnail and the preview. Null when the file is missing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMissing), nameof(Preview))]
    public partial BitmapSource? Bitmap { get; set; }

    public bool IsMissing => IsImage && Bitmap is null;

    public bool IsSizeAdjustable => Position is not (OverlayPosition.FullScreen or OverlayPosition.Custom);

    public bool IsShown => Visible;

    public ImageSource? Preview => Bitmap;

    public double Aspect => IsWeb ? (double)PageWidth / Math.Max(1, PageHeight) : OverlayRenderer.AspectOf(Bitmap);

    /// <summary>The output canvas, so <see cref="Placement"/> can be worked out for the editor.</summary>
    public double CanvasWidth { get; private set; } = 1920;

    public double CanvasHeight { get; private set; } = 1080;

    private bool _placing;

    /// <summary>Where it is on the canvas. Setting it (a drag in the editor) makes the position Custom.</summary>
    public NormalizedRect Placement
    {
        get => PlacementOn(CanvasWidth, CanvasHeight);
        set
        {
            var rect = value.Clamped();
            if (Position == OverlayPosition.Custom && CustomPlacement == rect)
            {
                return;
            }

            // One notification for the pair, not one per property.
            _placing = true;
            try
            {
                CustomPlacement = rect;
                Position = OverlayPosition.Custom;
            }
            finally
            {
                _placing = false;
            }

            PlacementChanged();
        }
    }

    public NormalizedRect PlacementOn(double canvasWidth, double canvasHeight) =>
        Position == OverlayPosition.Custom && CustomPlacement is { } custom
            ? custom.Clamped()
            : OverlayGeometry.Place(Position, Size, Aspect, canvasWidth, canvasHeight);

    /// <summary>Its size and place on the stream in output pixels, for example "480 × 270 at 1400, 40 on the 1920 × 1080 stream".</summary>
    public string PlacementSummary
    {
        get
        {
            var p = Placement;
            return string.Create(CultureInfo.InvariantCulture,
                $"{p.Width * CanvasWidth:0} × {p.Height * CanvasHeight:0} at {p.X * CanvasWidth:0}, {p.Y * CanvasHeight:0} on the {CanvasWidth:0} × {CanvasHeight:0} stream");
        }
    }

    public void SetCanvas(double width, double height)
    {
        (CanvasWidth, CanvasHeight) = (width, height);
        OnPropertyChanged(nameof(Placement));
        OnPropertyChanged(nameof(PlacementSummary));
    }

    /// <summary>Something that moves or reshapes the overlay changed: the editor redraws, and it is saved and applied live.</summary>
    private void PlacementChanged()
    {
        if (_placing)
        {
            return;
        }

        OnPropertyChanged(nameof(Placement));
        OnPropertyChanged(nameof(PlacementSummary));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// A new page size. Like OBS, a dragged overlay keeps its scale, so it grows or shrinks on that side;
    /// one at a fixed spot keeps its spot and takes the new shape.
    /// </summary>
    private void PageSizeChanged(int oldValue, int newValue, bool width)
    {
        if (oldValue is >= MinPageSize and <= MaxPageSize && Position == OverlayPosition.Custom && CustomPlacement is { } c)
        {
            var ratio = (double)newValue / oldValue;
            _placing = true;
            try
            {
                CustomPlacement = (width ? c with { Width = c.Width * ratio } : c with { Height = c.Height * ratio }).Clamped();
            }
            finally
            {
                _placing = false;
            }
        }

        PlacementChanged();
    }

    public const int MinPageSize = 100;

    public const int MaxPageSize = 3840;

    partial void OnImagePathChanged(string? value) => Bitmap = OverlayRenderer.LoadImage(value);

    partial void OnNameChanged(string value) => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>Size an overlay gets when it moves from full screen to one of the nine spots.</summary>
    public const double SpotSize = 0.3;

    partial void OnPositionChanged(OverlayPosition oldValue, OverlayPosition newValue)
    {
        // Leaving full screen (or a drag) for a spot with the size still at about 100 % would keep the
        // overlay covering the whole stream, so the chosen spot would make no difference.
        if (oldValue is OverlayPosition.FullScreen or OverlayPosition.Custom
            && newValue is not (OverlayPosition.FullScreen or OverlayPosition.Custom)
            && Size >= 0.9)
        {
            _placing = true;
            try
            {
                Size = SpotSize;
            }
            finally
            {
                _placing = false;
            }
        }

        PlacementChanged();
    }

    partial void OnSizeChanged(double value) => PlacementChanged();

    partial void OnCustomPlacementChanged(NormalizedRect? value) => PlacementChanged();

    partial void OnVisibleChanged(bool value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnBitmapChanged(BitmapSource? value)
    {
        if (IsImage)
        {
            PlacementChanged(); // a new picture can change the shape
        }
    }

    partial void OnUrlChanged(string? value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnPageWidthChanged(int oldValue, int newValue)
    {
        if (newValue is < MinPageSize or > MaxPageSize)
        {
            PageWidth = Math.Clamp(newValue, MinPageSize, MaxPageSize);
            return;
        }

        PageSizeChanged(oldValue, newValue, width: true);
    }

    partial void OnPageHeightChanged(int oldValue, int newValue)
    {
        if (newValue is < MinPageSize or > MaxPageSize)
        {
            PageHeight = Math.Clamp(newValue, MinPageSize, MaxPageSize);
            return;
        }

        PageSizeChanged(oldValue, newValue, width: false);
    }

    public OverlayConfig ToConfig() => new()
    {
        LayerId = LayerId,
        Name = Name,
        Kind = Kind,
        ImagePath = ImagePath,
        Position = Position,
        PageWidth = PageWidth,
        PageHeight = PageHeight,
        Size = Size,
        CustomPlacement = CustomPlacement,
        Visible = Visible,
    };
}

/// <summary>
/// Overlays on top of the stream (pictures and web alerts). Each scene shows or hides each overlay
/// (<see cref="OverlayItemViewModel.Visible"/> is "shown in the active scene"); position and size are
/// the same in every scene. Showing, hiding, moving and resizing apply to a running stream instantly;
/// adding an overlay needs a restart, which the main view model offers.
/// </summary>
public sealed partial class OverlaysViewModel : ObservableObject
{
    private readonly DpapiSecretStore? _secrets;
    private readonly Dictionary<string, string?> _savedUrls = new(StringComparer.Ordinal);
    private bool _loading;
    private bool _applyingScene;
    private double _canvasWidth = 1920;
    private double _canvasHeight = 1080;

    /// <param name="secrets">Where web overlay links are kept (encrypted for this Windows account).</param>
    public OverlaysViewModel(DpapiSecretStore? secrets = null)
    {
        _secrets = secrets;
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanAdd));
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(HasWebItems));
            AddImageCommand.NotifyCanExecuteChanged();
            AddWebCommand.NotifyCanExecuteChanged();
            ItemsChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>Raised when the user asks a web overlay to load again.</summary>
    public event EventHandler<OverlayItemViewModel>? ReloadRequested;

    /// <summary>Raised when something to save changed.</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>Raised when an overlay should move, appear or disappear on a running stream.</summary>
    public event EventHandler<OverlayLiveChange>? LiveChanged;

    /// <summary>Raised when the user shows or hides an overlay (in the active scene), not when a scene is applied.</summary>
    public event EventHandler<OverlayItemViewModel>? VisibilityChanged;

    /// <summary>Raised when overlays are added or removed, or a web overlay's link changes (the mixer has a fader per web overlay).</summary>
    public event EventHandler? ItemsChanged;

    public ObservableCollection<OverlayItemViewModel> Items { get; } = [];

    public bool CanAdd => Items.Count < OverlayConfig.MaxOverlays;

    public bool HasItems => Items.Count > 0;

    public bool HasWebItems => Items.Any(o => o.IsWeb);

    public IReadOnlyList<OverlayPosition> Positions { get; } = Enum.GetValues<OverlayPosition>();

    /// <summary>The overlay being dragged or resized in the editor, if any. Cameras keep their own selection.</summary>
    [ObservableProperty]
    public partial OverlayItemViewModel? Selected { get; set; }

    partial void OnSelectedChanged(OverlayItemViewModel? oldValue, OverlayItemViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }
    }

    // ---- Overlays ---------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddImage()
    {
        if (PickImage() is not { } path)
        {
            return;
        }

        var item = new OverlayItemViewModel(new OverlayConfig
        {
            LayerId = NewId(),
            Name = Path.GetFileNameWithoutExtension(path),
            Kind = OverlayKind.Image,
            Position = OverlayPosition.TopRight,
            Size = 0.15,
        });
        item.ImagePath = path;
        Attach(item);
        Items.Add(item);
        Selected = item;
        Changed(item, liveRelevant: true); // while live, this is where a restart is offered
    }

    /// <summary>
    /// Alerts, a chat box or any web page with a transparent background. Starts like a new OBS browser
    /// source: an 800 × 600 page at its own size, here centred, so it is easy to see and then place.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddWeb()
    {
        const int pageWidth = 800, pageHeight = 600;
        var item = new OverlayItemViewModel(new OverlayConfig
        {
            LayerId = NewId(),
            Name = "Alerts",
            Kind = OverlayKind.Web,
            PageWidth = pageWidth,
            PageHeight = pageHeight,
            Position = OverlayPosition.Center,
            Size = Math.Round(Math.Clamp(pageWidth / Math.Max(1, _canvasWidth), 0.05, 1), 2),
        });
        Attach(item);
        Items.Add(item);
        Selected = item;
        Changed(item, liveRelevant: false); // nothing to show until the link is saved
    }

    [RelayCommand]
    private void Remove(OverlayItemViewModel item)
    {
        if (Items.Remove(item))
        {
            if (ReferenceEquals(Selected, item))
            {
                Selected = null;
            }

            item.Changed -= OnItemChanged;
            if (item.IsWeb)
            {
                _secrets?.Set(UrlSecretName(item.LayerId), null);
                _savedUrls.Remove(item.LayerId);
            }

            LiveChanged?.Invoke(this, new OverlayLiveChange(item.LayerId, null));
            Changed(null, liveRelevant: false);
        }
    }

    [RelayCommand]
    private void ChangeImage(OverlayItemViewModel item)
    {
        if (PickImage() is { } path)
        {
            item.ImagePath = path;
        }
    }

    // ---- Scenes, stream and preview -----------------------------------------------------------

    /// <summary>
    /// Shows exactly the overlays <paramref name="scene"/> shows. While live, each one that changes is
    /// shown or hidden in the stream at once (through <see cref="LiveChanged"/>).
    /// </summary>
    public void ApplyScene(SceneLayout scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        _applyingScene = true;
        try
        {
            foreach (var o in Items)
            {
                o.Visible = scene.Shows(o.LayerId);
            }
        }
        finally
        {
            _applyingScene = false;
        }
    }

    /// <summary>Called when the output canvas changes size or orientation.</summary>
    public void SetCanvas(double width, double height)
    {
        (_canvasWidth, _canvasHeight) = (width, height);
        foreach (var o in Items)
        {
            o.SetCanvas(width, height);
        }
    }

    /// <summary>
    /// The overlays for a new stream, bottom to top. Hidden overlays are included off the canvas so they
    /// can be shown live (by a scene switch) without a restart.
    /// </summary>
    public IReadOnlyList<OverlayLayerBinding> Bindings(double canvasWidth, double canvasHeight)
    {
        var list = new List<OverlayLayerBinding>();
        foreach (var o in Items)
        {
            OverlaySource? source = o switch
            {
                { IsImage: true, ImagePath: { Length: > 0 } path } => new ImageOverlaySource(path),
                { IsWeb: true, HasUrl: true } => new WebOverlaySource(new Secret(o.Url!), o.PageWidth, o.PageHeight),
                _ => null,
            };
            if (source is not null)
            {
                list.Add(new OverlayLayerBinding(o.LayerId, source, o.PlacementOn(canvasWidth, canvasHeight), StartHidden: !o.Visible));
            }
        }

        return list;
    }

    public void Load(OverlaySettings settings)
    {
        _loading = true;
        try
        {
            Selected = null;
            foreach (var old in Items)
            {
                old.Changed -= OnItemChanged;
            }

            Items.Clear();
            foreach (var config in settings.Items)
            {
                var item = new OverlayItemViewModel(config);
                if (item.IsImage)
                {
                    item.Bitmap = OverlayRenderer.LoadImage(config.ImagePath);
                }
                else if (_secrets?.Get(UrlSecretName(config.LayerId)) is { } url)
                {
                    Redactor.Shared.Register(url);
                    item.Url = url.Reveal();
                    _savedUrls[config.LayerId] = item.Url;
                }

                Attach(item);
                Items.Add(item);
            }
        }
        finally
        {
            _loading = false;
        }
    }

    public OverlaySettings ToSettings()
    {
        // Links go to the encrypted secret store, only when they change.
        foreach (var web in Items.Where(o => o.IsWeb))
        {
            if (!_savedUrls.TryGetValue(web.LayerId, out var saved) || saved != web.Url)
            {
                var secret = string.IsNullOrWhiteSpace(web.Url) ? null : new Secret(web.Url);
                if (secret is not null)
                {
                    Redactor.Shared.Register(secret);
                }

                _secrets?.Set(UrlSecretName(web.LayerId), secret);
                _savedUrls[web.LayerId] = web.Url;
            }
        }

        // Showing and hiding is stored per scene; the overlay itself is always on.
        return new() { Items = [.. Items.Select(o => o.ToConfig() with { Visible = true })] };
    }

    private void Attach(OverlayItemViewModel item)
    {
        item.SetCanvas(_canvasWidth, _canvasHeight);
        item.Changed += OnItemChanged;
        item.ReloadRequested += (_, _) => ReloadRequested?.Invoke(this, item);
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OverlayItemViewModel.Visible) && !_applyingScene && !_loading)
            {
                VisibilityChanged?.Invoke(this, item);
            }
            else if (e.PropertyName == nameof(OverlayItemViewModel.Url))
            {
                ItemsChanged?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    private string NewId() =>
        Enumerable.Range(1, OverlayConfig.MaxOverlays + Items.Count + 1).Select(i => $"overlay-{i}").First(i => Items.All(o => o.LayerId != i));

    private static string UrlSecretName(string layerId) => "overlay.url." + layerId;

    private void OnItemChanged(object? sender, EventArgs e) => Changed(sender as OverlayItemViewModel, liveRelevant: true);

    private void Changed(OverlayItemViewModel? item, bool liveRelevant)
    {
        if (_loading)
        {
            return;
        }

        if (!_applyingScene)
        {
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }

        if (liveRelevant && item is not null)
        {
            LiveChanged?.Invoke(this, new OverlayLiveChange(item.LayerId, item.Visible ? item.PlacementOn(_canvasWidth, _canvasHeight) : null));
        }
    }

    /// <summary>Asks for a picture file (overlays and scene backgrounds).</summary>
    public static string? PickImage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a picture",
            Filter = "Pictures (*.png;*.jpg;*.jpeg;*.gif;*.bmp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>Asks for a video file that a scene plays on a loop.</summary>
    public static string? PickVideo()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a video to loop",
            Filter = "Videos (*.mp4;*.mkv;*.mov;*.webm;*.avi)|*.mp4;*.mkv;*.mov;*.webm;*.avi",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}

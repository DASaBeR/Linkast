// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linkast.App.Services;
using Linkast.Core.Camera;
using Linkast.Core.Settings;

namespace Linkast.App.ViewModels;

/// <summary>An overlay as the output preview draws it.</summary>
public sealed record OverlayPreview(ImageSource Image, NormalizedRect Placement);

/// <summary>A change to show on a running stream: a new placement, or null to hide.</summary>
public sealed record OverlayLiveChange(string LayerId, NormalizedRect? Placement);

/// <summary>One picture (or, later, web) overlay.</summary>
public sealed partial class OverlayItemViewModel : ObservableObject
{
    public OverlayItemViewModel(OverlayConfig config)
    {
        LayerId = config.LayerId;
        Kind = config.Kind;
        Name = config.Name;
        Position = config.Position;
        Size = config.Size;
        Visible = config.Visible;
        ImagePath = config.ImagePath;
    }

    public event EventHandler? Changed;

    public string LayerId { get; }

    public OverlayKind Kind { get; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSizeAdjustable))]
    public partial OverlayPosition Position { get; set; }

    [ObservableProperty]
    public partial double Size { get; set; }

    [ObservableProperty]
    public partial bool Visible { get; set; }

    [ObservableProperty]
    public partial string? ImagePath { get; set; }

    /// <summary>The picture, for the thumbnail and the preview. Null when the file is missing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMissing))]
    public partial BitmapSource? Bitmap { get; set; }

    public bool IsMissing => Bitmap is null;

    public bool IsSizeAdjustable => Position != OverlayPosition.FullScreen;

    public double Aspect => OverlayRenderer.AspectOf(Bitmap);

    public NormalizedRect PlacementOn(double canvasWidth, double canvasHeight) =>
        OverlayGeometry.Place(Position, Size, Aspect, canvasWidth, canvasHeight);

    partial void OnImagePathChanged(string? value) => Bitmap = OverlayRenderer.LoadImage(value);

    partial void OnNameChanged(string value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnPositionChanged(OverlayPosition value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnSizeChanged(double value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnVisibleChanged(bool value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnBitmapChanged(BitmapSource? value) => Changed?.Invoke(this, EventArgs.Empty);

    public OverlayConfig ToConfig() => new()
    {
        LayerId = LayerId,
        Name = Name,
        Kind = Kind,
        ImagePath = ImagePath,
        Position = Position,
        Size = Size,
        Visible = Visible,
    };
}

/// <summary>
/// Overlays on top of the stream (pictures now, web alerts later) and the "Be right back" screen.
/// Visibility, position and size apply to a running stream instantly; adding an overlay needs the next go-live.
/// </summary>
public sealed partial class OverlaysViewModel : ObservableObject
{
    public const string AwayLayerId = "away-screen";

    private bool _loading;
    private double _canvasWidth = 1920;
    private double _canvasHeight = 1080;

    public OverlaysViewModel()
    {
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanAdd));
            OnPropertyChanged(nameof(HasItems));
            AddImageCommand.NotifyCanExecuteChanged();
        };
    }

    /// <summary>Raised when something to save changed.</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>Raised when an overlay or the away screen should move, appear or disappear on a running stream.</summary>
    public event EventHandler<OverlayLiveChange>? LiveChanged;

    public ObservableCollection<OverlayItemViewModel> Items { get; } = [];

    public bool CanAdd => Items.Count < OverlayConfig.MaxOverlays;

    public bool HasItems => Items.Count > 0;

    public IReadOnlyList<OverlayPosition> Positions { get; } = Enum.GetValues<OverlayPosition>();

    /// <summary>What the output preview draws above the cameras.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<OverlayPreview> PreviewItems { get; set; } = [];

    // ---- Overlays ---------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddImage()
    {
        if (PickImage() is not { } path)
        {
            return;
        }

        var id = Enumerable.Range(1, OverlayConfig.MaxOverlays + Items.Count).Select(i => $"overlay-{i}").First(i => Items.All(o => o.LayerId != i));
        var item = new OverlayItemViewModel(new OverlayConfig
        {
            LayerId = id,
            Name = Path.GetFileNameWithoutExtension(path),
            Kind = OverlayKind.Image,
            Position = OverlayPosition.TopRight,
            Size = 0.15,
        });
        item.ImagePath = path;
        Attach(item);
        Items.Add(item);
        Changed(item, liveRelevant: false);
    }

    [RelayCommand]
    private void Remove(OverlayItemViewModel item)
    {
        if (Items.Remove(item))
        {
            item.Changed -= OnItemChanged;
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

    // ---- Be right back ------------------------------------------------------------------------

    /// <summary>The away screen covers the stream (game, cameras and overlays) without stopping it.</summary>
    [ObservableProperty]
    public partial bool IsAway { get; set; }

    [ObservableProperty]
    public partial string AwayTitle { get; set; } = "Be right back";

    [ObservableProperty]
    public partial string? AwaySubtitle { get; set; } = "Stay tuned.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAwayImage))]
    public partial string? AwayImagePath { get; set; }

    [ObservableProperty]
    public partial bool AwayMuteSystemAudio { get; set; } = true;

    /// <summary>A small picture of the away screen for the settings card.</summary>
    [ObservableProperty]
    public partial ImageSource? AwayThumbnail { get; set; }

    public bool HasAwayImage => !string.IsNullOrWhiteSpace(AwayImagePath);

    [RelayCommand]
    private void ToggleAway() => IsAway = !IsAway;

    [RelayCommand]
    private void ChooseAwayImage()
    {
        if (PickImage() is { } path)
        {
            AwayImagePath = path;
        }
    }

    [RelayCommand]
    private void UseAwayCard() => AwayImagePath = null;

    partial void OnIsAwayChanged(bool value)
    {
        LiveChanged?.Invoke(this, new OverlayLiveChange(AwayLayerId, value ? new NormalizedRect(0, 0, 1, 1) : null));
        RefreshPreview();
    }

    partial void OnAwayTitleChanged(string value) => AwayLooksChanged();

    partial void OnAwaySubtitleChanged(string? value) => AwayLooksChanged();

    partial void OnAwayImagePathChanged(string? value) => AwayLooksChanged();

    partial void OnAwayMuteSystemAudioChanged(bool value) => Changed(null, liveRelevant: false);

    private void AwayLooksChanged()
    {
        AwayThumbnail = RenderAway(384, 216);
        RefreshPreview();
        Changed(null, liveRelevant: false);
    }

    // ---- Stream and preview -------------------------------------------------------------------

    /// <summary>Called when the output canvas changes size or orientation.</summary>
    public void SetCanvas(double width, double height)
    {
        (_canvasWidth, _canvasHeight) = (width, height);
        RefreshPreview();
    }

    /// <summary>
    /// The overlays for a new stream, bottom to top, with the away screen last so it covers everything.
    /// Hidden overlays are included off the canvas so they can be shown live without a restart.
    /// </summary>
    public IReadOnlyList<OverlayLayerBinding> Bindings(double canvasWidth, double canvasHeight)
    {
        var list = Items
            .Where(o => o.Kind == OverlayKind.Image && !string.IsNullOrWhiteSpace(o.ImagePath))
            .Select(o => new OverlayLayerBinding(o.LayerId, new ImageOverlaySource(o.ImagePath!), o.PlacementOn(canvasWidth, canvasHeight), StartHidden: !o.Visible))
            .ToList();
        list.Add(new OverlayLayerBinding(
            AwayLayerId,
            new AwayScreenSource(AwayTitle, AwaySubtitle, AwayImagePath),
            new NormalizedRect(0, 0, 1, 1),
            StartHidden: !IsAway));
        return list;
    }

    public void Load(OverlaySettings settings)
    {
        _loading = true;
        try
        {
            foreach (var old in Items)
            {
                old.Changed -= OnItemChanged;
            }

            Items.Clear();
            foreach (var config in settings.Items)
            {
                var item = new OverlayItemViewModel(config);
                item.Bitmap = OverlayRenderer.LoadImage(config.ImagePath);
                Attach(item);
                Items.Add(item);
            }

            AwayTitle = settings.Away.Title;
            AwaySubtitle = settings.Away.Subtitle;
            AwayImagePath = settings.Away.ImagePath;
            AwayMuteSystemAudio = settings.Away.MuteSystemAudio;
        }
        finally
        {
            _loading = false;
        }

        AwayThumbnail = RenderAway(384, 216);
        RefreshPreview();
    }

    public OverlaySettings ToSettings() => new()
    {
        Items = [.. Items.Select(o => o.ToConfig())],
        Away = new AwayScreenSettings
        {
            Title = AwayTitle,
            Subtitle = AwaySubtitle,
            ImagePath = AwayImagePath,
            MuteSystemAudio = AwayMuteSystemAudio,
        },
    };

    private void Attach(OverlayItemViewModel item) => item.Changed += OnItemChanged;

    private void OnItemChanged(object? sender, EventArgs e) => Changed(sender as OverlayItemViewModel, liveRelevant: true);

    private void Changed(OverlayItemViewModel? item, bool liveRelevant)
    {
        if (_loading)
        {
            return;
        }

        RefreshPreview();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        if (liveRelevant && item is not null)
        {
            LiveChanged?.Invoke(this, new OverlayLiveChange(item.LayerId, item.Visible ? item.PlacementOn(_canvasWidth, _canvasHeight) : null));
        }
    }

    private void RefreshPreview()
    {
        var list = Items
            .Where(o => o.Visible && o.Bitmap is not null)
            .Select(o => new OverlayPreview(o.Bitmap!, o.PlacementOn(_canvasWidth, _canvasHeight)))
            .ToList();
        if (IsAway)
        {
            // A small rendering is plenty for the preview.
            var (w, h) = _canvasWidth >= _canvasHeight ? (480, (int)Math.Round(480 * _canvasHeight / _canvasWidth)) : ((int)Math.Round(480 * _canvasWidth / _canvasHeight), 480);
            list.Add(new OverlayPreview(RenderAway(Math.Max(w, 2), Math.Max(h, 2)), new NormalizedRect(0, 0, 1, 1)));
        }

        PreviewItems = list;
    }

    private BitmapSource RenderAway(int width, int height)
    {
        if (OverlayRenderer.LoadImage(AwayImagePath) is { } picture)
        {
            var frame = OverlayRenderer.ToLayerFrame(picture, width, height, cover: true);
            var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, frame, width * 4);
            bmp.Freeze();
            return bmp;
        }

        return OverlayRenderer.RenderAwayCard(width, height, AwayTitle, AwaySubtitle);
    }

    private static string? PickImage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a picture",
            Filter = "Pictures (*.png;*.jpg;*.jpeg;*.gif;*.bmp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}

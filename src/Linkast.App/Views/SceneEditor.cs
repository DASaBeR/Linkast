// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Linkast.App.ViewModels;
using Linkast.Core.Camera;

namespace Linkast.App.Views;

/// <summary>
/// Layout editor for the gameplay area, the camera layers of the active layout and the overlays. Works
/// on a logical canvas the size of the output (1920×1080, or 1080×1920 for vertical) scaled by a Viewbox,
/// so editor coordinates are output pixels: what you place here is exactly what streams. Click an item
/// to select it, drag to move, drag a corner to resize (aspect kept), arrow keys nudge; snapping to a
/// grid, the edges and the centre with visible guides. The gameplay area also has edge handles: a plain
/// drag resizes that side, an Alt-drag trims the captured picture on that side (a crop, as in OBS).
/// Cameras are drawn in stream order (later ones on top), overlays above them, the away screen on top.
/// In preview mode (the Stream page) only the selected or hovered item shows an outline and handles.
/// </summary>
public sealed class SceneEditor : UserControl
{
    private const double Handle = 56;
    private const double SnapDistance = 18;
    private const double MinSize = 80;

    // Stacking: the gameplay area, cameras, overlays, the away screen, then the snap guides.
    private const int ZBase = 0;
    private const int ZCameras = 1;
    private const int ZOverlays = 3;
    private const int ZDecorations = 5;
    private const int ZGuides = 6;

    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x91, 0x46, 0xFF));
    private static readonly Brush Faint = new SolidColorBrush(Color.FromArgb(200, 0xFF, 0xFF, 0xFF));

    public static readonly DependencyProperty LayersProperty = DependencyProperty.Register(
        nameof(Layers), typeof(IEnumerable), typeof(SceneEditor), new PropertyMetadata(null, (d, e) => ((SceneEditor)d).OnLayersChanged(e.NewValue as IEnumerable)));

    public static readonly DependencyProperty SelectedLayerProperty = DependencyProperty.Register(
        nameof(SelectedLayer), typeof(CameraLayerViewModel), typeof(SceneEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((SceneEditor)d).OnSelectedLayerChanged(e.NewValue)));

    /// <summary>The overlays (editable): moved and resized like cameras, drawn above them.</summary>
    public static readonly DependencyProperty OverlaysProperty = DependencyProperty.Register(
        nameof(Overlays), typeof(IEnumerable), typeof(SceneEditor), new PropertyMetadata(null, (d, e) => ((SceneEditor)d).OnOverlaysChanged(e.NewValue as IEnumerable)));

    public static readonly DependencyProperty SelectedOverlayProperty = DependencyProperty.Register(
        nameof(SelectedOverlay), typeof(OverlayItemViewModel), typeof(SceneEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((SceneEditor)d).OnSelectedOverlayChanged(e.NewValue)));

    public static readonly DependencyProperty CanvasWidthProperty = DependencyProperty.Register(
        nameof(CanvasWidth), typeof(double), typeof(SceneEditor), new PropertyMetadata(1920.0, (d, _) => ((SceneEditor)d).OnCanvasSize()));

    public static readonly DependencyProperty CanvasHeightProperty = DependencyProperty.Register(
        nameof(CanvasHeight), typeof(double), typeof(SceneEditor), new PropertyMetadata(1080.0, (d, _) => ((SceneEditor)d).OnCanvasSize()));

    public static readonly DependencyProperty BasePlacementProperty = DependencyProperty.Register(
        nameof(BasePlacement), typeof(NormalizedRect), typeof(SceneEditor),
        new FrameworkPropertyMetadata(new NormalizedRect(0, 0, 1, 1), FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((SceneEditor)d)._base.Refresh()));

    public static readonly DependencyProperty BaseFitProperty = DependencyProperty.Register(
        nameof(BaseFit), typeof(BaseFit), typeof(SceneEditor), new PropertyMetadata(BaseFit.Fit, (d, _) => ((SceneEditor)d)._base.Refresh()));

    /// <summary>The part of the captured picture that is shown (fractions of it); Alt-drag on an edge of the gameplay area changes it.</summary>
    public static readonly DependencyProperty BaseCropProperty = DependencyProperty.Register(
        nameof(BaseCrop), typeof(NormalizedRect), typeof(SceneEditor),
        new FrameworkPropertyMetadata(new NormalizedRect(0, 0, 1, 1), FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((SceneEditor)d)._base.Refresh()));

    public static readonly DependencyProperty BackgroundSourceProperty = DependencyProperty.Register(
        nameof(BackgroundSource), typeof(ImageSource), typeof(SceneEditor), new PropertyMetadata(null, (d, e) => ((SceneEditor)d)._base.SetPicture((ImageSource?)e.NewValue)));

    public static readonly DependencyProperty SnapProperty = DependencyProperty.Register(
        nameof(Snap), typeof(bool), typeof(SceneEditor), new PropertyMetadata(true));

    /// <summary>Pictures drawn above everything, read-only (the away screen while it is on).</summary>
    public static readonly DependencyProperty OverlayItemsProperty = DependencyProperty.Register(
        nameof(OverlayItems), typeof(IEnumerable), typeof(SceneEditor), new PropertyMetadata(null, (d, _) => ((SceneEditor)d).RefreshDecorations()));

    /// <summary>Draws the scene as the stream will: no outlines, labels or placeholders except on the selected or hovered item.</summary>
    public static readonly DependencyProperty IsPreviewOnlyProperty = DependencyProperty.Register(
        nameof(IsPreviewOnly), typeof(bool), typeof(SceneEditor), new PropertyMetadata(false, (d, _) => ((SceneEditor)d).RefreshAll()));

    private readonly Canvas _canvas = new() { ClipToBounds = true, Background = new SolidColorBrush(Color.FromRgb(24, 24, 28)) };
    private readonly Line _vGuide = Guide();
    private readonly Line _hGuide = Guide();
    private readonly List<Image> _decorations = [];
    private readonly List<LayerVisual> _cameras = [];
    private readonly List<LayerVisual> _overlays = [];
    private readonly BaseVisual _base;
    private readonly Viewbox _viewbox;
    private INotifyCollectionChanged? _observedLayers;
    private INotifyCollectionChanged? _observedOverlays;
    private bool _baseSelected;

    public SceneEditor()
    {
        _base = new BaseVisual(this);
        _canvas.Children.Add(_base.Root);
        _canvas.Children.Add(_vGuide);
        _canvas.Children.Add(_hGuide);
        Panel.SetZIndex(_base.Root, ZBase);
        Panel.SetZIndex(_vGuide, ZGuides);
        Panel.SetZIndex(_hGuide, ZGuides);
        _viewbox = new Viewbox { Stretch = Stretch.Uniform, Child = _canvas };
        Content = _viewbox;
        OnCanvasSize();
        Loaded += (_, _) => RefreshAll();
    }

    public IEnumerable? Layers
    {
        get => (IEnumerable?)GetValue(LayersProperty);
        set => SetValue(LayersProperty, value);
    }

    public CameraLayerViewModel? SelectedLayer
    {
        get => (CameraLayerViewModel?)GetValue(SelectedLayerProperty);
        set => SetValue(SelectedLayerProperty, value);
    }

    public IEnumerable? Overlays
    {
        get => (IEnumerable?)GetValue(OverlaysProperty);
        set => SetValue(OverlaysProperty, value);
    }

    public OverlayItemViewModel? SelectedOverlay
    {
        get => (OverlayItemViewModel?)GetValue(SelectedOverlayProperty);
        set => SetValue(SelectedOverlayProperty, value);
    }

    public double CanvasWidth
    {
        get => (double)GetValue(CanvasWidthProperty);
        set => SetValue(CanvasWidthProperty, value);
    }

    public double CanvasHeight
    {
        get => (double)GetValue(CanvasHeightProperty);
        set => SetValue(CanvasHeightProperty, value);
    }

    public NormalizedRect BasePlacement
    {
        get => (NormalizedRect)GetValue(BasePlacementProperty);
        set => SetValue(BasePlacementProperty, value);
    }

    public BaseFit BaseFit
    {
        get => (BaseFit)GetValue(BaseFitProperty);
        set => SetValue(BaseFitProperty, value);
    }

    public NormalizedRect BaseCrop
    {
        get => (NormalizedRect)GetValue(BaseCropProperty);
        set => SetValue(BaseCropProperty, value);
    }

    public ImageSource? BackgroundSource
    {
        get => (ImageSource?)GetValue(BackgroundSourceProperty);
        set => SetValue(BackgroundSourceProperty, value);
    }

    public bool Snap
    {
        get => (bool)GetValue(SnapProperty);
        set => SetValue(SnapProperty, value);
    }

    public bool IsPreviewOnly
    {
        get => (bool)GetValue(IsPreviewOnlyProperty);
        set => SetValue(IsPreviewOnlyProperty, value);
    }

    public IEnumerable? OverlayItems
    {
        get => (IEnumerable?)GetValue(OverlayItemsProperty);
        set => SetValue(OverlayItemsProperty, value);
    }

    private double GridStep => Math.Max(CanvasWidth, CanvasHeight) / 48;

    /// <summary>True while the gameplay area is the selected thing (its handles show, cameras hide theirs).</summary>
    internal bool IsBaseSelected => _baseSelected;

    /// <summary>Cameras show handles only while neither an overlay nor the gameplay area is selected.</summary>
    internal bool CameraHandlesAllowed => SelectedOverlay is null && !_baseSelected;

    private static Line Guide() => new()
    {
        Stroke = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)),
        StrokeThickness = 4,
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
    };

    private void OnCanvasSize()
    {
        _canvas.Width = CanvasWidth;
        _canvas.Height = CanvasHeight;

        // A vertical canvas is shown at a sensible height rather than filling the panel width.
        _viewbox.MaxHeight = CanvasHeight > CanvasWidth ? 520 : double.PositiveInfinity;
        RefreshAll();
    }

    private void OnLayersChanged(IEnumerable? value)
    {
        Observe(ref _observedLayers, value, OnLayersCollectionChanged);
        Rebuild(_cameras, value, ZCameras);
    }

    private void OnOverlaysChanged(IEnumerable? value)
    {
        Observe(ref _observedOverlays, value, OnOverlaysCollectionChanged);
        Rebuild(_overlays, value, ZOverlays);
    }

    private void Observe(ref INotifyCollectionChanged? observed, IEnumerable? value, NotifyCollectionChangedEventHandler handler)
    {
        if (observed is not null)
        {
            observed.CollectionChanged -= handler;
        }

        observed = value as INotifyCollectionChanged;
        if (observed is not null)
        {
            observed.CollectionChanged += handler;
        }
    }

    private void OnLayersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild(_cameras, Layers, ZCameras);

    private void OnOverlaysCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild(_overlays, Overlays, ZOverlays);

    private void OnSelectedLayerChanged(object? value)
    {
        // Picked from the layer list: the camera takes over from whatever else was selected.
        if (value is not null)
        {
            _baseSelected = false;
            SelectedOverlay = null;
        }

        RefreshAll();
    }

    private void OnSelectedOverlayChanged(object? value)
    {
        if (value is not null)
        {
            _baseSelected = false;
        }

        RefreshAll();
    }

    /// <summary>Recreates one group of visuals (cameras or overlays) in the collection's order, which is the stacking order.</summary>
    private void Rebuild(List<LayerVisual> visuals, IEnumerable? source, int zBase)
    {
        foreach (var v in visuals)
        {
            v.Detach();
            _canvas.Children.Remove(v.Root);
        }

        visuals.Clear();
        if (source is not null)
        {
            foreach (var item in source.OfType<ISceneItem>())
            {
                var visual = new LayerVisual(this, item, zBase);
                visuals.Add(visual);
                _canvas.Children.Add(visual.Root);
            }
        }

        RefreshAll();
    }

    private void RefreshAll()
    {
        _base.Refresh();
        foreach (var v in _cameras)
        {
            v.Refresh();
        }

        foreach (var v in _overlays)
        {
            v.Refresh();
        }

        RefreshDecorations();
    }

    /// <summary>Redraws the read-only pictures (the away screen) above every camera and overlay and below the snap guides.</summary>
    private void RefreshDecorations()
    {
        foreach (var image in _decorations)
        {
            _canvas.Children.Remove(image);
        }

        _decorations.Clear();
        if (OverlayItems is null)
        {
            return;
        }

        foreach (var item in OverlayItems.OfType<OverlayPreview>())
        {
            var r = item.Placement;
            var image = new Image
            {
                Source = item.Image,
                Stretch = Stretch.Uniform,
                Width = r.Width * CanvasWidth,
                Height = r.Height * CanvasHeight,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(image, r.X * CanvasWidth);
            Canvas.SetTop(image, r.Y * CanvasHeight);
            Panel.SetZIndex(image, ZDecorations);
            _decorations.Add(image);
            _canvas.Children.Add(image);
        }
    }

    internal void Select(ISceneItem item)
    {
        _baseSelected = false;
        switch (item)
        {
            case CameraLayerViewModel camera:
                SelectedOverlay = null;
                if (!ReferenceEquals(SelectedLayer, camera))
                {
                    SelectedLayer = camera;
                }

                break;
            case OverlayItemViewModel overlay:
                if (!ReferenceEquals(SelectedOverlay, overlay))
                {
                    SelectedOverlay = overlay;
                }

                break;
        }

        RefreshAll();
    }

    internal void SelectBase()
    {
        _baseSelected = true;
        SelectedOverlay = null;
        RefreshAll();
    }

    internal void ShowGuides(double? x, double? y)
    {
        _vGuide.Visibility = x is null ? Visibility.Collapsed : Visibility.Visible;
        _hGuide.Visibility = y is null ? Visibility.Collapsed : Visibility.Visible;
        if (x is { } gx)
        {
            _vGuide.X1 = _vGuide.X2 = gx;
            _vGuide.Y1 = 0;
            _vGuide.Y2 = CanvasHeight;
        }

        if (y is { } gy)
        {
            _hGuide.Y1 = _hGuide.Y2 = gy;
            _hGuide.X1 = 0;
            _hGuide.X2 = CanvasWidth;
        }
    }

    internal double SnapAxis(double pos, double size, double extent, out double? guide)
    {
        guide = null;
        if (!Snap)
        {
            return pos;
        }

        var margin = 0.025 * Math.Max(CanvasWidth, CanvasHeight);
        (double Target, double Line)[] candidates =
        [
            (0, 0),
            (margin, margin),
            (extent - size, extent),
            (extent - size - margin, extent - margin),
            ((extent - size) / 2, extent / 2),
        ];
        foreach (var (target, line) in candidates)
        {
            if (Math.Abs(pos - target) <= SnapDistance)
            {
                guide = line;
                return target;
            }
        }

        return Math.Round(pos / GridStep) * GridStep;
    }

    /// <summary>Moves a rectangle by a drag delta with snapping, showing the guides.</summary>
    private Rect Moved(Rect r, double dx, double dy)
    {
        r.X += dx;
        r.Y += dy;
        r.X = SnapAxis(r.X, r.Width, CanvasWidth, out var gx);
        r.Y = SnapAxis(r.Y, r.Height, CanvasHeight, out var gy);
        ShowGuides(gx, gy);
        return r;
    }

    /// <summary>
    /// Resizes a rectangle from one corner, keeping its aspect ratio and the opposite corner in place.
    /// Thumb deltas are incremental because the handle moves with the corner.
    /// </summary>
    private Rect ResizedFromCorner(Rect r, bool left, bool top, double dx)
    {
        var (cw, ch) = (CanvasWidth, CanvasHeight);
        var aspect = r.Height > 0 ? r.Width / r.Height : 16.0 / 9;
        var width = Math.Clamp(r.Width + (left ? -dx : dx), MinSize, cw);
        var height = width / aspect;
        if (height > ch)
        {
            height = ch;
            width = height * aspect;
        }

        var anchorX = left ? r.Right : r.X;
        var anchorY = top ? r.Bottom : r.Y;
        return new Rect(left ? anchorX - width : anchorX, top ? anchorY - height : anchorY, width, height);
    }

    private Rect Nudged(Rect r, Key key)
    {
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? GridStep : 4;
        switch (key)
        {
            case Key.Left: r.X -= step; break;
            case Key.Right: r.X += step; break;
            case Key.Up: r.Y -= step; break;
            case Key.Down: r.Y += step; break;
        }

        return r;
    }

    private NormalizedRect Normalized(Rect r) =>
        new NormalizedRect(r.X / CanvasWidth, r.Y / CanvasHeight, r.Width / CanvasWidth, r.Height / CanvasHeight).Clamped();

    private static Thumb RoundThumb(HorizontalAlignment h, VerticalAlignment v, Cursor cursor) => new()
    {
        Width = Handle,
        Height = Handle,
        HorizontalAlignment = h,
        VerticalAlignment = v,
        Margin = new Thickness(-Handle / 2),
        Cursor = cursor,
        Background = Brushes.White,
        BorderBrush = Accent,
        BorderThickness = new Thickness(6),
        Template = ThumbTemplate(Handle / 2),
    };

    /// <summary>A bar along one side of the gameplay area, between the corner handles.</summary>
    private static Thumb EdgeThumb(Side side)
    {
        var vertical = side is Side.Left or Side.Right;
        var thickness = Handle / 3;
        return new Thumb
        {
            Width = vertical ? thickness : double.NaN,
            Height = vertical ? double.NaN : thickness,
            HorizontalAlignment = side switch { Side.Left => HorizontalAlignment.Left, Side.Right => HorizontalAlignment.Right, _ => HorizontalAlignment.Stretch },
            VerticalAlignment = side switch { Side.Top => VerticalAlignment.Top, Side.Bottom => VerticalAlignment.Bottom, _ => VerticalAlignment.Stretch },
            Margin = vertical ? new Thickness(-thickness / 2, Handle / 2, -thickness / 2, Handle / 2) : new Thickness(Handle / 2, -thickness / 2, Handle / 2, -thickness / 2),
            Cursor = vertical ? Cursors.SizeWE : Cursors.SizeNS,
            Background = new SolidColorBrush(Color.FromArgb(150, 0xFF, 0xFF, 0xFF)),
            BorderBrush = Accent,
            BorderThickness = new Thickness(3),
            Template = ThumbTemplate(thickness / 2),
        };
    }

    private static ControlTemplate ThumbTemplate(double radius)
    {
        var f = new FrameworkElementFactory(typeof(Border));
        f.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        f.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        f.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        f.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
        return new ControlTemplate(typeof(Thumb)) { VisualTree = f };
    }

    private static Rectangle Outline() => new() { StrokeThickness = 6, StrokeDashArray = [3, 2], IsHitTestVisible = false };

    private static IEnumerable<(HorizontalAlignment H, VerticalAlignment V)> Corners =>
    [
        (HorizontalAlignment.Left, VerticalAlignment.Top),
        (HorizontalAlignment.Right, VerticalAlignment.Top),
        (HorizontalAlignment.Left, VerticalAlignment.Bottom),
        (HorizontalAlignment.Right, VerticalAlignment.Bottom),
    ];

    private enum Side
    {
        Left,
        Top,
        Right,
        Bottom,
    }

    /// <summary>One camera or overlay on the canvas, bound to its view model.</summary>
    private sealed class LayerVisual
    {
        private readonly SceneEditor _owner;
        private readonly ISceneItem _item;
        private readonly int _zBase;
        private readonly Image _image = new() { Stretch = Stretch.Fill };
        private readonly Border _placeholder = new() { Background = new SolidColorBrush(Color.FromArgb(160, 0x91, 0x46, 0xFF)) };
        private readonly TextBlock _placeholderText = new() { Foreground = Brushes.White, FontSize = 56, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        private readonly Rectangle _outline = Outline();
        private readonly Border _label = new() { Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), Padding = new Thickness(14, 4, 14, 4), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        private readonly TextBlock _labelText = new() { Foreground = Brushes.White, FontSize = 36 };
        private readonly List<Thumb> _corners = [];
        private Rect _dragRect;
        private bool _hover;

        public LayerVisual(SceneEditor owner, ISceneItem item, int zBase)
        {
            _owner = owner;
            _item = item;
            _zBase = zBase;
            Root = new Grid { Focusable = true, FocusVisualStyle = null };
            _placeholder.Child = _placeholderText;
            _label.Child = _labelText;
            var body = new Thumb { Cursor = Cursors.SizeAll, Opacity = 0 };
            body.DragStarted += (_, _) => BeginDrag();
            body.DragDelta += (_, e) => Commit(_dragRect = _owner.Moved(_dragRect, e.HorizontalChange, e.VerticalChange));
            body.DragCompleted += (_, _) => _owner.ShowGuides(null, null);
            Root.Children.Add(_placeholder);
            Root.Children.Add(_image);
            Root.Children.Add(_outline);
            Root.Children.Add(_label);
            Root.Children.Add(body);
            foreach (var (h, v) in Corners)
            {
                var left = h == HorizontalAlignment.Left;
                var top = v == VerticalAlignment.Top;
                var t = RoundThumb(h, v, left == top ? Cursors.SizeNWSE : Cursors.SizeNESW);
                t.DragStarted += (_, _) => BeginDrag();
                t.DragDelta += (_, e) => Commit(_dragRect = _owner.ResizedFromCorner(_dragRect, left, top, e.HorizontalChange));
                t.DragCompleted += (_, _) => _owner.ShowGuides(null, null);
                _corners.Add(t);
                Root.Children.Add(t);
            }

            Root.KeyDown += OnKeyDown;
            Root.PreviewMouseDown += (_, _) =>
            {
                _owner.Select(_item);
                Root.Focus();
            };
            Root.MouseEnter += (_, _) => SetHover(true);
            Root.MouseLeave += (_, _) => SetHover(false);
            _item.PropertyChanged += OnItemChanged;
        }

        public Grid Root { get; }

        public void Detach() => _item.PropertyChanged -= OnItemChanged;

        private void SetHover(bool value)
        {
            _hover = value;
            Refresh();
        }

        private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ISceneItem.Placement) or nameof(ISceneItem.IsShown)
                or nameof(ISceneItem.IsSelected) or nameof(ISceneItem.Name) or nameof(ISceneItem.Preview))
            {
                Refresh();
            }
        }

        public void Refresh()
        {
            var clean = _owner.IsPreviewOnly;
            var selected = _item.IsSelected && (_item is OverlayItemViewModel || _owner.CameraHandlesAllowed);
            var preview = _item.Preview;
            Root.Visibility = _item.IsShown && (!clean || preview is not null || selected) ? Visibility.Visible : Visibility.Collapsed;
            _image.Source = preview;
            _placeholder.Visibility = preview is null && (!clean || selected) ? Visibility.Visible : Visibility.Collapsed;
            _placeholderText.Text = _item.Name;
            _labelText.Text = _item.Name;
            _label.Visibility = preview is null || clean ? Visibility.Collapsed : Visibility.Visible;
            _outline.Visibility = !clean || selected || _hover ? Visibility.Visible : Visibility.Collapsed;
            _outline.Stroke = selected ? Accent : Faint;
            _outline.StrokeThickness = selected ? 6 : 3;
            foreach (var c in _corners)
            {
                c.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            }

            Panel.SetZIndex(Root, _zBase + (selected ? 1 : 0));
            AutomationProperties.SetName(Root, $"{_item.Name} position. Drag to move, drag a corner to resize, or use the arrow keys.");
            var r = CurrentRect();
            Canvas.SetLeft(Root, r.X);
            Canvas.SetTop(Root, r.Y);
            Root.Width = r.Width;
            Root.Height = r.Height;
        }

        private Rect CurrentRect()
        {
            var p = _item.Placement.Clamped();
            var (w, h) = (_owner.CanvasWidth, _owner.CanvasHeight);
            return new Rect(p.X * w, p.Y * h, p.Width * w, p.Height * h);
        }

        private void BeginDrag()
        {
            _owner.Select(_item);
            _dragRect = CurrentRect();
            Root.Focus();
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down))
            {
                return;
            }

            e.Handled = true;
            Commit(_owner.Nudged(CurrentRect(), e.Key));
        }

        private void Commit(Rect r)
        {
            var n = _owner.Normalized(r);
            if (n != _item.Placement)
            {
                _item.Placement = n;
            }
        }
    }

    /// <summary>
    /// The gameplay area: the capture's picture trimmed and fitted as the stream will draw it, with handles
    /// to move it, resize it from a corner (shape kept) or an edge (free), and Alt-drag an edge to trim.
    /// </summary>
    private sealed class BaseVisual
    {
        private readonly SceneEditor _owner;
        private readonly Border _clip = new() { ClipToBounds = true, Background = new SolidColorBrush(Color.FromRgb(36, 36, 42)) };
        private readonly Canvas _picture = new();
        private readonly Image _image = new() { Stretch = Stretch.Fill };
        private readonly Rectangle _outline = Outline();
        private readonly List<Thumb> _handles = [];
        private Rect _dragRect;
        private NormalizedRect _dragCrop;
        private bool _trimming;
        private bool _hover;

        public BaseVisual(SceneEditor owner)
        {
            _owner = owner;
            Root = new Grid { Focusable = true, FocusVisualStyle = null };
            _picture.Children.Add(_image);
            _clip.Child = _picture;
            var body = new Thumb { Cursor = Cursors.SizeAll, Opacity = 0 };
            body.DragStarted += (_, _) => BeginDrag();
            body.DragDelta += (_, e) => Commit(_dragRect = _owner.Moved(_dragRect, e.HorizontalChange, e.VerticalChange), _dragCrop);
            body.DragCompleted += (_, _) => _owner.ShowGuides(null, null);
            Root.Children.Add(_clip);
            Root.Children.Add(_outline);
            Root.Children.Add(body);
            foreach (var side in Enum.GetValues<Side>())
            {
                var t = EdgeThumb(side);
                t.DragStarted += (_, _) => BeginDrag();
                t.DragDelta += (_, e) => DragEdge(side, side is Side.Left or Side.Right ? e.HorizontalChange : e.VerticalChange);
                _handles.Add(t);
                Root.Children.Add(t);
            }

            foreach (var (h, v) in Corners)
            {
                var left = h == HorizontalAlignment.Left;
                var top = v == VerticalAlignment.Top;
                var t = RoundThumb(h, v, left == top ? Cursors.SizeNWSE : Cursors.SizeNESW);
                t.DragStarted += (_, _) => BeginDrag();
                t.DragDelta += (_, e) => Commit(_dragRect = _owner.ResizedFromCorner(_dragRect, left, top, e.HorizontalChange), _dragCrop);
                _handles.Add(t);
                Root.Children.Add(t);
            }

            Root.KeyDown += OnKeyDown;
            Root.PreviewMouseDown += (_, _) =>
            {
                _owner.SelectBase();
                Root.Focus();
            };
            Root.MouseEnter += (_, _) => SetHover(true);
            Root.MouseLeave += (_, _) => SetHover(false);
        }

        public Grid Root { get; }

        public void SetPicture(ImageSource? source)
        {
            _image.Source = source;
            Refresh();
        }

        private void SetHover(bool value)
        {
            _hover = value;
            Refresh();
        }

        public void Refresh()
        {
            var b = Bounds();
            Canvas.SetLeft(Root, b.X);
            Canvas.SetTop(Root, b.Y);
            Root.Width = b.Width;
            Root.Height = b.Height;
            ArrangePicture(b);

            var selected = _owner.IsBaseSelected;
            _outline.Visibility = selected || _hover ? Visibility.Visible : Visibility.Collapsed;
            _outline.Stroke = selected ? Accent : Faint;
            _outline.StrokeThickness = selected ? 6 : 3;
            foreach (var h in _handles)
            {
                h.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            }

            AutomationProperties.SetName(Root, "Game picture. Drag to move, drag a corner or an edge to resize, hold Alt and drag an edge to trim, or use the arrow keys.");
        }

        private Rect Bounds()
        {
            var p = _owner.BasePlacement.Clamped();
            var (w, h) = (_owner.CanvasWidth, _owner.CanvasHeight);
            return new Rect(p.X * w, p.Y * h, p.Width * w, p.Height * h);
        }

        /// <summary>The captured picture's size. Without a snapshot the capture is assumed to have the canvas's shape.</summary>
        private (double Width, double Height) SourceSize() =>
            _image.Source is BitmapSource { PixelWidth: > 0, PixelHeight: > 0 } s ? (s.PixelWidth, s.PixelHeight) : (_owner.CanvasWidth, _owner.CanvasHeight);

        /// <summary>
        /// Where the trimmed picture is drawn, relative to the area: letterboxed inside it (Fit) or
        /// covering it (Fill), centred either way, which is what the stream's filter graph does.
        /// </summary>
        private Rect Region(Rect bounds, NormalizedRect crop)
        {
            var (sw, sh) = SourceSize();
            var rw = sw * crop.Width;
            var rh = sh * crop.Height;
            var scale = _owner.BaseFit == BaseFit.Fill ? Math.Max(bounds.Width / rw, bounds.Height / rh) : Math.Min(bounds.Width / rw, bounds.Height / rh);
            var w = rw * scale;
            var h = rh * scale;
            return new Rect((bounds.Width - w) / 2, (bounds.Height - h) / 2, w, h);
        }

        private void ArrangePicture(Rect bounds)
        {
            _clip.Width = bounds.Width;
            _clip.Height = bounds.Height;
            var crop = _owner.BaseCrop.Clamped(SceneLayout.MinCrop);
            var region = Region(bounds, crop);
            var (sw, sh) = SourceSize();
            var scale = region.Width / (sw * crop.Width);
            _image.Width = sw * scale;
            _image.Height = sh * scale;
            Canvas.SetLeft(_image, region.X - (crop.X * sw * scale));
            Canvas.SetTop(_image, region.Y - (crop.Y * sh * scale));
        }

        private void BeginDrag()
        {
            _owner.SelectBase();
            _dragRect = Bounds();
            _dragCrop = _owner.BaseCrop.Clamped(SceneLayout.MinCrop);
            _trimming = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
            Root.Focus();
        }

        private void DragEdge(Side side, double delta)
        {
            if (_trimming)
            {
                Trim(side, delta);
            }
            else
            {
                ResizeEdge(side, delta);
            }
        }

        /// <summary>Moves one side; the other three stay. The picture is refitted inside the new shape.</summary>
        private void ResizeEdge(Side side, double delta)
        {
            var r = _dragRect;
            var (cw, ch) = (_owner.CanvasWidth, _owner.CanvasHeight);
            switch (side)
            {
                case Side.Left:
                    var x = Math.Clamp(r.X + delta, 0, r.Right - MinSize);
                    r = new Rect(x, r.Y, r.Right - x, r.Height);
                    break;
                case Side.Right:
                    r.Width = Math.Clamp(r.Width + delta, MinSize, cw - r.X);
                    break;
                case Side.Top:
                    var y = Math.Clamp(r.Y + delta, 0, r.Bottom - MinSize);
                    r = new Rect(r.X, y, r.Width, r.Bottom - y);
                    break;
                case Side.Bottom:
                    r.Height = Math.Clamp(r.Height + delta, MinSize, ch - r.Y);
                    break;
            }

            _dragRect = r;
            Commit(r, _dragCrop);
        }

        /// <summary>
        /// Alt-drag: the side moves and the picture under it is trimmed away instead of squeezed, so what
        /// stays keeps its scale and place, as in OBS. Dragging back restores what was trimmed. The area
        /// becomes exactly the drawn picture, so no bars appear where the trim was.
        /// </summary>
        private void Trim(Side side, double delta)
        {
            var region = Region(_dragRect, _dragCrop);
            var drawn = new Rect(_dragRect.X + region.X, _dragRect.Y + region.Y, region.Width, region.Height);
            var (cw, ch) = (_owner.CanvasWidth, _owner.CanvasHeight);
            var c = _dragCrop;
            switch (side)
            {
                case Side.Left:
                {
                    // Fraction of the picture removed on this side, limited by what is left, what was
                    // trimmed before (when dragging back out) and the canvas edge.
                    var f = Math.Clamp(delta / drawn.Width * c.Width, Math.Max(-c.X, -drawn.X / drawn.Width * c.Width), c.Width - SceneLayout.MinCrop);
                    var px = f * drawn.Width / c.Width;
                    c = new NormalizedRect(c.X + f, c.Y, c.Width - f, c.Height);
                    drawn = new Rect(drawn.X + px, drawn.Y, drawn.Width - px, drawn.Height);
                    break;
                }

                case Side.Right:
                {
                    var f = Math.Clamp(-delta / drawn.Width * c.Width, Math.Max(-(1 - c.Right), (drawn.Right - cw) / drawn.Width * c.Width), c.Width - SceneLayout.MinCrop);
                    var px = f * drawn.Width / c.Width;
                    c = c with { Width = c.Width - f };
                    drawn = new Rect(drawn.X, drawn.Y, drawn.Width - px, drawn.Height);
                    break;
                }

                case Side.Top:
                {
                    var f = Math.Clamp(delta / drawn.Height * c.Height, Math.Max(-c.Y, -drawn.Y / drawn.Height * c.Height), c.Height - SceneLayout.MinCrop);
                    var px = f * drawn.Height / c.Height;
                    c = new NormalizedRect(c.X, c.Y + f, c.Width, c.Height - f);
                    drawn = new Rect(drawn.X, drawn.Y + px, drawn.Width, drawn.Height - px);
                    break;
                }

                case Side.Bottom:
                {
                    var f = Math.Clamp(-delta / drawn.Height * c.Height, Math.Max(-(1 - c.Bottom), (drawn.Bottom - ch) / drawn.Height * c.Height), c.Height - SceneLayout.MinCrop);
                    var px = f * drawn.Height / c.Height;
                    c = c with { Height = c.Height - f };
                    drawn = new Rect(drawn.X, drawn.Y, drawn.Width, drawn.Height - px);
                    break;
                }
            }

            _dragCrop = c;
            _dragRect = drawn;
            Commit(drawn, c);
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down))
            {
                return;
            }

            e.Handled = true;
            Commit(_owner.Nudged(Bounds(), e.Key), _owner.BaseCrop);
        }

        private void Commit(Rect r, NormalizedRect crop)
        {
            var c = crop.Clamped(SceneLayout.MinCrop);
            if (c != _owner.BaseCrop)
            {
                _owner.BaseCrop = c;
            }

            var n = _owner.Normalized(r);
            if (n != _owner.BasePlacement)
            {
                _owner.BasePlacement = n;
            }
        }
    }
}

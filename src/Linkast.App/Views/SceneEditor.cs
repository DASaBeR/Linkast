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
using System.Windows.Shapes;
using Linkast.App.ViewModels;
using Linkast.Core.Camera;

namespace Linkast.App.Views;

/// <summary>
/// Layout editor for all camera layers of the active layout. Works on a logical canvas the size of
/// the output (1920×1080, or 1080×1920 for vertical) scaled by a Viewbox, so editor coordinates are
/// output pixels: what you place here is exactly what streams. Click a camera to select it, drag to
/// move, drag a corner to resize (aspect kept), arrow keys nudge; snapping to a grid, the edges and
/// the centre with visible guides. Layers are drawn in stream order (later ones on top).
/// </summary>
public sealed class SceneEditor : UserControl
{
    private const double Handle = 56;
    private const double SnapDistance = 18;

    public static readonly DependencyProperty LayersProperty = DependencyProperty.Register(
        nameof(Layers), typeof(IEnumerable), typeof(SceneEditor), new PropertyMetadata(null, (d, e) => ((SceneEditor)d).OnLayersChanged(e.OldValue as IEnumerable, e.NewValue as IEnumerable)));

    public static readonly DependencyProperty SelectedLayerProperty = DependencyProperty.Register(
        nameof(SelectedLayer), typeof(CameraLayerViewModel), typeof(SceneEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((SceneEditor)d).RefreshAll()));

    public static readonly DependencyProperty CanvasWidthProperty = DependencyProperty.Register(
        nameof(CanvasWidth), typeof(double), typeof(SceneEditor), new PropertyMetadata(1920.0, (d, _) => ((SceneEditor)d).OnCanvasSize()));

    public static readonly DependencyProperty CanvasHeightProperty = DependencyProperty.Register(
        nameof(CanvasHeight), typeof(double), typeof(SceneEditor), new PropertyMetadata(1080.0, (d, _) => ((SceneEditor)d).OnCanvasSize()));

    public static readonly DependencyProperty BasePlacementProperty = DependencyProperty.Register(
        nameof(BasePlacement), typeof(NormalizedRect), typeof(SceneEditor), new PropertyMetadata(new NormalizedRect(0, 0, 1, 1), (d, _) => ((SceneEditor)d).ArrangeBase()));

    public static readonly DependencyProperty BaseFitProperty = DependencyProperty.Register(
        nameof(BaseFit), typeof(BaseFit), typeof(SceneEditor), new PropertyMetadata(BaseFit.Fit, (d, _) => ((SceneEditor)d).ArrangeBase()));

    public static readonly DependencyProperty BackgroundSourceProperty = DependencyProperty.Register(
        nameof(BackgroundSource), typeof(ImageSource), typeof(SceneEditor), new PropertyMetadata(null, (d, e) => ((SceneEditor)d)._background.Source = (ImageSource?)e.NewValue));

    public static readonly DependencyProperty SnapProperty = DependencyProperty.Register(
        nameof(Snap), typeof(bool), typeof(SceneEditor), new PropertyMetadata(true));

    private readonly Canvas _canvas = new() { ClipToBounds = true, Background = new SolidColorBrush(Color.FromRgb(24, 24, 28)) };
    private readonly Border _baseArea = new() { ClipToBounds = true, Background = new SolidColorBrush(Color.FromRgb(36, 36, 42)) };
    private readonly Image _background = new() { Stretch = Stretch.Uniform };
    private readonly Line _vGuide = Guide();
    private readonly Line _hGuide = Guide();
    private readonly List<LayerVisual> _visuals = [];
    private INotifyCollectionChanged? _observed;
    private readonly Viewbox _viewbox;

    public SceneEditor()
    {
        _baseArea.Child = _background;
        _canvas.Children.Add(_baseArea);
        _canvas.Children.Add(_vGuide);
        _canvas.Children.Add(_hGuide);
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

    private double GridStep => Math.Max(CanvasWidth, CanvasHeight) / 48;

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
        ArrangeBase();
        RefreshAll();
    }

    private void ArrangeBase()
    {
        var b = BasePlacement.Clamped();
        Canvas.SetLeft(_baseArea, b.X * CanvasWidth);
        Canvas.SetTop(_baseArea, b.Y * CanvasHeight);
        _baseArea.Width = b.Width * CanvasWidth;
        _baseArea.Height = b.Height * CanvasHeight;
        _background.Width = _baseArea.Width;
        _background.Height = _baseArea.Height;
        _background.Stretch = BaseFit == BaseFit.Fill ? Stretch.UniformToFill : Stretch.Uniform;
    }

    private void OnLayersChanged(IEnumerable? oldValue, IEnumerable? newValue)
    {
        if (_observed is not null)
        {
            _observed.CollectionChanged -= OnCollectionChanged;
        }

        _observed = newValue as INotifyCollectionChanged;
        if (_observed is not null)
        {
            _observed.CollectionChanged += OnCollectionChanged;
        }

        Rebuild();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        foreach (var v in _visuals)
        {
            v.Detach();
            _canvas.Children.Remove(v.Root);
        }

        _visuals.Clear();
        if (Layers is null)
        {
            return;
        }

        // Insert below the guides so they stay on top; collection order is stacking order.
        var insertAt = _canvas.Children.IndexOf(_vGuide);
        foreach (var vm in Layers.OfType<CameraLayerViewModel>())
        {
            var visual = new LayerVisual(this, vm);
            _visuals.Add(visual);
            _canvas.Children.Insert(insertAt++, visual.Root);
        }

        RefreshAll();
    }

    private void RefreshAll()
    {
        foreach (var v in _visuals)
        {
            v.Refresh();
        }
    }

    internal void Select(CameraLayerViewModel vm)
    {
        if (!ReferenceEquals(SelectedLayer, vm))
        {
            SelectedLayer = vm;
        }
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

    /// <summary>One camera on the canvas, bound to its view model.</summary>
    private sealed class LayerVisual
    {
        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x91, 0x46, 0xFF));
        private static readonly Brush Faint = new SolidColorBrush(Color.FromArgb(200, 0xFF, 0xFF, 0xFF));

        private readonly SceneEditor _owner;
        private readonly CameraLayerViewModel _vm;
        private readonly Image _image = new() { Stretch = Stretch.Fill };
        private readonly Border _placeholder = new() { Background = new SolidColorBrush(Color.FromArgb(160, 0x91, 0x46, 0xFF)) };
        private readonly TextBlock _placeholderText = new() { Foreground = Brushes.White, FontSize = 56, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        private readonly Rectangle _outline = new() { StrokeThickness = 6, StrokeDashArray = [3, 2], IsHitTestVisible = false };
        private readonly Border _label = new() { Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), Padding = new Thickness(14, 4, 14, 4), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        private readonly TextBlock _labelText = new() { Foreground = Brushes.White, FontSize = 36 };
        private readonly List<Thumb> _corners = [];
        private Rect _dragRect;

        public LayerVisual(SceneEditor owner, CameraLayerViewModel vm)
        {
            _owner = owner;
            _vm = vm;
            Root = new Grid { Focusable = true, FocusVisualStyle = null };
            _placeholder.Child = _placeholderText;
            _label.Child = _labelText;
            var body = new Thumb { Cursor = Cursors.SizeAll, Opacity = 0 };
            body.DragStarted += (_, _) => BeginDrag();
            body.DragDelta += (_, e) => Move(e.HorizontalChange, e.VerticalChange);
            body.DragCompleted += (_, _) => _owner.ShowGuides(null, null);
            Root.Children.Add(_placeholder);
            Root.Children.Add(_image);
            Root.Children.Add(_outline);
            Root.Children.Add(_label);
            Root.Children.Add(body);
            foreach (var (h, v) in new[] { (HorizontalAlignment.Left, VerticalAlignment.Top), (HorizontalAlignment.Right, VerticalAlignment.Top), (HorizontalAlignment.Left, VerticalAlignment.Bottom), (HorizontalAlignment.Right, VerticalAlignment.Bottom) })
            {
                var t = CornerThumb(h, v);
                _corners.Add(t);
                Root.Children.Add(t);
            }

            Root.KeyDown += OnKeyDown;
            Root.PreviewMouseDown += (_, _) =>
            {
                _owner.Select(_vm);
                Root.Focus();
            };
            _vm.PropertyChanged += OnVmChanged;
        }

        public Grid Root { get; }

        public void Detach() => _vm.PropertyChanged -= OnVmChanged;

        private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(CameraLayerViewModel.Placement) or nameof(CameraLayerViewModel.IsShown)
                or nameof(CameraLayerViewModel.IsSelected) or nameof(CameraLayerViewModel.Name) or nameof(CameraLayerViewModel.Preview))
            {
                Refresh();
            }
        }

        public void Refresh()
        {
            var selected = _vm.IsSelected;
            Root.Visibility = _vm.IsShown ? Visibility.Visible : Visibility.Collapsed;
            _image.Source = _vm.Preview;
            _placeholder.Visibility = _vm.Preview is null ? Visibility.Visible : Visibility.Collapsed;
            _placeholderText.Text = _vm.Name;
            _labelText.Text = _vm.Name;
            _label.Visibility = _vm.Preview is null ? Visibility.Collapsed : Visibility.Visible;
            _outline.Stroke = selected ? Accent : Faint;
            _outline.StrokeThickness = selected ? 6 : 3;
            foreach (var c in _corners)
            {
                c.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            }

            Panel.SetZIndex(Root, selected ? 1 : 0);
            AutomationProperties.SetName(Root, $"{_vm.Name} position. Drag to move, drag a corner to resize, or use the arrow keys.");
            var r = CurrentRect();
            Canvas.SetLeft(Root, r.X);
            Canvas.SetTop(Root, r.Y);
            Root.Width = r.Width;
            Root.Height = r.Height;
        }

        private Thumb CornerThumb(HorizontalAlignment h, VerticalAlignment v)
        {
            var t = new Thumb
            {
                Width = Handle,
                Height = Handle,
                HorizontalAlignment = h,
                VerticalAlignment = v,
                Margin = new Thickness(-Handle / 2),
                Cursor = (h == HorizontalAlignment.Left) == (v == VerticalAlignment.Top) ? Cursors.SizeNWSE : Cursors.SizeNESW,
                Background = Brushes.White,
                BorderBrush = Accent,
                BorderThickness = new Thickness(6),
                Template = new ControlTemplate(typeof(Thumb)) { VisualTree = Factory() },
            };
            t.DragStarted += (_, _) => BeginDrag();
            t.DragDelta += (_, e) => Resize(h == HorizontalAlignment.Left, v == VerticalAlignment.Top, e.HorizontalChange);
            t.DragCompleted += (_, _) => _owner.ShowGuides(null, null);
            return t;

            static FrameworkElementFactory Factory()
            {
                var f = new FrameworkElementFactory(typeof(Border));
                f.SetValue(Border.CornerRadiusProperty, new CornerRadius(Handle / 2));
                f.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
                f.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
                f.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
                return f;
            }
        }

        private Rect CurrentRect()
        {
            var p = _vm.Placement.Clamped();
            var (w, h) = (_owner.CanvasWidth, _owner.CanvasHeight);
            return new Rect(p.X * w, p.Y * h, p.Width * w, p.Height * h);
        }

        private void BeginDrag()
        {
            _owner.Select(_vm);
            _dragRect = CurrentRect();
            Root.Focus();
        }

        private void Move(double dx, double dy)
        {
            _dragRect.X += dx;
            _dragRect.Y += dy;
            var r = _dragRect;
            r.X = _owner.SnapAxis(r.X, r.Width, _owner.CanvasWidth, out var gx);
            r.Y = _owner.SnapAxis(r.Y, r.Height, _owner.CanvasHeight, out var gy);
            _owner.ShowGuides(gx, gy);
            Commit(r);
        }

        private void Resize(bool left, bool top, double dx)
        {
            // Thumb deltas are incremental because the handle moves with the corner. The opposite
            // corner stays anchored and the rectangle keeps its current aspect ratio.
            var (cw, ch) = (_owner.CanvasWidth, _owner.CanvasHeight);
            var aspect = _dragRect.Height > 0 ? _dragRect.Width / _dragRect.Height : 16.0 / 9;
            var width = Math.Clamp(_dragRect.Width + (left ? -dx : dx), 80, cw);
            var height = width / aspect;
            if (height > ch)
            {
                height = ch;
                width = height * aspect;
            }

            var anchorX = left ? _dragRect.Right : _dragRect.X;
            var anchorY = top ? _dragRect.Bottom : _dragRect.Y;
            _dragRect = new Rect(left ? anchorX - width : anchorX, top ? anchorY - height : anchorY, width, height);
            Commit(_dragRect);
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? _owner.GridStep : 4;
            var r = CurrentRect();
            switch (e.Key)
            {
                case Key.Left: r.X -= step; break;
                case Key.Right: r.X += step; break;
                case Key.Up: r.Y -= step; break;
                case Key.Down: r.Y += step; break;
                default: return;
            }

            e.Handled = true;
            Commit(r);
        }

        private void Commit(Rect r)
        {
            var (cw, ch) = (_owner.CanvasWidth, _owner.CanvasHeight);
            var n = new NormalizedRect(r.X / cw, r.Y / ch, r.Width / cw, r.Height / ch).Clamped();
            if (n != _vm.Placement)
            {
                _vm.Placement = n;
            }
        }
    }
}

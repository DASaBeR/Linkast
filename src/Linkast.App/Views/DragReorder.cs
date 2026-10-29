// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Linkast.App.Views;

/// <summary>
/// Lets the user drag a row of a list to another place. While dragging, <c>marker</c> (a thin line laid
/// over the list) shows where the row will land; on drop, <c>move</c> gets the row's item and its new
/// index. A press without a drag stays a normal click.
/// </summary>
internal sealed class DragReorder
{
    private const string Format = "Linkast.Reorder";

    private readonly ItemsControl _list;
    private readonly FrameworkElement _marker;
    private readonly Action<object, int> _move;
    private Point _pressedAt;
    private object? _pressedItem;

    private DragReorder(ItemsControl list, FrameworkElement marker, Action<object, int> move)
    {
        _list = list;
        _marker = marker;
        _move = move;
        _marker.IsHitTestVisible = false;
        _marker.Visibility = Visibility.Collapsed;
        list.AllowDrop = true;
        list.PreviewMouseLeftButtonDown += OnPressed;
        list.PreviewMouseLeftButtonUp += OnReleased;
        list.PreviewMouseMove += OnMouseMove;
        list.DragOver += OnDragOver;
        list.DragLeave += (_, _) => _marker.Visibility = Visibility.Collapsed;
        list.Drop += OnDrop;
    }

    /// <summary>Makes the rows of <paramref name="list"/> draggable. <paramref name="move"/> receives the item and its new index.</summary>
    public static void Attach(ItemsControl list, FrameworkElement marker, Action<object, int> move) => _ = new DragReorder(list, marker, move);

    private void OnPressed(object sender, MouseButtonEventArgs e)
    {
        _pressedAt = e.GetPosition(_list);
        _pressedItem = ItemAt(e.OriginalSource as DependencyObject);

        // A list box would select (switch scenes, on air while live) as soon as the button goes down;
        // here it selects on release, and only when the row wasn't dragged.
        if (_pressedItem is not null && _list is ListBox)
        {
            e.Handled = true;
        }
    }

    private void OnReleased(object sender, MouseButtonEventArgs e)
    {
        if (_pressedItem is { } item && _list is ListBox box)
        {
            box.SelectedItem = item;
            (box.ItemContainerGenerator.ContainerFromItem(item) as UIElement)?.Focus();
            e.Handled = true;
        }

        _pressedItem = null;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedItem is null || e.LeftButton != MouseButtonState.Pressed)
        {
            _pressedItem = null;
            return;
        }

        var moved = e.GetPosition(_list) - _pressedAt;
        if (Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance && Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance)
        {
            return;
        }

        var item = _pressedItem;
        _pressedItem = null;

        // A pressed button holds the mouse; let it go, or the drag never starts and the click fires later.
        Mouse.Capture(null);
        DragDrop.DoDragDrop(_list, new DataObject(Format, item), DragDropEffects.Move);
        _marker.Visibility = Visibility.Collapsed;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(Format))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var (_, y) = InsertionPoint(e.GetPosition(_list));
        _marker.Margin = new Thickness(_marker.Margin.Left, y - (_marker.ActualHeight / 2), _marker.Margin.Right, 0);
        _marker.Visibility = Visibility.Visible;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        _marker.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(Format) is not { } item)
        {
            return;
        }

        var from = _list.Items.IndexOf(item);
        var (before, _) = InsertionPoint(e.GetPosition(_list));
        if (from < 0)
        {
            return;
        }

        // Dropped before row `before`; once the row leaves its place, the rows after it move up by one.
        var to = before > from ? before - 1 : before;
        if (to != from)
        {
            _move(item, to);
        }

        e.Handled = true;
    }

    /// <summary>The row the pointer is before (Count = after the last) and where the marker goes, in list coordinates.</summary>
    private (int Before, double Y) InsertionPoint(Point pointer)
    {
        var lastBottom = 0.0;
        for (var i = 0; i < _list.Items.Count; i++)
        {
            if (_list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement row || !row.IsVisible)
            {
                continue;
            }

            var top = row.TranslatePoint(new Point(0, 0), _list).Y;
            if (pointer.Y < top + (row.ActualHeight / 2))
            {
                return (i, top);
            }

            lastBottom = top + row.ActualHeight;
        }

        return (_list.Items.Count, lastBottom);
    }

    private object? ItemAt(DependencyObject? source)
    {
        // Not from the eye, a menu button or other controls inside the row: those keep their own clicks.
        for (var d = source; d is not null && d != _list; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.Primitives.ToggleButton || (d is Button b && b.Tag is "NoDrag"))
            {
                return null;
            }

            if (_list.ItemContainerGenerator.ItemFromContainer(d) is { } item && item != DependencyProperty.UnsetValue)
            {
                return item;
            }
        }

        return null;
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;

namespace Linkast.App.Views;

/// <summary>
/// A labelled slider that shows its current value with a unit and, when <see cref="DefaultValue"/> is set,
/// a button that puts it back to that value.
/// </summary>
public sealed class ValueSlider : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(ValueSlider), new PropertyMetadata(string.Empty, (d, _) => ((ValueSlider)d).OnLabel()));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ValueSlider),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ValueSlider)d).OnValue()));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(ValueSlider), new PropertyMetadata(0.0));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(ValueSlider), new PropertyMetadata(100.0));

    /// <summary>Composite format for the value, for example "{0:0}%" or "{0:0.00}×".</summary>
    public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(
        nameof(Format), typeof(string), typeof(ValueSlider), new PropertyMetadata("{0:0}", (d, _) => ((ValueSlider)d).OnValue()));

    /// <summary>The value the reset button restores. NaN (the default) hides the button.</summary>
    public static readonly DependencyProperty DefaultValueProperty = DependencyProperty.Register(
        nameof(DefaultValue), typeof(double), typeof(ValueSlider), new PropertyMetadata(double.NaN, (d, _) => ((ValueSlider)d).OnDefault()));

    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _value = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _reset = new() { Width = 24, Height = 24, FontSize = 11, Margin = new Thickness(4, 0, -4, 0), Visibility = Visibility.Collapsed };
    private readonly Slider _slider = new() { Margin = new Thickness(0, 2, 0, 0) };

    public ValueSlider()
    {
        Typography.SetNumeralAlignment(_value, FontNumeralAlignment.Tabular);
        _value.SetResourceReference(TextBlock.ForegroundProperty, "Linkast.TextPrimary");
        _reset.SetResourceReference(StyleProperty, "Linkast.Button.Icon");
        _reset.Content = ((char)0xE72C).ToString(); // Segoe Fluent Icons: Refresh
        _reset.ToolTip = "Reset";
        _reset.Click += (_, _) => Value = DefaultValue;

        _slider.SetBinding(RangeBase.ValueProperty, new Binding(nameof(Value)) { Source = this, Mode = BindingMode.TwoWay });
        _slider.SetBinding(RangeBase.MinimumProperty, new Binding(nameof(Minimum)) { Source = this });
        _slider.SetBinding(RangeBase.MaximumProperty, new Binding(nameof(Maximum)) { Source = this });

        var head = new DockPanel();
        DockPanel.SetDock(_reset, Dock.Right);
        DockPanel.SetDock(_value, Dock.Right);
        head.Children.Add(_reset);
        head.Children.Add(_value);
        head.Children.Add(_label);

        var root = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        root.Children.Add(head);
        root.Children.Add(_slider);
        Content = root;
        OnValue();
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public string Format
    {
        get => (string)GetValue(FormatProperty);
        set => SetValue(FormatProperty, value);
    }

    public double DefaultValue
    {
        get => (double)GetValue(DefaultValueProperty);
        set => SetValue(DefaultValueProperty, value);
    }

    private void OnLabel()
    {
        _label.Text = Label;
        AutomationProperties.SetName(_slider, Label);
        AutomationProperties.SetName(_reset, "Reset " + Label);
    }

    private void OnValue()
    {
        var text = string.Format(CultureInfo.CurrentCulture, Format, Value);
        _value.Text = text;
        AutomationProperties.SetItemStatus(_slider, text);
    }

    private void OnDefault() => _reset.Visibility = double.IsNaN(DefaultValue) ? Visibility.Collapsed : Visibility.Visible;
}

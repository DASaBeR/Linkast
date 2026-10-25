// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Linkast.Core;

namespace Linkast.App.ViewModels;

public static class Converters
{
    public static IValueConverter NotNullToVisibility { get; } = new Lambda(v =>
        v is null || (v is string s && s.Length == 0) ? Visibility.Collapsed : Visibility.Visible);

    /// <summary>PRD 4.1: capture, encoding and network errors must be clearly distinguished.</summary>
    public static IValueConverter ErrorCategoryLabel { get; } = new Lambda(v => v switch
    {
        ErrorCategory.Capture => "Capture problem",
        ErrorCategory.Audio => "Audio problem",
        ErrorCategory.Encoder => "Encoding problem",
        ErrorCategory.Network => "Network problem",
        ErrorCategory.Configuration => "Settings problem",
        ErrorCategory.Process => "FFmpeg problem",
        _ => "Problem",
    });

    public static IValueConverter TestResultBrush { get; } = new Lambda(v => v switch
    {
        true => Brush("OkBrush"),
        false => Brush("FailedBrush"),
        _ => Application.Current.TryFindResource("TextFillColorSecondaryBrush") ?? Brushes.Gray,
    });

    public static IValueConverter ProblemBrush { get; } = new Lambda(v => v is true
        ? Brush("ConnectingBrush")
        : Application.Current.TryFindResource("TextFillColorSecondaryBrush") ?? Brushes.Gray);

    /// <summary>Accent colour for "a new version is ready", secondary text otherwise.</summary>
    public static IValueConverter UpdateBrush { get; } = new Lambda(v => v is true
        ? Application.Current.TryFindResource("AccentTextFillColorPrimaryBrush") ?? Brush("OkBrush")
        : Application.Current.TryFindResource("TextFillColorSecondaryBrush") ?? Brushes.Gray);

    public static IValueConverter BoldWhenTrue { get; } = new Lambda(v => v is true ? FontWeights.SemiBold : FontWeights.Normal);

    public static IValueConverter InverseBool { get; } = new Lambda(v => v is not true);

    public static IValueConverter InverseBoolToVisibility { get; } = new Lambda(v => v is true ? Visibility.Collapsed : Visibility.Visible);

    public static IValueConverter RunningToVisibility { get; } = new Lambda(v =>
        v is Core.Camera.CameraState.Running ? Visibility.Visible : Visibility.Collapsed);

    public static IValueConverter NotCircle { get; } = new Lambda(v => v is not Core.Camera.LayerShape.Circle);

    public static IValueConverter RoundedToVisibility { get; } = new Lambda(v =>
        v is Core.Camera.LayerShape.RoundedRectangle ? Visibility.Visible : Visibility.Collapsed);

    /// <summary>Human-readable names for enums shown in combo boxes.</summary>
    public static IValueConverter EnumLabel { get; } = new Lambda(v => v switch
    {
        Core.Camera.LayerShape.Rectangle => "Rectangle",
        Core.Camera.LayerShape.RoundedRectangle => "Rounded rectangle",
        Core.Camera.LayerShape.Circle => "Circle",
        Core.Camera.LayerAspect.Wide16x9 => "16:9 wide",
        Core.Camera.LayerAspect.Standard4x3 => "4:3 classic",
        Core.Camera.LayerAspect.Square1x1 => "1:1 square",
        Core.Camera.LayerAspect.Portrait9x16 => "9:16 portrait",
        Core.Camera.BackgroundMode.Original => "Original",
        Core.Camera.BackgroundMode.Blur => "Blur",
        Core.Camera.BackgroundMode.Remove => "Remove",
        Core.Camera.BackgroundMode.Image => "Image",
        Core.Camera.SegmentationQuality.Performance => "Performance (fastest)",
        Core.Camera.SegmentationQuality.Balanced => "Balanced (recommended)",
        Core.Camera.SegmentationQuality.Quality => "Quality (sharpest edges)",
        Core.Camera.BorderAnimation.None => "None",
        Core.Camera.BorderAnimation.Pulse => "Pulse",
        Core.Camera.BorderAnimation.ColorCycle => "Colour cycle",
        Core.Camera.AudioReactiveStyle.BorderPulse => "Border grows with your voice",
        Core.Camera.AudioReactiveStyle.GlowRing => "Glow ring lights up",
        Core.Camera.AudioReactiveStyle.SpeakingIndicator => "Border changes colour while you speak",
        Core.Camera.BaseFit.Fit => "Fit (whole picture, may show bars)",
        Core.Camera.BaseFit.Fill => "Fill (no bars, edges cropped)",
        _ => v?.ToString(),
    });

    public static IValueConverter StateToBrush { get; } = new Lambda(v => v switch
    {
        StreamState.Live => Brush("LiveBrush"),
        StreamState.Connecting or StreamState.Reconnecting or StreamState.Preparing or StreamState.Stopping => Brush("ConnectingBrush"),
        StreamState.Failed => Brush("FailedBrush"),
        _ => Brush("IdleBrush"),
    });

    private static object Brush(string key) => Application.Current.TryFindResource(key) ?? Brushes.Gray;

    private sealed class Lambda(Func<object?, object?> convert) : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => convert(value);

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}

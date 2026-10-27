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

    public static IValueConverter NullToVisibility { get; } = new Lambda(v => v is null ? Visibility.Visible : Visibility.Collapsed);

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
        true => Brush("Linkast.Success"),
        false => Brush("Linkast.Error"),
        _ => Brush("Linkast.TextSecondary"),
    });

    public static IValueConverter ProblemBrush { get; } = new Lambda(v => v is true
        ? Brush("Linkast.Warning")
        : Brush("Linkast.TextSecondary"));

    /// <summary>Accent colour for "a new version is ready", secondary text otherwise.</summary>
    public static IValueConverter UpdateBrush { get; } = new Lambda(v => v is true
        ? Brush("Linkast.Accent")
        : Brush("Linkast.TextSecondary"));

    public static IValueConverter BoldWhenTrue { get; } = new Lambda(v => v is true ? FontWeights.SemiBold : FontWeights.Normal);

    public static IValueConverter MuteLabel { get; } = new Lambda(v => v is true ? "Muted" : "Mute");

    /// <summary>Visible when a count is 0, for "nothing here yet" hints.</summary>
    public static IValueConverter ZeroToVisibility { get; } = new Lambda(v => v is 0 ? Visibility.Visible : Visibility.Collapsed);

    public static IValueConverter InverseBool { get; } = new Lambda(v => v is not true);

    public static IValueConverter InverseBoolToVisibility { get; } = new Lambda(v => v is true ? Visibility.Collapsed : Visibility.Visible);

    public static IValueConverter NonZeroToVisibility { get; } = new Lambda(v => v is int n && n != 0 ? Visibility.Visible : Visibility.Collapsed);

    public static IValueConverter RunningToVisibility { get; } = new Lambda(v =>
        v is Core.Camera.CameraState.Running ? Visibility.Visible : Visibility.Collapsed);

    /// <summary>The camera placeholder: hidden while pictures arrive, so it never shows through a removed background.</summary>
    public static IValueConverter NotRunningToVisibility { get; } = new Lambda(v =>
        v is Core.Camera.CameraState.Running ? Visibility.Collapsed : Visibility.Visible);

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
        Core.Camera.OverlayPosition.FullScreen => "Full screen",
        Core.Camera.OverlayPosition.TopLeft => "Top left",
        Core.Camera.OverlayPosition.TopCenter => "Top center",
        Core.Camera.OverlayPosition.TopRight => "Top right",
        Core.Camera.OverlayPosition.MiddleLeft => "Middle left",
        Core.Camera.OverlayPosition.Center => "Center",
        Core.Camera.OverlayPosition.MiddleRight => "Middle right",
        Core.Camera.OverlayPosition.BottomLeft => "Bottom left",
        Core.Camera.OverlayPosition.BottomCenter => "Bottom center",
        Core.Camera.OverlayPosition.BottomRight => "Bottom right",
        Core.Camera.OverlayPosition.Custom => "Custom (dragged in the preview)",
        Core.Camera.SceneKind.Gaming => "Gaming: game with camera",
        Core.Camera.SceneKind.JustChatting => "Just chatting: large camera",
        Core.Camera.SceneKind.StartingSoon => "Starting soon",
        Core.Camera.SceneKind.BeRightBack => "Be right back",
        Core.Camera.SceneKind.Ending => "Ending: thanks for watching",
        Core.Camera.SceneKind.Custom => "Empty scene",
        SceneBackgroundChoice.None => "None",
        SceneBackgroundChoice.Card => "Linkast card",
        SceneBackgroundChoice.Picture => "Picture",
        SceneBackgroundChoice.Video => "Video (loops)",
        _ => v?.ToString(),
    });

    public static IValueConverter StateToBrush { get; } = new Lambda(v => v switch
    {
        StreamState.Live => Brush("Linkast.Live"),
        StreamState.Connecting or StreamState.Reconnecting or StreamState.Preparing or StreamState.Stopping => Brush("Linkast.Warning"),
        StreamState.Failed => Brush("Linkast.Error"),
        _ => Brush("Linkast.Idle"),
    });

    /// <summary>The status pill's style: red when live, amber while changing state, red-orange after an error.</summary>
    public static IValueConverter StateToPillStyle { get; } = new Lambda(v => Application.Current.TryFindResource(v switch
    {
        StreamState.Live => "Linkast.StatusPill.Live",
        StreamState.Connecting or StreamState.Reconnecting or StreamState.Preparing or StreamState.Stopping => "Linkast.StatusPill.Warning",
        StreamState.Failed => "Linkast.StatusPill.Error",
        _ => "Linkast.StatusPill",
    }));

    /// <summary>Short, upper-case pill text. The full state is spelled out in the status title next to it.</summary>
    public static IValueConverter StateToPillText { get; } = new Lambda(v => v switch
    {
        StreamState.Live => "● LIVE",
        StreamState.Preparing => "PREPARING",
        StreamState.Connecting => "CONNECTING",
        StreamState.Reconnecting => "RECONNECTING",
        StreamState.Stopping => "STOPPING",
        StreamState.Failed => "ERROR",
        _ => "OFFLINE",
    });

    /// <summary>Status title from (State, IsReady).</summary>
    public static IMultiValueConverter StatusTitle { get; } = new MultiLambda(v => (v[0], v[1]) switch
    {
        (StreamState.Live, _) => "Streaming normally",
        (StreamState.Preparing, _) => "Getting ready…",
        (StreamState.Connecting, _) => "Connecting…",
        (StreamState.Reconnecting, _) => "Reconnecting…",
        (StreamState.Stopping, _) => "Ending stream…",
        (StreamState.Failed, _) => "Stopped with an error",
        (_, true) => "Ready to stream",
        _ => "Not ready yet",
    });

    /// <summary>Short shape names for segmented controls.</summary>
    public static IValueConverter ShapeShort { get; } = new Lambda(v => v switch
    {
        Core.Camera.LayerShape.RoundedRectangle => "Rounded",
        Core.Camera.LayerShape.Circle => "Circle",
        _ => "Rectangle",
    });

    /// <summary>One-word gameplay fit for segmented controls; the full explanation goes in the tooltip.</summary>
    public static IValueConverter BaseFitShort { get; } = new Lambda(v => v switch
    {
        Core.Camera.BaseFit.Fill => "Fill",
        _ => "Fit",
    });

    /// <summary>"Vertical (9:16): game on top, camera below" → "Vertical (9:16)" for segmented controls.</summary>
    public static IValueConverter LayoutShort { get; } = new Lambda(v => v?.ToString() is { } name && name.IndexOf("):", StringComparison.Ordinal) is var i and > 0
        ? name[..(i + 1)]
        : v?.ToString());

    public static IValueConverter NetworkErrorToVisibility { get; } = new Lambda(v =>
        v is ErrorCategory.Network ? Visibility.Visible : Visibility.Collapsed);

    /// <summary>Segoe Fluent Icons glyph for a network choice.</summary>
    public static IValueConverter NetworkGlyph { get; } = new Lambda(v => v is NetworkOption { Adapter: { } adapter }
        ? adapter.Kind switch
        {
            Networking.NetworkAdapterKind.WiFi => "\uE701",
            Networking.NetworkAdapterKind.MobileBroadband => "\uEC05",
            Networking.NetworkAdapterKind.UsbTethering => "\uE8EA",
            Networking.NetworkAdapterKind.Vpn => "\uE705",
            _ => "\uE839",
        }
        : "\uE774");

    private static object Brush(string key) => Application.Current.TryFindResource(key) ?? Brushes.Gray;

    private sealed class MultiLambda(Func<object?[], object?> convert) : IMultiValueConverter
    {
        public object? Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) => convert(values);

        public object?[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class Lambda(Func<object?, object?> convert) : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => convert(value);

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}

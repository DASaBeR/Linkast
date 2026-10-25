// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linkast.Core.Camera;
using Linkast.Core.Settings;
using Linkast.Platform.Windows.VirtualCamera;

namespace Linkast.App.ViewModels;

/// <summary>
/// Linkast Camera: a virtual camera for Zoom, Teams, Discord and others, showing one camera layer
/// with its look. The camera is a system component installed once with an administrator prompt;
/// Linkast itself never needs admin rights.
/// </summary>
public sealed partial class WebcamStudioViewModel
{
    private VirtualCameraOutput? _virtualCamera;
    private string? _virtualCameraLayerId;

    public bool VirtualCameraSupported => VirtualCameraInstaller.IsWindows11;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VirtualCameraInstalled), nameof(VirtualCameraCanInstall))]
    [NotifyCanExecuteChangedFor(nameof(InstallVirtualCameraCommand), nameof(RemoveVirtualCameraCommand))]
    public partial VirtualCameraSupport VirtualCameraSupport { get; set; } = VirtualCameraSupport.NotInstalled;

    public bool VirtualCameraInstalled => VirtualCameraSupport == VirtualCameraSupport.Installed;

    public bool VirtualCameraCanInstall => VirtualCameraSupport == VirtualCameraSupport.NotInstalled;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallVirtualCameraCommand), nameof(RemoveVirtualCameraCommand))]
    public partial bool VirtualCameraBusy { get; set; }

    [ObservableProperty]
    public partial string VirtualCameraStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool VirtualCameraProblem { get; set; }

    [ObservableProperty]
    public partial bool VirtualCameraEnabled { get; set; } = true;

    [ObservableProperty]
    public partial CameraLayerViewModel? VirtualCameraLayer { get; set; }

    partial void OnVirtualCameraEnabledChanged(bool value)
    {
        if (_virtualCamera is not null)
        {
            _virtualCamera.Enabled = value && VirtualCameraInstalled;
        }

        UpdateVirtualCameraStatus();
        Changed();
    }

    partial void OnVirtualCameraLayerChanged(CameraLayerViewModel? value)
    {
        if (_virtualCamera is not null)
        {
            _virtualCamera.Layer = value?.Layer;
        }

        Changed();
    }

    /// <summary>Starts the Linkast Camera feed (Windows 11) and checks whether the camera is installed.</summary>
    public async Task InitializeVirtualCameraAsync()
    {
        if (!VirtualCameraSupported)
        {
            VirtualCameraSupport = VirtualCameraSupport.NeedsWindows11;
            VirtualCameraStatus = "The Linkast Camera needs Windows 11.";
            return;
        }

        _virtualCamera ??= new VirtualCameraOutput(_ctx.Cameras, _ctx.Segmentation, _ctx.Images, _ctx.Framing);
        _virtualCamera.SetStandbyFrame(RenderStandbyFrame());
        _virtualCamera.StateChanged += (_, _) => _ctx.Ui.InvokeAsync(UpdateVirtualCameraStatus);
        _virtualCamera.Layer = VirtualCameraLayer?.Layer;
        await RefreshVirtualCameraAsync();
    }

    [RelayCommand]
    private async Task RefreshVirtualCameraAsync()
    {
        if (!VirtualCameraSupported)
        {
            return;
        }

        var status = await VirtualCameraInstaller.GetStatusAsync();
        VirtualCameraSupport = status.Support;
        if (_virtualCamera is not null)
        {
            _virtualCamera.Enabled = VirtualCameraEnabled && VirtualCameraInstalled;
        }

        UpdateVirtualCameraStatus();
    }

    private bool CanInstallVirtualCamera() => !VirtualCameraBusy && VirtualCameraSupport is VirtualCameraSupport.NotInstalled or VirtualCameraSupport.Installed;

    /// <summary>Installs, or updates, the Linkast Camera. Windows asks for administrator permission once.</summary>
    [RelayCommand(CanExecute = nameof(CanInstallVirtualCamera))]
    private async Task InstallVirtualCameraAsync()
    {
        VirtualCameraBusy = true;
        VirtualCameraStatus = "Waiting for Windows' administrator permission…";
        VirtualCameraProblem = false;
        string? problem = null;
        try
        {
            problem = await VirtualCameraInstaller.InstallAsync();
            await RefreshVirtualCameraAsync();
        }
        finally
        {
            VirtualCameraBusy = false;
        }

        ShowVirtualCameraResult(problem);
    }

    private bool CanRemoveVirtualCamera() => !VirtualCameraBusy && VirtualCameraInstalled;

    [RelayCommand(CanExecute = nameof(CanRemoveVirtualCamera))]
    private async Task RemoveVirtualCameraAsync()
    {
        VirtualCameraBusy = true;
        VirtualCameraStatus = "Waiting for Windows' administrator permission…";
        VirtualCameraProblem = false;
        string? problem = null;
        try
        {
            if (_virtualCamera is not null)
            {
                _virtualCamera.Enabled = false;
            }

            problem = await VirtualCameraInstaller.RemoveAsync();
            await RefreshVirtualCameraAsync();
        }
        finally
        {
            VirtualCameraBusy = false;
        }

        ShowVirtualCameraResult(problem);
    }

    /// <summary>After installing or removing: the error, or the normal status.</summary>
    private void ShowVirtualCameraResult(string? problem)
    {
        UpdateVirtualCameraStatus();
        if (problem is not null)
        {
            VirtualCameraStatus = problem;
            VirtualCameraProblem = true;
        }
    }

    private void UpdateVirtualCameraStatus()
    {
        if (VirtualCameraBusy)
        {
            return;
        }

        VirtualCameraProblem = false;
        VirtualCameraStatus = VirtualCameraSupport switch
        {
            VirtualCameraSupport.NeedsWindows11 => "The Linkast Camera needs Windows 11.",
            VirtualCameraSupport.ComponentsMissing => "The virtual camera components aren't part of this build. Use the installed version of Linkast.",
            VirtualCameraSupport.NotInstalled => "Not set up yet. Setting it up asks Windows for administrator permission once.",
            _ when _virtualCamera?.NeedsComponentUpdate == true => "The Linkast Camera on this PC belongs to another Linkast version. Press “Repair or update”.",
            _ when !VirtualCameraEnabled => "Set up. Turned off: apps using it see “Linkast is not running”.",
            _ => _virtualCamera?.State switch
            {
                VirtualCameraState.Serving => string.Create(CultureInfo.CurrentCulture, $"In use by an app · {_virtualCamera.FramesDelivered:N0} frames sent"),
                _ => "Ready. Choose “Linkast Camera” as the camera in Zoom, Teams, Discord, Meet or OBS.",
            },
        };
    }

    private void LoadVirtualCamera(VirtualCameraSettings settings)
    {
        _virtualCameraLayerId = settings.LayerId;
        VirtualCameraEnabled = settings.Enabled;
        VirtualCameraLayer = Layers.FirstOrDefault(l => l.LayerId == settings.LayerId) ?? Layers.FirstOrDefault();
    }

    private VirtualCameraSettings VirtualCameraToSettings() => new()
    {
        Enabled = VirtualCameraEnabled,
        LayerId = VirtualCameraLayer?.LayerId ?? _virtualCameraLayerId,
    };

    /// <summary>Keeps the chosen layer valid when cameras are added or removed.</summary>
    private void ReconcileVirtualCameraLayer()
    {
        if (VirtualCameraLayer is null || !Layers.Contains(VirtualCameraLayer))
        {
            VirtualCameraLayer = Layers.FirstOrDefault();
        }
    }

    /// <summary>1280 x 720 "camera is off" picture in the brand style, shown while the chosen camera is off.</summary>
    private static byte[] RenderStandbyFrame()
    {
        const int w = VirtualCameraProtocol.Width, h = VirtualCameraProtocol.Height;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var background = new LinearGradientBrush(Color.FromRgb(0x1D, 0x1B, 0x48), Color.FromRgb(0x09, 0x0A, 0x1C), 90);
            dc.DrawRectangle(background, null, new Rect(0, 0, w, h));
            var logo = new BitmapImage(new Uri("pack://application:,,,/Assets/linkast-logo.png"));
            dc.DrawImage(logo, new Rect((w - 260) / 2.0, 150, 260, 260));

            var face = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var text = new FormattedText("Camera is off", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, 40, Brushes.White, 1.0);
            dc.DrawText(text, new Point((w - text.Width) / 2, 450));
        }

        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[VirtualCameraProtocol.FrameBytes];
        bitmap.CopyPixels(pixels, w * 4, 0);
        return pixels;
    }
}

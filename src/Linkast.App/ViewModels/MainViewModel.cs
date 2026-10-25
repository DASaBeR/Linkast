// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linkast.Core;
using Linkast.Core.Audio;
using Linkast.Core.Camera;
using Linkast.Core.Settings;
using Linkast.App.Services;
using Linkast.Engine.FFmpeg;
using Linkast.Platform.Windows.Audio;
using Linkast.Platform.Windows.Camera;
using Linkast.Platform.Windows.Capture;
using Microsoft.Extensions.Logging;

namespace Linkast.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private const int MaxLogLines = 400;

    private readonly IOutputTransport _transport;
    private readonly IAudioInputProvider _audioProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MainViewModel> _logger;
    private readonly Dispatcher _ui;
    private readonly IVideoLayerInputProvider _layerProvider;
    private readonly IOverlayInputProvider _overlayProvider;
    private readonly JsonSettingsStore _settingsStore;
    private readonly DpapiSecretStore _secrets;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _snapshotTimer;
    private FfmpegStreamEngine? _engine;
    private IReadOnlyList<EncoderProbeResult> _probeResults = [];
    private LinkastSettings _loaded = new();
    private bool _loading = true;
    private readonly Dictionary<string, NormalizedRect?> _pendingMoves = [];
    private bool _moveInFlight;
    private readonly HashSet<string> _liveLayerIds = [];
    private readonly AudioLevelMonitor _levelMonitor;
    private readonly AudioLevelBus _systemLevels = new();
    private readonly AudioLevelBus _microphoneLevels = new();
    private readonly AudioLevelMonitor _systemMeter;
    private readonly AudioLevelMonitor _microphoneMeter;
    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _webOverlayTimer;
    private readonly WebOverlayHost _webOverlays;
    private bool _syncingWebOverlays;
    private readonly UpdateService _updates;

    public MainViewModel(
        IOutputTransport transport,
        IAudioInputProvider audioProvider,
        ICameraService cameras,
        ISegmentationService segmentation,
        BackgroundImageCache backgroundImages,
        IFramingService framing,
        AudioCaptureHub audioHub,
        AudioLevelBus audioLevels,
        IVideoLayerInputProvider layerProvider,
        IOverlayInputProvider overlayProvider,
        JsonSettingsStore settingsStore,
        DpapiSecretStore secrets,
        WebOverlayHost webOverlays,
        UpdateService updates,
        ILoggerFactory loggerFactory)
    {
        _updates = updates;
        _transport = transport;
        _audioProvider = audioProvider;
        _layerProvider = layerProvider;
        _overlayProvider = overlayProvider;
        _settingsStore = settingsStore;
        _secrets = secrets;
        _webOverlays = webOverlays;
        Overlays = new OverlaysViewModel(secrets);
        Overlays.ReloadRequested += (_, item) => _webOverlays.Get(item.LayerId)?.Reload();
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<MainViewModel>();
        _ui = Application.Current.Dispatcher;
        SelectedSourceKind = SourceKinds[0];
        SelectedResolution = Resolutions[0];
        SelectedDestinationKind = DestinationKinds[0];

        _levelMonitor = new AudioLevelMonitor(audioHub, audioLevels);
        _systemMeter = new AudioLevelMonitor(audioHub, _systemLevels, loopback: true);
        _microphoneMeter = new AudioLevelMonitor(audioHub, _microphoneLevels);
        _meterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(66), DispatcherPriority.Background, (_, _) => ReadStreamMeters(), _ui) { IsEnabled = false };
        Webcam = new WebcamStudioViewModel(cameras, _ui, segmentation, backgroundImages, framing, audioLevels);
        Webcam.SettingsChanged += (_, _) => ScheduleSave();
        Webcam.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(WebcamStudioViewModel.CanvasWidth) or nameof(WebcamStudioViewModel.CanvasHeight))
            {
                Overlays.SetCanvas(Webcam.CanvasWidth, Webcam.CanvasHeight);
            }
        };
        Overlays.SettingsChanged += (_, _) => ScheduleSave();
        Overlays.LiveChanged += (_, change) => OnOverlayLiveChanged(change);
        Overlays.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(OverlaysViewModel.IsAway) or nameof(OverlaysViewModel.AwayMuteSystemAudio))
            {
                ApplyLiveGain(AudioChannel.System, SystemGain);
            }

            if (e.PropertyName == nameof(OverlaysViewModel.IsAway))
            {
                AppendLog(Overlays.IsAway ? "Showing the Be right back screen." : "Back from the Be right back screen.");
            }
        };
        Webcam.LayerPlacementChanged += (_, layer) => QueueLiveMove(layer);
        Webcam.LayersChanged += (_, _) => UpdateAudioLevelMonitor();
        Webcam.LayoutSwitching = OnLayoutSwitchingAsync;

        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) => OnNetworkChanged();
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (_, _) => OnNetworkChanged();

        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) => SaveNow(), _ui) { IsEnabled = false };
        _snapshotTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => _ = RefreshEditorBackgroundAsync(), _ui) { IsEnabled = false };
        _webOverlayTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => _ = SyncWebOverlaysAsync(), _ui);
    }

    /// <summary>Webcam Studio.</summary>
    public WebcamStudioViewModel Webcam { get; }

    /// <summary>Pictures on top of the stream and the "Be right back" screen.</summary>
    public OverlaysViewModel Overlays { get; }

    // ---- Network for the stream ------------------------------------------------------------

    public System.Collections.ObjectModel.ObservableCollection<NetworkOption> NetworkOptions { get; } = [NetworkOption.Automatic];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NetworkDetails))]
    public partial NetworkOption SelectedNetwork { get; set; } = NetworkOption.Automatic;

    public string NetworkDetails => SelectedNetwork?.Details ?? string.Empty;

    [ObservableProperty]
    public partial string? NetworkTestText { get; set; }

    /// <summary>True = last test succeeded, false = failed, null = not tested.</summary>
    [ObservableProperty]
    public partial bool? NetworkTestOk { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    public partial bool IsTestingNetwork { get; set; }

    private string? _savedNetworkId;
    private string? _savedNetworkName;
    private DispatcherTimer? _networkChangeTimer;

    /// <summary>Rebuilds the adapter list, keeping the current choice even if that adapter disappeared.</summary>
    [RelayCommand]
    private void RefreshNetworks()
    {
        var keepId = SelectedNetwork?.Id ?? _savedNetworkId;
        var keepName = SelectedNetwork?.IsAutomatic == false ? SelectedNetwork.Name : _savedNetworkName;
        var adapters = Linkast.Networking.NetworkAdapterCatalog.List()
            .Where(a => a.IsUp || a.Kind is Linkast.Networking.NetworkAdapterKind.Ethernet or Linkast.Networking.NetworkAdapterKind.WiFi
                or Linkast.Networking.NetworkAdapterKind.UsbTethering or Linkast.Networking.NetworkAdapterKind.MobileBroadband)
            .ToList();

        var wasLoading = _loading;
        _loading = true;
        NetworkOptions.Clear();
        NetworkOptions.Add(NetworkOption.Automatic);
        foreach (var a in adapters)
        {
            NetworkOptions.Add(new NetworkOption(a.Id, a.Name, a));
        }

        NetworkOption? selected = keepId is null ? NetworkOption.Automatic : NetworkOptions.FirstOrDefault(o => string.Equals(o.Id, keepId, StringComparison.OrdinalIgnoreCase));
        if (selected is null && keepId is not null)
        {
            // Never silently fall back: keep showing the user's choice as "not connected".
            selected = new NetworkOption(keepId, keepName ?? "Selected network", null);
            NetworkOptions.Add(selected);
        }

        SelectedNetwork = selected ?? NetworkOption.Automatic;
        OnPropertyChanged(nameof(NetworkDetails));
        _loading = wasLoading;
    }

    partial void OnSelectedNetworkChanged(NetworkOption value)
    {
        NetworkTestText = null;
        NetworkTestOk = null;
    }

    private bool CanTestConnection() => !IsTestingNetwork;

    /// <summary>Connects to the configured server through the selected network and reports what really happened.</summary>
    [RelayCommand(CanExecute = nameof(CanTestConnection))]
    private async Task TestConnectionAsync()
    {
        if (!Uri.TryCreate(ServerUrl.Trim(), UriKind.Absolute, out var server) || (server.Scheme != "rtmp" && server.Scheme != "rtmps"))
        {
            NetworkTestOk = false;
            NetworkTestText = "Enter the server URL first (rtmp:// or rtmps://).";
            return;
        }

        IsTestingNetwork = true;
        NetworkTestOk = null;
        NetworkTestText = $"Connecting to {server.Host} through {(SelectedNetwork.IsAutomatic ? "the Windows default network" : SelectedNetwork.Name)}…";
        try
        {
            var r = await Linkast.Networking.NetworkProbe.TestAsync(server, SelectedNetwork.ToSelection());
            NetworkTestOk = r.Success;
            var defaultRoute = r.DefaultRouteAdapter is null ? string.Empty : $" Without a choice, Windows would use '{r.DefaultRouteAdapter}'.";
            NetworkTestText = r.Success
                ? $"Connected to {r.RemoteEndpoint} through '{r.AdapterUsed}' from {r.LocalEndpoint} in {r.ConnectTime?.TotalMilliseconds:0} ms" +
                  $"{(r.UsedAdapterDns ? ", using that network's own DNS" : string.Empty)}.{defaultRoute}"
                : $"{r.Error?.UserMessage}{defaultRoute}";
            AppendLog("Connection test: " + NetworkTestText);
        }
        finally
        {
            IsTestingNetwork = false;
        }
    }

    private void OnNetworkChanged()
    {
        _ui.InvokeAsync(() =>
        {
            _networkChangeTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
            {
                _networkChangeTimer!.Stop();
                RefreshNetworks();
            }, _ui);
            _networkChangeTimer.Stop();
            _networkChangeTimer.Start();
        });
    }

    /// <summary>Raised once after start-up with the saved stream key, so the view can fill its PasswordBox.</summary>
    public event EventHandler<Secret>? StreamKeyLoaded;

    /// <summary>Set by the view while the output preview or the layout editor is on screen; drives the 2-second capture snapshots.</summary>
    [ObservableProperty]
    public partial bool IsPreviewVisible { get; set; }

    partial void OnIsPreviewVisibleChanged(bool value)
    {
        _snapshotTimer.IsEnabled = value;
        if (value)
        {
            _ = RefreshEditorBackgroundAsync();
        }
    }

    // ---- Appearance ------------------------------------------------------------------------

    public IReadOnlyList<ThemeOption> Themes => ThemeOption.All;

    /// <summary>Dark by default. Applied immediately; App also applies the saved choice before the window shows.</summary>
    [ObservableProperty]
    public partial ThemeOption SelectedTheme { get; set; } = ThemeOption.All[0];

    partial void OnSelectedThemeChanged(ThemeOption value)
    {
        if (value is not null)
        {
            ThemeService.Apply(value.Theme);
        }
    }

    // ---- Version and updates ---------------------------------------------------------------

    public string VersionText { get; } = "Linkast " + UpdateService.CurrentVersion;

    /// <summary>Quiet status next to the button. Empty after a start-up check that found nothing.</summary>
    [ObservableProperty]
    public partial string UpdateText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    public partial bool UpdateAvailable { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand), nameof(InstallUpdateCommand))]
    public partial bool IsUpdating { get; set; }

    private bool CanCheckForUpdates() => !IsUpdating;

    /// <summary>Manual check: always reports the outcome, as text.</summary>
    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private Task CheckForUpdatesAsync() => RunUpdateCheckAsync(startup: false);

    /// <summary>
    /// The one automatic check, at start-up. Only a newer version is shown; "up to date", "not
    /// installed" and network problems stay silent so nothing distracts from streaming.
    /// </summary>
    private async Task RunUpdateCheckAsync(bool startup)
    {
        IsUpdating = true;
        if (!startup)
        {
            UpdateText = "Checking for updates…";
        }

        try
        {
            var result = await _updates.CheckAsync();
            UpdateAvailable = result.Status == UpdateCheckStatus.Available;
            if (!startup || UpdateAvailable)
            {
                UpdateText = result.Message;
            }

            if (!startup)
            {
                AppendLog("Update check: " + result.Message);
            }
        }
        finally
        {
            IsUpdating = false;
        }
    }

    private bool CanInstallUpdate() => UpdateAvailable && !IsUpdating && !IsActive;

    /// <summary>Downloads and installs only when the user asks, and never while live.</summary>
    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private async Task InstallUpdateAsync()
    {
        IsUpdating = true;
        try
        {
            SaveNow();
            await _updates.InstallAsync(new Progress<int>(p => UpdateText = $"Downloading update… {p}%"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            UpdateText = "The update couldn't be downloaded. Try again later.";
            AppendLog("Update failed: " + ex.Message);
        }
        finally
        {
            IsUpdating = false;
        }
    }

    // ---- Readiness -------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(ToggleRecordingCommand))]
    [NotifyPropertyChangedFor(nameof(StatusTitleText))]
    public partial bool IsReady { get; set; }

    [ObservableProperty]
    public partial string ReadinessText { get; set; } = "Checking FFmpeg and encoders…";

    [ObservableProperty]
    public partial string FfmpegDescription { get; set; } = string.Empty;

    // ---- Video source ----------------------------------------------------------------------

    public IReadOnlyList<VideoSourceKindOption> SourceKinds { get; } =
    [
        new(VideoSourceKind.Display, "Entire display"),
        new(VideoSourceKind.Window, "Single window or game"),
        new(VideoSourceKind.CameraOnly, "Webcam only (no screen)"),
        new(VideoSourceKind.TestPattern, "Test pattern"),
    ];

    public ObservableCollection<MonitorInfo> Monitors { get; } = [];

    public ObservableCollection<WindowInfo> Windows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisplaySource), nameof(IsWindowSource))]
    public partial VideoSourceKindOption SelectedSourceKind { get; set; }

    public bool IsDisplaySource => SelectedSourceKind.Kind == VideoSourceKind.Display;

    public bool IsWindowSource => SelectedSourceKind.Kind == VideoSourceKind.Window;

    [ObservableProperty]
    public partial MonitorInfo? SelectedMonitor { get; set; }

    [ObservableProperty]
    public partial WindowInfo? SelectedWindow { get; set; }

    [ObservableProperty]
    public partial bool UseDesktopDuplication { get; set; }

    [ObservableProperty]
    public partial bool CaptureCursor { get; set; } = true;

    // ---- Audio -----------------------------------------------------------------------------

    public ObservableCollection<AudioDevice> OutputDevices { get; } = [];

    public ObservableCollection<AudioDevice> InputDevices { get; } = [];

    [ObservableProperty]
    public partial bool SystemAudioEnabled { get; set; } = true;

    [ObservableProperty]
    public partial AudioDevice? SelectedOutputDevice { get; set; }

    [ObservableProperty]
    public partial double SystemAudioVolume { get; set; } = 100;

    [ObservableProperty]
    public partial bool MicrophoneEnabled { get; set; }

    [ObservableProperty]
    public partial AudioDevice? SelectedInputDevice { get; set; }

    [ObservableProperty]
    public partial double MicrophoneVolume { get; set; } = 100;

    /// <summary>Set by the view while the Stream page is on screen; the level meters only run then.</summary>
    [ObservableProperty]
    public partial bool IsStreamPageVisible { get; set; }

    /// <summary>System audio level, 0 to 100, for the meter on the Stream page.</summary>
    [ObservableProperty]
    public partial double SystemAudioLevel { get; set; }

    /// <summary>Microphone level, 0 to 100. The microphone is only metered while it is switched on.</summary>
    [ObservableProperty]
    public partial double MicrophoneLevel { get; set; }

    partial void OnIsStreamPageVisibleChanged(bool value) => UpdateStreamMeters();

    partial void OnSystemAudioEnabledChanged(bool value) => UpdateStreamMeters();

    partial void OnMicrophoneEnabledChanged(bool value)
    {
        UpdateStreamMeters();
        OnPropertyChanged(nameof(ShowMicrophoneMuted));
    }

    /// <summary>Shown in the header on every page, so a muted microphone is never forgotten.</summary>
    public bool ShowMicrophoneMuted => MicrophoneMuted && MicrophoneEnabled;

    /// <summary>Mutes the microphone in the stream instantly, without stopping it. Not saved: every start is unmuted.</summary>
    [ObservableProperty]
    public partial bool MicrophoneMuted { get; set; }

    /// <summary>Remove steady background noise (fans, hum) from the microphone. Can be switched while live.</summary>
    [ObservableProperty]
    public partial bool MicrophoneNoiseSuppression { get; set; }

    private double MicrophoneGain => MicrophoneMuted ? 0 : MicrophoneVolume / 100.0;

    [RelayCommand]
    private void ToggleMicrophoneMute() => MicrophoneMuted = !MicrophoneMuted;

    partial void OnMicrophoneMutedChanged(bool value)
    {
        AppendLog(value ? "Microphone muted." : "Microphone unmuted.");
        OnPropertyChanged(nameof(ShowMicrophoneMuted));
        ApplyLiveGain(AudioChannel.Microphone, MicrophoneGain);
    }

    partial void OnMicrophoneVolumeChanged(double value) => ApplyLiveGain(AudioChannel.Microphone, MicrophoneGain);

    partial void OnSystemAudioVolumeChanged(double value) => ApplyLiveGain(AudioChannel.System, SystemGain);

    /// <summary>System audio volume, silenced while the Be right back screen is up (if the user chose that).</summary>
    private double SystemGain => Overlays.IsAway && Overlays.AwayMuteSystemAudio ? 0 : SystemAudioVolume / 100.0;

    partial void OnMicrophoneNoiseSuppressionChanged(bool value)
    {
        if (_engine is { } engine && IsActive)
        {
            _ = engine.SetNoiseSuppressionAsync(value);
        }
    }

    /// <summary>Volume and mute apply to a running stream at once, and to its reconnects.</summary>
    private void ApplyLiveGain(AudioChannel channel, double gain)
    {
        if (_engine is { } engine && IsActive)
        {
            _ = engine.SetGainAsync(channel, gain);
        }
    }

    // ---- Platform ----------------------------------------------------------------------------

    public IReadOnlyList<StreamPlatformInfo> Platforms => StreamPlatforms.All;

    /// <summary>A preset fills in the server; Custom lets the user type it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomPlatform))]
    public partial StreamPlatformInfo SelectedPlatform { get; set; } = StreamPlatforms.Get(StreamPlatform.Custom);

    public bool IsCustomPlatform => SelectedPlatform.Platform == StreamPlatform.Custom;

    partial void OnSelectedPlatformChanged(StreamPlatformInfo value)
    {
        if (!_loading && value.Server is { } server)
        {
            ServerUrl = server;
            SelectedDestinationKind = DestinationKinds.First(d => d.Kind == DestinationKind.Direct);
        }
    }

    /// <summary>Opens the platform's page that shows the stream key, in the default browser.</summary>
    [RelayCommand]
    private void OpenStreamKeyPage()
    {
        if (SelectedPlatform.KeyPage is { } page)
        {
            Process.Start(new ProcessStartInfo(page) { UseShellExecute = true });
        }
    }

    private void UpdateStreamMeters()
    {
        var system = IsStreamPageVisible && SystemAudioEnabled;
        var microphone = IsStreamPageVisible && MicrophoneEnabled;
        if (system)
        {
            _systemMeter.Start(SelectedOutputDevice?.Id);
        }
        else
        {
            _systemMeter.Stop();
            SystemAudioLevel = 0;
        }

        if (microphone)
        {
            _microphoneMeter.Start(SelectedInputDevice?.Id);
        }
        else
        {
            _microphoneMeter.Stop();
            MicrophoneLevel = 0;
        }

        _meterTimer.IsEnabled = system || microphone;
    }

    private void ReadStreamMeters()
    {
        SystemAudioLevel = _systemLevels.Active ? _systemLevels.Meter.Level * 100 : 0;
        MicrophoneLevel = _microphoneLevels.Active ? _microphoneLevels.Meter.Level * 100 : 0;
    }

    // ---- Encoding --------------------------------------------------------------------------

    public ObservableCollection<EncoderInfo> Encoders { get; } = [];

    [ObservableProperty]
    public partial EncoderInfo? SelectedEncoder { get; set; }

    [ObservableProperty]
    public partial string EncoderNote { get; set; } = string.Empty;

    public IReadOnlyList<ResolutionOption> Resolutions { get; } =
        [new(1920, 1080), new(1600, 900), new(1280, 720), new(2560, 1440), new(854, 480)];

    [ObservableProperty]
    public partial ResolutionOption SelectedResolution { get; set; }

    public IReadOnlyList<int> FrameRates { get; } = [60, 30];

    [ObservableProperty]
    public partial int SelectedFrameRate { get; set; } = 60;

    [ObservableProperty]
    public partial int VideoBitrateKbps { get; set; } = 6000;

    [ObservableProperty]
    public partial int KeyframeIntervalSeconds { get; set; } = 2;

    [ObservableProperty]
    public partial int AudioBitrateKbps { get; set; } = 160;

    [ObservableProperty]
    public partial string? EncoderPreset { get; set; }

    // ---- Destination -----------------------------------------------------------------------

    public IReadOnlyList<DestinationKindOption> DestinationKinds => DestinationKindOption.All;

    [ObservableProperty]
    public partial DestinationKindOption SelectedDestinationKind { get; set; }

    [ObservableProperty]
    public partial string ServerUrl { get; set; } = string.Empty;

    /// <summary>Set from the view's PasswordBox. Never bound as plain text. Saved encrypted with DPAPI.</summary>
    public Secret StreamKey
    {
        get;
        set
        {
            field = value ?? new Secret(string.Empty);
            if (!_loading)
            {
                _secrets.Set(DpapiSecretStore.StreamKeyName, field);
            }
        }
    } = new(string.Empty);

    // ---- Status ----------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(CanEditSettings), nameof(StatusText), nameof(IsBroadcasting), nameof(IsRecording), nameof(IsRecordingOnly), nameof(RecordButtonText), nameof(PillText), nameof(StatusTitleText))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(InstallUpdateCommand), nameof(ToggleRecordingCommand))]
    public partial StreamState State { get; set; } = StreamState.Idle;

    /// <summary>The running session goes to a platform (not only to a file).</summary>
    public bool IsBroadcasting => IsActive && !_recordOnly;

    /// <summary>The running session writes a file (alone or while streaming).</summary>
    public bool IsRecording => IsActive && _recording;

    public bool IsRecordingOnly => IsActive && _recordOnly;

    public string RecordButtonText => IsRecordingOnly ? "Stop recording" : "Record";

    private bool _recordOnly;
    private bool _recording;

    /// <summary>Short, upper-case state pill. Recording without streaming never says LIVE.</summary>
    public string PillText => State switch
    {
        StreamState.Live when _recordOnly => "● REC",
        StreamState.Live when _recording => "● LIVE · REC",
        StreamState.Live => "● LIVE",
        StreamState.Preparing => "PREPARING",
        StreamState.Connecting when _recordOnly => "STARTING",
        StreamState.Connecting => "CONNECTING",
        StreamState.Reconnecting => "RECONNECTING",
        StreamState.Stopping => "STOPPING",
        StreamState.Failed => "ERROR",
        _ => "OFFLINE",
    };

    public string StatusTitleText => State switch
    {
        StreamState.Live when _recordOnly => "Recording (not live)",
        StreamState.Live when _recording => "Streaming and recording",
        StreamState.Live => "Streaming normally",
        StreamState.Preparing => "Getting ready…",
        StreamState.Connecting when _recordOnly => "Starting recording…",
        StreamState.Connecting => "Connecting…",
        StreamState.Reconnecting => "Reconnecting…",
        StreamState.Stopping when _recordOnly => "Saving recording…",
        StreamState.Stopping => "Ending stream…",
        StreamState.Failed => "Stopped with an error",
        _ when IsReady => "Ready to stream",
        _ => "Not ready yet",
    };

    public bool IsActive => StreamStateMachine.IsActive(State);

    public bool CanEditSettings => !IsActive;

    public string StatusText => State switch
    {
        StreamState.Idle => "Offline",
        StreamState.Preparing => "Preparing…",
        StreamState.Connecting when _recordOnly => "Starting recording…",
        StreamState.Connecting => "Connecting…",
        StreamState.Live when _recordOnly => "REC",
        StreamState.Live => "LIVE",
        StreamState.Reconnecting => "Reconnecting…",
        StreamState.Stopping => "Stopping…",
        StreamState.Stopped => "Offline",
        StreamState.Failed => "Stopped with an error",
        _ => State.ToString(),
    };

    [ObservableProperty]
    public partial string ElapsedText { get; set; } = "00:00:00";

    [ObservableProperty]
    public partial string FpsText { get; set; } = "–";

    [ObservableProperty]
    public partial string UploadText { get; set; } = "–";

    [ObservableProperty]
    public partial string EncoderText { get; set; } = "–";

    [ObservableProperty]
    public partial string DroppedText { get; set; } = "–";

    [ObservableProperty]
    public partial string ReconnectsText { get; set; } = "0";

    [ObservableProperty]
    public partial string InterfaceText { get; set; } = "–";

    [ObservableProperty]
    public partial string? WarningText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial StreamError? LastError { get; set; }

    public bool HasError => LastError is not null;

    public ObservableCollection<string> LogLines { get; } = [];

    // ---- Lifecycle -------------------------------------------------------------------------

    public async Task InitializeAsync()
    {
        RefreshSources();
        _loaded = _settingsStore.Load();
        SelectedTheme = ThemeOption.For(_loaded.Theme);
        ApplyStreamSettings(_loaded.Stream);
        Webcam.SetResolution(SelectedResolution.Width, SelectedResolution.Height);
        Webcam.Load(_loaded.Webcam, _loaded.CustomPresets);
        Overlays.SetCanvas(Webcam.CanvasWidth, Webcam.CanvasHeight);
        Overlays.Load(_loaded.Overlays);
        await Webcam.RefreshDevicesAsync();
        UpdateAudioLevelMonitor();
        await Webcam.InitializeVirtualCameraAsync();
        if (_settingsStore.LastOutcome is SettingsLoadOutcome.Corrupt)
        {
            AppendLog("Saved settings could not be read and were reset. A copy of the old file was kept in the Linkast folder.");
        }

        if (_secrets.Get(DpapiSecretStore.StreamKeyName) is { IsEmpty: false } key)
        {
            Redactor.Shared.Register(key);
            StreamKey = key;
            StreamKeyLoaded?.Invoke(this, key);
        }

        _loading = false;

        try
        {
            var ffmpeg = await FfmpegLocator.LocateAsync();
            FfmpegDescription = ffmpeg.Describe();
            AppendLog("Using " + ffmpeg.Describe());

            ReadinessText = "Testing encoders on this PC…";
            _probeResults = await new EncoderCatalog(ffmpeg).ProbeAsync();
            Encoders.Clear();
            foreach (var r in _probeResults.Where(r => r.Works))
            {
                Encoders.Add(r.Encoder);
            }

            foreach (var r in _probeResults)
            {
                AppendLog($"Encoder {r.Encoder.Id}: {(r.Works ? "available" : "unavailable – " + r.FailureReason)}");
            }

            var wasLoading = _loading;
            _loading = true;
            SelectedEncoder = Encoders.FirstOrDefault(e => e.Id == _loaded.Stream.EncoderId)
                ?? Encoders.FirstOrDefault(e => e.Kind == EncoderKind.Hardware)
                ?? Encoders.FirstOrDefault();
            _loading = wasLoading;
            EncoderNote = SelectedEncoder switch
            {
                null => "No working H.264 encoder was found. Streaming is not possible.",
                { Kind: EncoderKind.Software } => "No hardware encoder works on this PC, so the software encoder (x264) is used. It needs a fast CPU.",
                _ => $"{Encoders.Count(e => e.Kind == EncoderKind.Hardware)} hardware encoder(s) available.",
            };

            _engine = new FfmpegStreamEngine(ffmpeg, _transport, _audioProvider, _loggerFactory.CreateLogger<FfmpegStreamEngine>(), layerProvider: _layerProvider, overlayProvider: _overlayProvider);
            _engine.StateChanged += (_, e) => _ui.InvokeAsync(() => OnStateChanged(e));
            _engine.TelemetryUpdated += (_, t) => _ui.InvokeAsync(() => OnTelemetry(t));
            _engine.DiagnosticLine += (_, line) => _ui.InvokeAsync(() => AppendLog(line));
            _engine.RecordingSaved += (_, path) => _ui.InvokeAsync(() => OnRecordingSavedAsync(path));

            IsReady = SelectedEncoder is not null;
            ReadinessText = IsReady ? "Ready" : "Not ready";
        }
        catch (StreamFailureException ex)
        {
            ReadinessText = ex.Error.UserMessage;
            LastError = ex.Error;
            AppendLog(ex.Error.ToString());
        }

        // The one automatic update check per run (see UpdateService for the policy).
        await RunUpdateCheckAsync(startup: true);
    }

    public async ValueTask DisposeAsync()
    {
        _snapshotTimer.Stop();
        if (_saveTimer.IsEnabled)
        {
            SaveNow();
        }

        if (_engine is not null)
        {
            await _engine.DisposeAsync();
        }

        _levelMonitor.Dispose();
        _systemMeter.Dispose();
        _microphoneMeter.Dispose();
        await Webcam.DisposeAsync();
    }

    // ---- Persistence -----------------------------------------------------------------------

    private static readonly HashSet<string> PersistedProperties =
    [
        nameof(SelectedSourceKind), nameof(SelectedMonitor), nameof(SelectedWindow), nameof(UseDesktopDuplication), nameof(CaptureCursor),
        nameof(SystemAudioEnabled), nameof(SelectedOutputDevice), nameof(SystemAudioVolume),
        nameof(MicrophoneEnabled), nameof(SelectedInputDevice), nameof(MicrophoneVolume), nameof(MicrophoneNoiseSuppression),
        nameof(SelectedEncoder), nameof(SelectedResolution), nameof(SelectedFrameRate), nameof(VideoBitrateKbps),
        nameof(KeyframeIntervalSeconds), nameof(AudioBitrateKbps), nameof(EncoderPreset),
        nameof(SelectedDestinationKind), nameof(SelectedPlatform), nameof(ServerUrl), nameof(SelectedNetwork), nameof(SelectedTheme),
        nameof(RecordingFolder), nameof(RecordWhenLive), nameof(RemuxRecordingsToMp4),
    ];

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is { } name && PersistedProperties.Contains(name))
        {
            ScheduleSave();
        }

        if (e.PropertyName is nameof(SelectedSourceKind) or nameof(SelectedMonitor) or nameof(SelectedWindow) && IsPreviewVisible)
        {
            _ = RefreshEditorBackgroundAsync();
        }

        // The constructor sets defaults before the studio exists.
        if (Webcam is null)
        {
            return;
        }

        if (e.PropertyName is nameof(SelectedResolution) && SelectedResolution is { } r)
        {
            Webcam.SetResolution(r.Width, r.Height);
        }

        if (e.PropertyName is nameof(SelectedInputDevice))
        {
            UpdateAudioLevelMonitor();
        }
    }

    // ---- Recording -------------------------------------------------------------------------

    [ObservableProperty]
    public partial string RecordingFolder { get; set; } = RecordingFiles.DefaultFolder;

    /// <summary>Every stream is also saved to a file.</summary>
    [ObservableProperty]
    public partial bool RecordWhenLive { get; set; }

    [ObservableProperty]
    public partial bool RemuxRecordingsToMp4 { get; set; }

    /// <summary>Where the last recording was saved, shown under the Record button.</summary>
    [ObservableProperty]
    public partial string? LastRecordingText { get; set; }

    [RelayCommand]
    private void ChooseRecordingFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Folder for recordings", InitialDirectory = Directory.Exists(RecordingFolder) ? RecordingFolder : null };
        if (dialog.ShowDialog() == true)
        {
            RecordingFolder = dialog.FolderName;
        }
    }

    [RelayCommand]
    private void OpenRecordingFolder()
    {
        try
        {
            Directory.CreateDirectory(RecordingFolder);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { RecordingFolder }, UseShellExecute = false });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AppendLog("The recordings folder could not be opened: " + ex.Message);
        }
    }

    /// <summary>A recording file was closed: report it, and turn it into an MP4 when asked to.</summary>
    private async Task OnRecordingSavedAsync(string path)
    {
        AppendLog("Recording saved: " + path);
        LastRecordingText = "Saved " + Path.GetFileName(path);
        if (!RemuxRecordingsToMp4 || _engine is null)
        {
            return;
        }

        LastRecordingText = "Making an MP4 of " + Path.GetFileName(path) + "…";
        var ffmpeg = _engine.Installation.Path;
        var mp4 = await Task.Run(() => FfmpegRemuxer.RemuxToMp4Async(ffmpeg, path));
        LastRecordingText = mp4 is null ? "Saved " + Path.GetFileName(path) + " (the MP4 copy failed; the MKV was kept)" : "Saved " + Path.GetFileName(mp4);
        AppendLog(mp4 is null ? "MP4 copy failed; kept " + path : "Recording saved as MP4: " + mp4);
    }

    private void ScheduleSave()
    {
        if (_loading)
        {
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        _saveTimer.Stop();
        _settingsStore.Save(new LinkastSettings
        {
            Theme = SelectedTheme.Theme,
            Stream = new StreamSettings
            {
                SourceKind = SelectedSourceKind.Kind.ToString(),
                MonitorDeviceName = SelectedMonitor?.DeviceName,
                WindowTitle = SelectedWindow?.Title,
                WindowProcess = SelectedWindow?.ProcessName,
                UseDesktopDuplication = UseDesktopDuplication,
                CaptureCursor = CaptureCursor,
                SystemAudioEnabled = SystemAudioEnabled,
                SystemAudioDeviceId = SelectedOutputDevice?.Id,
                SystemAudioVolume = SystemAudioVolume,
                MicrophoneEnabled = MicrophoneEnabled,
                MicrophoneDeviceId = SelectedInputDevice?.Id,
                MicrophoneVolume = MicrophoneVolume,
                MicrophoneNoiseSuppression = MicrophoneNoiseSuppression,
                EncoderId = SelectedEncoder?.Id ?? _loaded.Stream.EncoderId,
                Width = SelectedResolution.Width,
                Height = SelectedResolution.Height,
                FrameRate = SelectedFrameRate,
                VideoBitrateKbps = VideoBitrateKbps,
                KeyframeIntervalSeconds = KeyframeIntervalSeconds,
                AudioBitrateKbps = AudioBitrateKbps,
                EncoderPreset = EncoderPreset,
                DestinationKind = SelectedDestinationKind.Kind,
                Platform = SelectedPlatform.Platform,
                ServerUrl = ServerUrl,
                NetworkInterfaceId = SelectedNetwork?.Id,
                NetworkInterfaceName = SelectedNetwork?.IsAutomatic == false ? SelectedNetwork.Name : null,
                RecordingFolder = RecordingFolder == RecordingFiles.DefaultFolder ? null : RecordingFolder,
                RecordWhenLive = RecordWhenLive,
                RemuxRecordingsToMp4 = RemuxRecordingsToMp4,
            },
            Webcam = Webcam.ToSettings(),
            Overlays = Overlays.ToSettings(),
            CustomPresets = Webcam.CustomPresets,
        });
    }

    private void ApplyStreamSettings(StreamSettings s)
    {
        if (Enum.TryParse<VideoSourceKind>(s.SourceKind, out var kind))
        {
            SelectedSourceKind = SourceKinds.FirstOrDefault(k => k.Kind == kind) ?? SourceKinds[0];
        }

        SelectedMonitor = Monitors.FirstOrDefault(m => m.DeviceName == s.MonitorDeviceName) ?? SelectedMonitor;
        SelectedWindow = Windows.FirstOrDefault(w => w.Title == s.WindowTitle && w.ProcessName == s.WindowProcess)
            ?? Windows.FirstOrDefault(w => w.ProcessName is not null && w.ProcessName == s.WindowProcess)
            ?? SelectedWindow;
        UseDesktopDuplication = s.UseDesktopDuplication;
        CaptureCursor = s.CaptureCursor;
        SystemAudioEnabled = s.SystemAudioEnabled;
        SelectedOutputDevice = OutputDevices.FirstOrDefault(d => d.Id == s.SystemAudioDeviceId) ?? SelectedOutputDevice;
        SystemAudioVolume = s.SystemAudioVolume;
        MicrophoneEnabled = s.MicrophoneEnabled;
        SelectedInputDevice = InputDevices.FirstOrDefault(d => d.Id == s.MicrophoneDeviceId) ?? SelectedInputDevice;
        MicrophoneVolume = s.MicrophoneVolume;
        MicrophoneNoiseSuppression = s.MicrophoneNoiseSuppression;
        SelectedResolution = Resolutions.FirstOrDefault(r => r.Width == s.Width && r.Height == s.Height) ?? Resolutions[0];
        SelectedFrameRate = FrameRates.Contains(s.FrameRate) ? s.FrameRate : 60;
        VideoBitrateKbps = s.VideoBitrateKbps;
        KeyframeIntervalSeconds = s.KeyframeIntervalSeconds;
        AudioBitrateKbps = s.AudioBitrateKbps;
        EncoderPreset = s.EncoderPreset;
        SelectedDestinationKind = DestinationKinds.FirstOrDefault(d => d.Kind == s.DestinationKind) ?? DestinationKinds[0];
        ServerUrl = s.ServerUrl;
        SelectedPlatform = StreamPlatforms.Get(s.Platform != StreamPlatform.Custom ? s.Platform : StreamPlatforms.Detect(s.ServerUrl));
        RecordingFolder = string.IsNullOrWhiteSpace(s.RecordingFolder) ? RecordingFiles.DefaultFolder : s.RecordingFolder;
        RecordWhenLive = s.RecordWhenLive;
        RemuxRecordingsToMp4 = s.RemuxRecordingsToMp4;
        _savedNetworkId = s.NetworkInterfaceId;
        _savedNetworkName = s.NetworkInterfaceName;
        SelectedNetwork = NetworkOption.Automatic;
        if (_savedNetworkId is not null)
        {
            SelectedNetwork = new NetworkOption(_savedNetworkId, _savedNetworkName ?? "Selected network", null);
        }

        RefreshNetworks();
    }

    // ---- Live scene changes ----------------------------------------------------------------

    /// <summary>
    /// Sends the newest placement (or hide) of a camera to the running stream; intermediate drag
    /// positions are coalesced per camera.
    /// </summary>
    private void QueueLiveMove(CameraLayerViewModel layer)
    {
        if (_engine is null || State != StreamState.Live)
        {
            return;
        }

        if (!_liveLayerIds.Contains(layer.LayerId))
        {
            if (layer.IsShown && layer.HasCamera)
            {
                Webcam.Notice = $"{layer.Name} will appear the next time you go live.";
            }

            return;
        }

        _pendingMoves[layer.LayerId] = layer.IsShown ? layer.Placement : null;
        _ = SendPendingMovesAsync();
    }

    /// <summary>
    /// Runs the web overlays' pages only while they are needed (the preview is on screen or a stream is
    /// running), so they cost nothing otherwise, and refreshes their picture in the preview once a second.
    /// </summary>
    private async Task SyncWebOverlaysAsync()
    {
        if (_syncingWebOverlays)
        {
            return;
        }

        _syncingWebOverlays = true;
        try
        {
            var webs = Overlays.Items.Where(o => o.IsWeb && o.HasUrl).ToList();
            var needed = IsActive || IsStreamPageVisible || IsPreviewVisible;
            await _webOverlays.KeepOnlyAsync(needed ? [.. webs.Select(w => w.LayerId)] : []);
            if (!needed)
            {
                return;
            }

            foreach (var web in webs)
            {
                var renderer = await _webOverlays.EnsureAsync(web.LayerId, web.Url!, web.PageWidth, web.PageHeight);
                web.Problem = _webOverlays.ProblemOf(web.LayerId);
                if (renderer is not null && (IsStreamPageVisible || IsPreviewVisible))
                {
                    web.SetSnapshot(renderer.Snapshot());
                }
            }
        }
        finally
        {
            _syncingWebOverlays = false;
        }
    }

    /// <summary>
    /// An overlay moved, appeared or disappeared, or the away screen was switched. While live this uses
    /// the same instant path as camera moves; an overlay added after going live appears next time.
    /// </summary>
    private void OnOverlayLiveChanged(OverlayLiveChange change)
    {
        if (!IsActive)
        {
            return;
        }

        if (!_liveLayerIds.Contains(change.LayerId))
        {
            if (change.Placement is not null)
            {
                AppendLog("The new overlay will appear the next time you go live.");
            }

            return;
        }

        _pendingMoves[change.LayerId] = change.Placement;
        _ = SendPendingMovesAsync();
    }

    private async Task SendPendingMovesAsync()
    {
        if (_moveInFlight || _engine is null)
        {
            return;
        }

        _moveInFlight = true;
        try
        {
            while (_pendingMoves.Count > 0)
            {
                var (id, placement) = _pendingMoves.First();
                _pendingMoves.Remove(id);
                var ok = placement is { } p
                    ? await _engine.MoveLayerAsync(id, p)
                    : await _engine.HideLayerAsync(id);
                if (!ok)
                {
                    AppendLog("The change will apply the next time you go live.");
                    _pendingMoves.Clear();
                    break;
                }
            }
        }
        finally
        {
            _moveInFlight = false;
        }
    }

    /// <summary>
    /// Decides whether a layout switch can happen. Offline it always can. Live, a switch that only
    /// moves cameras happens instantly; one that changes the output (orientation or gameplay area) or
    /// shows a camera that is not in the stream needs a short restart, which the user confirms first.
    /// </summary>
    private Task<bool> OnLayoutSwitchingAsync(SceneLayout from, SceneLayout to)
    {
        if (_engine is null || !IsActive)
        {
            return Task.FromResult(true);
        }

        var shown = Webcam.Layers.Where(l => l.HasCamera && to.CameraPlacements.ContainsKey(l.LayerId));
        if (State == StreamState.Live && to.CanSwitchLiveFrom(from) && shown.All(l => _liveLayerIds.Contains(l.LayerId)))
        {
            foreach (var id in _liveLayerIds)
            {
                _pendingMoves[id] = to.CameraPlacements.TryGetValue(id, out var p) ? p : null;
            }

            _ = SendPendingMovesAsync();
            AppendLog($"Switched to layout '{to.Name}' without restarting.");
            return Task.FromResult(true);
        }

        var reason = to.Orientation != from.Orientation
            ? $"'{to.Name}' changes the stream to {(to.Orientation == OutputOrientation.Vertical ? "vertical" : "landscape")}."
            : $"'{to.Name}' changes the gameplay area or shows a camera that isn't in the current stream.";
        var answer = MessageBox.Show(
            Application.Current.MainWindow,
            reason + "\n\nThe stream has to restart to apply it, which takes a few seconds. Restart the stream now?",
            "Switch layout",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return Task.FromResult(false);
        }

        // The studio applies the layout as soon as this returns; the restart then picks it up.
        _ = _ui.BeginInvoke(async () =>
        {
            AppendLog($"Restarting the stream for layout '{to.Name}'.");
            await StopAsync();
            await StartAsync();
        }, DispatcherPriority.Background);
        return Task.FromResult(true);
    }

    /// <summary>Meters the microphone only while a camera that is on uses audio-reactive effects.</summary>
    private void UpdateAudioLevelMonitor()
    {
        if (Webcam.NeedsAudioLevel)
        {
            _levelMonitor.Start(SelectedInputDevice?.Id);
            Webcam.AudioProblem = _levelMonitor.Problem;
        }
        else
        {
            _levelMonitor.Stop();
            Webcam.AudioProblem = null;
        }
    }

    private async Task RefreshEditorBackgroundAsync()
    {
        var kind = SelectedSourceKind.Kind;
        var monitor = SelectedMonitor;
        var window = SelectedWindow;
        if (kind is VideoSourceKind.CameraOnly or VideoSourceKind.TestPattern)
        {
            Webcam.EditorBackground = null;
            return;
        }

        var frame = await Task.Run(() => kind == VideoSourceKind.Window && window is not null
            ? ScreenSnapshot.CaptureWindow(window.Handle)
            : monitor is not null ? ScreenSnapshot.CaptureMonitor(monitor) : null);
        if (frame is null)
        {
            return;
        }

        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(frame.Width, frame.Height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, frame.Pixels, frame.Stride);
        bitmap.Freeze();
        Webcam.EditorBackground = bitmap;
    }

    // ---- Commands --------------------------------------------------------------------------

    [RelayCommand]
    private void RefreshSources()
    {
        Replace(Monitors, VideoSourceCatalog.ListMonitors(), ref _selectedMonitorKey, m => m.DeviceName, v => SelectedMonitor = v,
            fallback: list => list.FirstOrDefault(m => m.IsPrimary) ?? list.FirstOrDefault());
        Replace(Windows, VideoSourceCatalog.ListWindows(), ref _selectedWindowKey, w => w.Handle.ToString(CultureInfo.InvariantCulture), v => SelectedWindow = v,
            fallback: list => list.FirstOrDefault());

        try
        {
            Replace(OutputDevices, AudioDeviceCatalog.List(AudioDeviceKind.Output), ref _selectedOutputKey, d => d.Id, v => SelectedOutputDevice = v,
                fallback: list => list.FirstOrDefault(d => d.IsDefault) ?? list.FirstOrDefault());
            Replace(InputDevices, AudioDeviceCatalog.List(AudioDeviceKind.Input), ref _selectedInputKey, d => d.Id, v => SelectedInputDevice = v,
                fallback: list => list.FirstOrDefault(d => d.IsDefault) ?? list.FirstOrDefault());
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            AppendLog("Audio devices could not be listed: " + ex.Message);
        }
    }

    private string? _selectedMonitorKey;
    private string? _selectedWindowKey;
    private string? _selectedOutputKey;
    private string? _selectedInputKey;

    partial void OnSelectedMonitorChanged(MonitorInfo? value) => _selectedMonitorKey = value?.DeviceName;

    partial void OnSelectedWindowChanged(WindowInfo? value) => _selectedWindowKey = value?.Handle.ToString(CultureInfo.InvariantCulture);

    partial void OnSelectedOutputDeviceChanged(AudioDevice? value)
    {
        _selectedOutputKey = value?.Id;
        UpdateStreamMeters();
    }

    partial void OnSelectedInputDeviceChanged(AudioDevice? value)
    {
        _selectedInputKey = value?.Id;
        UpdateStreamMeters();
    }

    private bool CanStart() => IsReady && !IsActive;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => StartSessionAsync(recordOnly: false);

    private bool CanToggleRecording() => IsRecordingOnly ? State != StreamState.Stopping : IsReady && !IsActive;

    /// <summary>Records without going live, or stops that recording. A stream records when "Also record" is on.</summary>
    [RelayCommand(CanExecute = nameof(CanToggleRecording))]
    private async Task ToggleRecordingAsync()
    {
        if (IsRecordingOnly)
        {
            await StopAsync();
            return;
        }

        await StartSessionAsync(recordOnly: true);
    }

    private async Task StartSessionAsync(bool recordOnly)
    {
        if (_engine is null || SelectedEncoder is null)
        {
            return;
        }

        LastError = null;
        WarningText = null;

        var session = BuildSession(recordOnly, out var error);
        if (session is null)
        {
            LastError = error;
            return;
        }

        _recordOnly = recordOnly;
        _recording = session.Recording is not null;
        try
        {
            await _engine.StartAsync(session);
            if (recordOnly)
            {
                AppendLog($"Recording to {session.Recording!.Folder} with {SelectedEncoder.DisplayName}, {session.Encoder.Width}x{session.Encoder.Height}@{SelectedFrameRate}, {VideoBitrateKbps} kbps");
                return;
            }

            AppendLog($"Starting stream to {session.Destination.ServerUrl.Host} with {SelectedEncoder.DisplayName}, {session.Encoder.Width}x{session.Encoder.Height}@{SelectedFrameRate}, {VideoBitrateKbps} kbps, {session.CameraLayers.Count} camera(s), layout '{Webcam.ActiveLayout.Name}'");
        }
        catch (StreamFailureException ex)
        {
            LastError = ex.Error;
        }
        catch (InvalidOperationException ex)
        {
            AppendLog(ex.Message);
        }
    }

    private bool CanStop() => IsActive && State != StreamState.Stopping;

    [RelayCommand(CanExecute = nameof(CanStop))]
    public async Task StopAsync()
    {
        if (_engine is not null)
        {
            await _engine.StopAsync();
        }
    }

    [RelayCommand]
    private void CopyDiagnostics()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Linkast {typeof(MainViewModel).Assembly.GetName().Version}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"OS: {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{FfmpegDescription}");
        foreach (var r in _probeResults)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {r.Encoder.Id}: {(r.Works ? "ok" : r.FailureReason)}");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"State: {State}; last error: {LastError}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Layout: {Webcam.ActiveLayout.Name} ({Webcam.CanvasDescription})");
        foreach (var l in Webcam.Layers)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"Camera '{l.Name}': {l.SelectedDevice?.Name ?? "none"}; on: {l.CameraOn}; shown: {l.IsShown}; state: {l.State}; {l.DiagnosticsText}; error: {l.CameraError}");
        }
        sb.AppendLine("Recent log:");
        foreach (var line in LogLines.TakeLast(80))
        {
            sb.AppendLine(line);
        }

        Clipboard.SetText(Redactor.Shared.Redact(sb.ToString()));
        AppendLog("Diagnostics copied to the clipboard (stream keys removed).");
    }

    [RelayCommand]
    private static void OpenLogFolder() =>
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { App.LogDirectory }, UseShellExecute = false });

    // ---- Helpers ---------------------------------------------------------------------------

    private StreamSession? BuildSession(bool recordOnly, out StreamError? error)
    {
        error = null;
        VideoSourceSpec video;
        switch (SelectedSourceKind.Kind)
        {
            case VideoSourceKind.Display when SelectedMonitor is { } m:
                video = m.ToSource(UseDesktopDuplication ? CaptureApi.DesktopDuplication : CaptureApi.WindowsGraphicsCapture) with { CaptureCursor = CaptureCursor };
                break;
            case VideoSourceKind.Window when SelectedWindow is { } w:
                if (!VideoSourceCatalog.WindowExists(w.Handle))
                {
                    error = new StreamError(ErrorCategory.Capture, "capture.window.gone", "The selected window has closed. Refresh the list and pick it again.");
                    return null;
                }

                video = w.ToSource() with { CaptureCursor = CaptureCursor };
                break;
            case VideoSourceKind.TestPattern:
                video = new TestPatternSource();
                break;
            case VideoSourceKind.CameraOnly:
                if (!Webcam.Layers.Any(l => l.HasCamera && l.IsShown))
                {
                    error = new StreamError(ErrorCategory.Capture, "camera.none", "Webcam only needs a camera that is shown in the current layout. Select one on the Webcam tab.");
                    return null;
                }

                video = new BlankCanvasSource();
                break;
            default:
                error = new StreamError(ErrorCategory.Capture, "capture.none", "Choose what to capture.");
                return null;
        }

        var audio = new List<AudioSourceSpec>();
        if (SystemAudioEnabled)
        {
            audio.Add(new SystemAudioSource(SelectedOutputDevice?.Id, SelectedOutputDevice?.Name ?? "Default output") { Gain = SystemGain });
        }

        if (MicrophoneEnabled)
        {
            audio.Add(new MicrophoneSource(SelectedInputDevice?.Id, SelectedInputDevice?.Name ?? "Default microphone")
            {
                Gain = MicrophoneGain,
                NoiseSuppression = MicrophoneNoiseSuppression,
            });
        }

        var server = new Uri("rtmp://localhost/none");
        if (!recordOnly && !Uri.TryCreate(ServerUrl.Trim(), UriKind.Absolute, out server))
        {
            error = new StreamError(ErrorCategory.Configuration, "destination.url", "Enter a server URL such as rtmp://live.twitch.tv/app.");
            return null;
        }

        // A vertical layout streams the configured resolution rotated, for example 1080 × 1920.
        var layout = Webcam.ActiveLayout;
        var (canvasWidth, canvasHeight) = layout.CanvasSize(SelectedResolution.Width, SelectedResolution.Height);
        var encoder = new EncoderSettings(
            SelectedEncoder!.Id,
            canvasWidth,
            canvasHeight,
            SelectedFrameRate,
            VideoBitrateKbps,
            KeyframeIntervalSeconds,
            RateControl.ConstantBitrate,
            string.IsNullOrWhiteSpace(EncoderPreset) ? null : EncoderPreset.Trim(),
            AudioBitrateKbps);

        var cameras = Webcam.Layers
            .Where(l => l.HasCamera && layout.CameraPlacements.ContainsKey(l.LayerId))
            .Select(l => new CameraLayerBinding(l.Layer, layout.CameraPlacements[l.LayerId]))
            .ToList();
        _liveLayerIds.Clear();
        var overlays = Overlays.Bindings(canvasWidth, canvasHeight);
        _liveLayerIds.UnionWith(cameras.Select(c => c.Layer.LayerId));
        _liveLayerIds.UnionWith(overlays.Select(o => o.LayerId));
        Webcam.Notice = null;

        return new StreamSession(
            video,
            audio,
            encoder,
            new Destination(SelectedDestinationKind.Label, server, StreamKey, SelectedDestinationKind.Kind),
            SelectedNetwork.ToSelection(),
            ReconnectPolicy.Default)
        {
            // Every camera shown in the layout is included, bottom to top. The camera on/off switch
            // only makes a layer transparent, so cameras can be toggled live without a restart.
            CameraLayers = cameras,
            Overlays = overlays,
            BasePlacement = layout.BasePlacement,
            BaseFit = layout.BaseFit,
            Broadcast = !recordOnly,
            Recording = recordOnly || RecordWhenLive
                ? new RecordingSpec(RecordingFolder) { RemuxToMp4 = RemuxRecordingsToMp4 }
                : null,
        };
    }

    private void OnStateChanged(StreamStateChangedEventArgs e)
    {
        State = e.Current;
        if (e.Error is not null && e.Current is StreamState.Failed or StreamState.Reconnecting)
        {
            LastError = e.Error;
        }

        if (e.Current == StreamState.Live)
        {
            LastError = null;
        }

        if (e.Current is StreamState.Stopped or StreamState.Failed)
        {
            FpsText = UploadText = EncoderText = "–";
            WarningText = null;
        }
    }

    private void OnTelemetry(StreamTelemetry t)
    {
        ElapsedText = t.Uptime.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        ReconnectsText = t.ReconnectCount.ToString(CultureInfo.InvariantCulture);

        if (t.Encoder is { } e)
        {
            FpsText = $"{e.Fps:0.#} / {t.TargetFps}";
            EncoderText = e.Speed > 0 ? $"{e.Speed:0.00}× real time" : "–";
            DroppedText = (e.DroppedFrames + e.DuplicatedFrames).ToString(CultureInfo.InvariantCulture);
        }

        if (t.Network is { } n)
        {
            UploadText = n.OutgoingKbps > 0 ? $"{n.OutgoingKbps:0} kbps" : "–";
            InterfaceText = n.InterfaceName is null ? (n.LocalEndpoint ?? "–") : $"{n.InterfaceName}  ({n.LocalEndpoint})";
            if (n.Connected && n.InterfaceName is not null && SelectedNetwork is { IsAutomatic: false } chosen)
            {
                // The relay already refuses mismatches; this makes the verification visible.
                InterfaceText += string.Equals(n.InterfaceName, chosen.Name, StringComparison.OrdinalIgnoreCase)
                    ? "  ✓ verified: your chosen network"
                    : $"  ⚠ not '{chosen.Name}'";
            }
        }

        WarningText = t.State == StreamState.Live && t.EncoderOverloaded
            ? "Encoding is falling behind. Lower the resolution or frame rate, or choose a hardware encoder."
            : null;
    }

    private void AppendLog(string line)
    {
        LogLines.Add($"{DateTime.Now:HH:mm:ss}  {Redactor.Shared.Redact(line)}");
        while (LogLines.Count > MaxLogLines)
        {
            LogLines.RemoveAt(0);
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> items, ref string? selectedKey, Func<T, string> key, Action<T?> select, Func<IReadOnlyList<T>, T?> fallback)
        where T : class
    {
        var keep = selectedKey;
        target.Clear();
        foreach (var i in items)
        {
            target.Add(i);
        }

        select(items.FirstOrDefault(i => key(i) == keep) ?? fallback(items));
    }
}

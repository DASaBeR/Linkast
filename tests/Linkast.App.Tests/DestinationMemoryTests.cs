// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using Linkast.App.Services;
using Linkast.App.ViewModels;
using Linkast.Core;
using Linkast.Core.Audio;
using Linkast.Core.Camera;
using Linkast.Core.Settings;
using Linkast.Platform.Windows.Audio;
using Linkast.Platform.Windows.Camera;
using Microsoft.Extensions.Logging.Abstractions;

namespace Linkast.App.Tests;

public sealed class DestinationMemoryTests : IDisposable
{
    private readonly string _dir;
    private readonly string _settingsPath;
    private readonly string _secretsPath;

    public DestinationMemoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "linkast-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _settingsPath = Path.Combine(_dir, "settings.json");
        _secretsPath = Path.Combine(_dir, "secrets.dat");

        if (Application.Current is null)
        {
            _ = new Application();
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed class DummyTransport : IOutputTransport
    {
        public Task<IOutputLease> OpenAsync(Destination destination, NetworkSelection network, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class DummyAudioProvider : IAudioInputProvider
    {
        public bool CanOpen(AudioSourceSpec source) => false;
        public IAudioInput Open(AudioSourceSpec source) => throw new NotSupportedException();
    }

    private sealed class MockCameraService : ICameraService, IDisposable
    {
        public readonly MockCameraFeed Feed = new(new CameraDevice("mock-cam", "Mock Camera"));
        public event EventHandler? DevicesChanged { add { } remove { } }
        public Task<IReadOnlyList<CameraDevice>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CameraDevice>>([new CameraDevice("mock-cam", "Mock Camera")]);
        public Task<IReadOnlyList<CameraFormat>> GetFormatsAsync(string deviceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CameraFormat>>([]);
        public ICameraFeed Open(CameraDevice device, CameraFormatRequest format) => Feed;
        public void Dispose() => Feed.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class MockCameraFeed(CameraDevice device) : ICameraFeed
    {
        public CameraDevice Device => device;
        public CameraState State => CameraState.Running;
        public StreamError? LastError => null;
        public CameraFormat? ActiveFormat => new(640, 480, 30, 1, "MJPG");
        public double MeasuredFps => 30.0;
        public LatestFrameExchange Frames { get; } = new();
        public event EventHandler? StateChanged { add { } remove { } }
        public IReadOnlyList<HardwareControlInfo> HardwareControls => [];
        public Task<bool> SetHardwareControlAsync(HardwareControl control, double? value) => Task.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DummySegmentationService : ISegmentationService
    {
        public ISegmentationSubscription Subscribe(CameraDevice device, CameraFormatRequest format, SegmentationQuality quality, bool preferGpu) =>
            throw new NotSupportedException();
    }

    private sealed class DummyFramingService : IFramingService
    {
        public IFramingSubscription Subscribe(CameraDevice device, CameraFormatRequest format, string layerId, FramingSettings settings) =>
            throw new NotSupportedException();
    }

    private sealed class DummyLayerProvider : IVideoLayerInputProvider
    {
        public IVideoLayerInput Open(LiveCameraLayer layer, int width, int height, int frameRate) =>
            throw new NotSupportedException();
    }

    private sealed class DummyOverlayProvider : IOverlayInputProvider
    {
        public IVideoLayerInput Open(OverlayLayerBinding overlay, int width, int height, int maxFrameRate) =>
            throw new NotSupportedException();
    }

    private (MainViewModel Vm, JsonSettingsStore Settings, DpapiSecretStore Secrets) CreateApp()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var settingsStore = new JsonSettingsStore(NullLogger<JsonSettingsStore>.Instance, _settingsPath);
        var secrets = new DpapiSecretStore(_secretsPath);
        var transport = new DummyTransport();
        var audioHub = new AudioCaptureHub();
        var audioLevels = new AudioLevelBus();
        var audioProvider = new DummyAudioProvider();
        var cameras = new MockCameraService();
        var segmentation = new DummySegmentationService();
        var bgImages = new BackgroundImageCache();
        var framing = new DummyFramingService();
        var layerProvider = new DummyLayerProvider();
        var overlayProvider = new DummyOverlayProvider();
        var webOverlays = new WebOverlayHost();
        var updates = new UpdateService(NullLogger<UpdateService>.Instance);

        var vm = new MainViewModel(
            transport,
            audioProvider,
            cameras,
            segmentation,
            bgImages,
            framing,
            audioHub,
            audioLevels,
            layerProvider,
            overlayProvider,
            settingsStore,
            secrets,
            webOverlays,
            updates,
            loggerFactory);

        return (vm, settingsStore, secrets);
    }

    [Fact]
    public async Task Destination_tabs_remember_own_values_when_switching()
    {
        var (vm, _, _) = CreateApp();
        await vm.InitializeAsync();

        // 1. Enter Kick key K
        vm.SelectedPlatform = StreamPlatforms.Get(StreamPlatform.Kick);
        vm.StreamKey = new Secret("KickKey123");
        var kickServer = StreamPlatforms.Get(StreamPlatform.Kick).Server;
        Assert.Equal(kickServer, vm.ServerUrl);
        Assert.Equal("KickKey123", vm.StreamKey.Reveal());

        // Switch to Custom, enter URL U and key C
        vm.SelectedPlatform = StreamPlatforms.Get(StreamPlatform.Custom);
        vm.ServerUrl = "rtmp://custom.example.com/live";
        vm.StreamKey = new Secret("CustomKey456");
        Assert.Equal("rtmp://custom.example.com/live", vm.ServerUrl);
        Assert.Equal("CustomKey456", vm.StreamKey.Reveal());

        // Switch back to Kick -> key K and Kick server shown
        vm.SelectedPlatform = StreamPlatforms.Get(StreamPlatform.Kick);
        Assert.Equal(kickServer, vm.ServerUrl);
        Assert.Equal("KickKey123", vm.StreamKey.Reveal());

        // Switch back to Custom -> U and C shown
        vm.SelectedPlatform = StreamPlatforms.Get(StreamPlatform.Custom);
        Assert.Equal("rtmp://custom.example.com/live", vm.ServerUrl);
        Assert.Equal("CustomKey456", vm.StreamKey.Reveal());
    }

    [Fact]
    public async Task Restart_preserves_both_destinations_and_last_used_active_tab()
    {
        var (vm1, settingsStore, secrets) = CreateApp();
        await vm1.InitializeAsync();

        vm1.SelectedPlatform = StreamPlatforms.Get(StreamPlatform.Kick);
        vm1.StreamKey = new Secret("KickKey123");

        vm1.SelectedPlatform = StreamPlatforms.Get(StreamPlatform.Custom);
        vm1.ServerUrl = "rtmp://custom.example.com/live";
        vm1.StreamKey = new Secret("CustomKey456");

        // Save current settings (simulating shutdown)
        settingsStore.Save(vm1.CurrentSettings());

        // 2. Restart (new VM from the same stores)
        var (vm2, _, _) = CreateApp();
        await vm2.InitializeAsync();

        // The active tab is Custom (the last one used)
        Assert.Equal(StreamPlatform.Custom, vm2.SelectedPlatform.Platform);
        Assert.Equal("rtmp://custom.example.com/live", vm2.ServerUrl);
        Assert.Equal("CustomKey456", vm2.StreamKey.Reveal());

        // Switch to Kick -> still has Kick key and server
        vm2.SelectedPlatform = StreamPlatforms.Get(StreamPlatform.Kick);
        Assert.Equal(StreamPlatforms.Get(StreamPlatform.Kick).Server, vm2.ServerUrl);
        Assert.Equal("KickKey123", vm2.StreamKey.Reveal());
    }

    [Fact]
    public async Task Migration_copies_legacy_streamKey_to_saved_active_platform_and_removes_old()
    {
        var (vm, settingsStore, secrets) = CreateApp();

        // 3. Migration: only legacy destination.streamKey + Platform=Kick
        secrets.Set(DpapiSecretStore.StreamKeyName, new Secret("LegacySecret999"));
        settingsStore.Save(new LinkastSettings
        {
            Stream = new StreamSettings
            {
                Platform = StreamPlatform.Kick,
                ServerUrl = StreamPlatforms.Get(StreamPlatform.Kick).Server!,
            },
        });

        await vm.InitializeAsync();

        Assert.Equal(StreamPlatform.Kick, vm.SelectedPlatform.Platform);
        Assert.Equal("LegacySecret999", vm.StreamKey.Reveal());
        Assert.Null(secrets.Get(DpapiSecretStore.StreamKeyName));
        Assert.Equal("LegacySecret999", secrets.Get(DpapiSecretStore.StreamKeyNameFor(StreamPlatform.Kick))?.Reveal());
    }

    [Fact]
    public async Task Redactor_masks_keys_of_inactive_platforms()
    {
        var (vm, _, _) = CreateApp();
        await vm.InitializeAsync();

        vm.SelectedPlatform = StreamPlatforms.Get(StreamPlatform.Custom);
        vm.StreamKey = new Secret("CustomKeySecret");

        vm.SelectedPlatform = StreamPlatforms.Get(StreamPlatform.Kick);
        vm.StreamKey = new Secret("KickKeySecret");

        // Active is Kick; Custom is inactive
        Assert.Equal(StreamPlatform.Kick, vm.SelectedPlatform.Platform);

        var logMessage = "Publishing to stream with CustomKeySecret and KickKeySecret";
        var redacted = Redactor.Shared.Redact(logMessage);

        Assert.DoesNotContain("CustomKeySecret", redacted);
        Assert.DoesNotContain("KickKeySecret", redacted);
    }
}

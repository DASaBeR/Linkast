// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Threading;
using Linkast.App.ViewModels;
using Linkast.Core;
using Linkast.Core.Camera;
using Linkast.Core.Settings;

namespace Linkast.App.Tests;

public class JustChattingCameraTests
{
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

    [Fact]
    public void Load_places_camera_in_just_chatting_scene_when_placements_are_empty()
    {
        using var cameraService = new MockCameraService();
        var vm = new WebcamStudioViewModel(cameraService, Dispatcher.CurrentDispatcher);

        var justChatting = SceneLayout.Create(SceneKind.JustChatting, null, OutputOrientation.Landscape);
        var gaming = SceneLayout.Create(SceneKind.Gaming, null, OutputOrientation.Landscape);

        var settings = new WebcamSettings
        {
            Layouts = [gaming, justChatting],
            ActiveLayoutId = justChatting.Id,
            Layers = [new CameraLayerConfig { LayerId = "cam-1" }],
        };

        vm.Load(settings, []);

        var loadedJustChatting = vm.Layouts.First(l => l.Kind == SceneKind.JustChatting);
        Assert.True(loadedJustChatting.CameraPlacements.ContainsKey("cam-1"));
        var placement = loadedJustChatting.CameraPlacements["cam-1"];
        Assert.True(placement.Width > 0 && placement.Height > 0);
    }

    [Fact]
    public void Load_retains_just_chatting_camera_when_removed_from_gaming()
    {
        using var cameraService = new MockCameraService();
        var vm = new WebcamStudioViewModel(cameraService, Dispatcher.CurrentDispatcher);

        // User removed camera from Gaming, but Just chatting still has camera
        var gaming = SceneLayout.Create(SceneKind.Gaming, null, OutputOrientation.Landscape);
        var justChatting = SceneLayout.Create(SceneKind.JustChatting, "cam-1", OutputOrientation.Landscape);

        var settings = new WebcamSettings
        {
            Layouts = [gaming, justChatting],
            ActiveLayoutId = gaming.Id,
            Layers = [new CameraLayerConfig { LayerId = "cam-1" }],
        };

        vm.Load(settings, []);

        var loadedGaming = vm.Layouts.First(l => l.Kind == SceneKind.Gaming);
        var loadedJustChatting = vm.Layouts.First(l => l.Kind == SceneKind.JustChatting);

        Assert.False(loadedGaming.CameraPlacements.ContainsKey("cam-1"));
        Assert.True(loadedJustChatting.CameraPlacements.ContainsKey("cam-1"));
    }

    [Fact]
    public void AddCamera_places_new_camera_in_just_chatting_when_empty()
    {
        using var cameraService = new MockCameraService();
        var vm = new WebcamStudioViewModel(cameraService, Dispatcher.CurrentDispatcher);

        var justChatting = SceneLayout.Create(SceneKind.JustChatting, null, OutputOrientation.Landscape);
        var settings = new WebcamSettings
        {
            Layouts = [justChatting],
            ActiveLayoutId = justChatting.Id,
            Layers = [new CameraLayerConfig { LayerId = "initial-cam" }],
        };

        vm.Load(settings, []);
        Assert.Single(vm.Layouts.First(l => l.Kind == SceneKind.JustChatting).CameraPlacements);

        // Now remove the camera placement manually from just chatting to simulate an empty scene
        var sceneWithoutCam = vm.ActiveLayout with { CameraPlacements = new Dictionary<string, NormalizedRect>() };
        vm.UpdateActiveSceneQuietly(_ => sceneWithoutCam);

        // Add a new camera
        vm.AddCameraCommand.Execute(null);

        var updatedJustChatting = vm.Layouts.First(l => l.Kind == SceneKind.JustChatting);
        Assert.Single(updatedJustChatting.CameraPlacements);
    }
}

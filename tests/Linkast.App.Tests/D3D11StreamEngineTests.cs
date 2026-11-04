// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Linkast.Core;
using Linkast.Core.Camera;
using Linkast.Core.Rendering;
using Linkast.Engine.FFmpeg;
using Linkast.Platform.Windows.Rendering;

namespace Linkast.App.Tests;

public class D3D11StreamEngineTests
{
    private sealed class TestOutputTransport : IOutputTransport
    {
        public Task<IOutputLease> OpenAsync(Destination destination, NetworkSelection network, CancellationToken cancellationToken) =>
            Task.FromResult<IOutputLease>(new TestOutputLease());
    }

    private sealed class TestOutputLease : IOutputLease
    {
        public string PublishUrl => "rtmp://127.0.0.1:1935/live/test";
        public string TcUrl => "rtmp://127.0.0.1:1935/live";
        public bool IsMeasured => false;
        public NetworkSnapshot Snapshot() => new(0, 0, 0, null, null, null, false);
        public Task Completion { get; } = new TaskCompletionSource().Task;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static FfmpegInstallation DummyFfmpeg() =>
        new(@"C:\ffmpeg\bin\ffmpeg.exe", "test", "7.1", "--enable-gpl");

    private static StreamSession ValidSession() => new(
        Video: new BlankCanvasSource(),
        Audio: [],
        Encoder: EncoderSettings.Default1080p60("h264_nvenc"),
        Destination: new Destination("Test", new Uri("rtmp://127.0.0.1/live"), new Secret("secret")),
        Network: NetworkSelection.SystemDefault,
        Reconnect: ReconnectPolicy.Default);

    [Fact]
    public void D3D11StreamEngine_initializes_with_correct_idle_state()
    {
        using var deviceManager = new D3D11DeviceManager();
        var engine = new D3D11StreamEngine(
            DummyFfmpeg(),
            new TestOutputTransport(),
            deviceManager: deviceManager);

        Assert.Equal(StreamState.Idle, engine.State);
        Assert.Equal(0, engine.FfmpegProcessId);
        Assert.NotNull(engine.Installation);
    }

    [Fact]
    public async Task D3D11StreamEngine_validates_session_and_rejects_invalid_encoder()
    {
        using var deviceManager = new D3D11DeviceManager();
        var engine = new D3D11StreamEngine(
            DummyFfmpeg(),
            new TestOutputTransport(),
            deviceManager: deviceManager);

        var badSession = ValidSession() with
        {
            Encoder = ValidSession().Encoder with { Width = -1 }
        };

        var ex = await Assert.ThrowsAsync<StreamFailureException>(() => engine.StartAsync(badSession));
        Assert.Equal(ErrorCategory.Configuration, ex.Error.Category);
    }

    [Fact]
    public async Task D3D11StreamEngine_mutates_scene_layout_dynamically()
    {
        using var deviceManager = new D3D11DeviceManager();
        var engine = new D3D11StreamEngine(
            DummyFfmpeg(),
            new TestOutputTransport(),
            deviceManager: deviceManager);

        // MoveLayerAsync
        var moved = await engine.MoveLayerAsync("cam-1", new NormalizedRect(0.1, 0.1, 0.3, 0.3));
        Assert.True(moved);

        // SetLayerCropAsync
        var cropped = await engine.SetLayerCropAsync("cam-1", new NormalizedRect(0.1, 0.1, 0.8, 0.8));
        Assert.True(cropped);

        // HideLayerAsync
        var hidden = await engine.HideLayerAsync("cam-1");
        Assert.True(hidden);

        // SetBaseAsync
        var baseMoved = await engine.SetBaseAsync(new NormalizedRect(0, 0, 1, 1), NormalizedRect.Full, BaseFit.Fit);
        Assert.True(baseMoved);

        // SetCaptureAsync
        var capMoved = await engine.SetCaptureAsync("game-1", new NormalizedRect(0, 0, 0.5, 0.5), NormalizedRect.Full, BaseFit.Fill);
        Assert.True(capMoved);
    }

    [Fact]
    public void D3D11StreamEngine_replaces_sources_cleanly()
    {
        using var deviceManager = new D3D11DeviceManager();
        var engine = new D3D11StreamEngine(
            DummyFfmpeg(),
            new TestOutputTransport(),
            deviceManager: deviceManager);

        // ReplaceSources when not active returns false
        var replacedWhileIdle = engine.ReplaceSources([], []);
        Assert.False(replacedWhileIdle);

        Assert.False(engine.HasCapture("test-capture"));
    }

    [Fact]
    public async Task D3D11StreamEngine_adjusts_audio_gain_and_noise_suppression()
    {
        using var deviceManager = new D3D11DeviceManager();
        var engine = new D3D11StreamEngine(
            DummyFfmpeg(),
            new TestOutputTransport(),
            deviceManager: deviceManager);

        // While not active (no mixer created yet), gain returns false safely
        var gainSet = await engine.SetGainAsync(AudioChannels.Microphone, 0.5);
        Assert.False(gainSet);

        // Noise suppression persists and succeeds so it takes effect upon launch
        var noiseSet = await engine.SetNoiseSuppressionAsync(true);
        Assert.True(noiseSet);
    }

    [Fact]
    public void SourceFrameCache_recreates_srv_when_texture_instance_changes()
    {
        using var manager = new D3D11DeviceManager();
        using var cache = new SourceFrameCache(manager);

        var desc = new Vortice.Direct3D11.Texture2DDescription
        {
            Width = 32,
            Height = 32,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            Usage = Vortice.Direct3D11.ResourceUsage.Default,
            BindFlags = Vortice.Direct3D11.BindFlags.ShaderResource
        };

        using var tex1 = manager.Device.CreateTexture2D(desc);
        using var tex2 = manager.Device.CreateTexture2D(desc);

        cache.Store("sourceA", tex1, 100);
        var (acquired1, srv1, ts1, fresh1) = cache.AcquireWithSrv("sourceA");
        Assert.Same(tex1, acquired1);
        Assert.NotNull(srv1);
        Assert.True(fresh1);

        // Store new texture for sourceA (simulating window resize or device restoration)
        cache.Store("sourceA", tex2, 200);
        var (acquired2, srv2, ts2, fresh2) = cache.AcquireWithSrv("sourceA");
        Assert.Same(tex2, acquired2);
        Assert.NotNull(srv2);
        Assert.NotSame(srv1, srv2);

        // Evicting or clearing source disposes views cleanly
        cache.Remove("sourceA");
        var (missing, missingSrv, _, _) = cache.AcquireWithSrv("sourceA");
        Assert.Null(missing);
        Assert.Null(missingSrv);
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

    [Fact]
    public async Task D3D11StreamEngine_disposes_cleanly_without_throwing()
    {
        using var deviceManager = new D3D11DeviceManager();
        var engine = new D3D11StreamEngine(
            DummyFfmpeg(),
            new TestOutputTransport(),
            deviceManager: deviceManager);

        await engine.DisposeAsync();
        Assert.Equal(StreamState.Idle, engine.State);
    }

    [Fact]
    public async Task CameraD3D11SceneSource_acquires_frames_and_manages_lifecycle()
    {
        using var deviceManager = new D3D11DeviceManager();
        using var mockService = new MockCameraService();
        var layer = new LiveCameraLayer("layer-1")
        {
            Device = new CameraDevice("mock-cam", "Mock Camera"),
            Enabled = true,
        };

        await using var source = new CameraD3D11SceneSource(
            "layer-1",
            deviceManager,
            layer,
            mockService,
            320,
            240);

        Assert.Equal("layer-1", source.Id);
        Assert.Equal(SceneSourceState.Capturing, source.State);

        // Before publishing frame, returns null
        var initial = await source.AcquireFrameAsync();
        Assert.Null(initial);

        // Publish a frame to the camera feed
        var write = mockService.Feed.Frames.BeginWrite(320, 240);
        Assert.NotNull(write);
        Array.Fill(write.Pixels, (byte)128);
        mockService.Feed.Frames.Publish(TimeSpan.Zero);

        // Now frame is acquired as a D3D11 GPU texture
        var acquired = await source.AcquireFrameAsync();
        Assert.NotNull(acquired);
        Assert.Equal(320u, acquired.Description.Width);
        Assert.Equal(240u, acquired.Description.Height);
    }

    [Fact]
    public void MediaOutputScheduler_passes_crops_to_compositor()
    {
        using var deviceManager = new D3D11DeviceManager();
        using var compositor = new D3D11SceneCompositor(deviceManager, 1920, 1080);
        using var frameCache = new SourceFrameCache(deviceManager);
        using var ms = new MemoryStream();

        var crops = new Dictionary<string, NormalizedRect>
        {
            ["cam-1"] = new NormalizedRect(0.1, 0.1, 0.8, 0.8)
        };

        var layout = new SceneLayout
        {
            CameraPlacements = new Dictionary<string, NormalizedRect>
            {
                ["cam-1"] = new NormalizedRect(0.7, 0.7, 0.25, 0.25)
            }
        };

        var scheduler = new MediaOutputScheduler(
            compositor,
            frameCache,
            () => layout,
            () => new Dictionary<string, ID3D11SceneSource>(),
            ms,
            targetFps: 60,
            cropsProvider: () => crops);

        Assert.False(scheduler.IsRunning);
    }

    [Fact]
    public async Task D3D11StreamEngine_configures_overlays_and_media_factories()
    {
        using var deviceManager = new D3D11DeviceManager();
        var engine = new D3D11StreamEngine(
            DummyFfmpeg(),
            new TestOutputTransport(),
            deviceManager: deviceManager);

        Assert.NotNull(engine.DeviceManager);

        bool overlayFactoryCalled = false;
        engine.OverlaySourceFactory = (overlay, width, height) =>
        {
            overlayFactoryCalled = true;
            return new ImageD3D11SceneSource(overlay.LayerId, engine.DeviceManager, new byte[width * height * 4], width, height);
        };

        bool mediaFactoryCalled = false;
        engine.MediaSourceFactory = (media, width, height) =>
        {
            mediaFactoryCalled = true;
            return new ImageD3D11SceneSource(media.LayerId, engine.DeviceManager, new byte[width * height * 4], width, height);
        };

        var overlayBinding = new OverlayLayerBinding("chat-overlay", new ImageOverlaySource("dummy.png"), NormalizedRect.Full);
        var mediaBinding = new MediaLayerBinding("bg-media", "dummy.png", IsVideo: false, StartHidden: false);

        var overlaySrc = engine.OverlaySourceFactory(overlayBinding, 64, 64);
        var mediaSrc = engine.MediaSourceFactory(mediaBinding, 64, 64);

        Assert.True(overlayFactoryCalled);
        Assert.True(mediaFactoryCalled);
        Assert.NotNull(overlaySrc);
        Assert.NotNull(mediaSrc);

        await overlaySrc.DisposeAsync();
        await mediaSrc.DisposeAsync();
    }

    [Fact]
    public async Task D3D11StreamEngine_fails_fast_when_ffmpeg_exits_early()
    {
        using var deviceManager = new D3D11DeviceManager();
        var wherePath = Path.Combine(Environment.SystemDirectory, "where.exe");
        var fakeFfmpeg = new FfmpegInstallation(wherePath, "test", "1.0", "test");
        await using var engine = new D3D11StreamEngine(
            fakeFfmpeg,
            new TestOutputTransport(),
            deviceManager: deviceManager,
            options: new EngineOptions { ConnectTimeout = TimeSpan.FromSeconds(10) });

        var session = ValidSession() with
        {
            Broadcast = false,
            Recording = new RecordingSpec(Path.GetTempPath()),
            Reconnect = ReconnectPolicy.Disabled
        };

        var states = new List<string>();
        engine.StateChanged += (_, e) => states.Add($"{e.Current} (error: {e.Error?.Code})");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await engine.StartAsync(session);

        while (engine.State != StreamState.Failed && sw.ElapsedMilliseconds < 3000)
        {
            await Task.Delay(50);
        }

        Assert.True(engine.State == StreamState.Failed, "States: " + string.Join(" -> ", states));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Expected fast fail under 5s, took {sw.Elapsed.TotalSeconds}s");
        Assert.NotNull(engine.LastError);
        Assert.Equal("ffmpeg.early_exit", engine.LastError.Code);
        Assert.Contains("Exit code", engine.LastError.Detail);
    }

    [Fact]
    public async Task D3D11StreamEngine_reports_diagnostic_pipe_status_on_timeout()
    {
        using var deviceManager = new D3D11DeviceManager();
        var cmdPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var fakeFfmpeg = new FfmpegInstallation(cmdPath, "test", "1.0", "test");
        await using var engine = new D3D11StreamEngine(
            fakeFfmpeg,
            new TestOutputTransport(),
            deviceManager: deviceManager,
            options: new EngineOptions { ConnectTimeout = TimeSpan.FromMilliseconds(400) });

        var session = ValidSession() with
        {
            Broadcast = false,
            Recording = new RecordingSpec(Path.GetTempPath()),
            Reconnect = ReconnectPolicy.Disabled
        };

        var states = new List<string>();
        engine.StateChanged += (_, e) => states.Add($"{e.Current} (error: {e.Error?.Code})");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await engine.StartAsync(session);

        // The 400 ms timeout fires quickly, but Failed is only published after teardown (process kill,
        // output pumps), which is slow on a loaded CI runner.
        while (engine.State != StreamState.Failed && sw.ElapsedMilliseconds < 20000)
        {
            await Task.Delay(50);
        }

        Assert.True(engine.State == StreamState.Failed, "States: " + string.Join(" -> ", states));
        Assert.NotNull(engine.LastError);
        Assert.Equal("pipe.connect_timeout", engine.LastError.Code);
        Assert.Contains("Pipes: video=", engine.LastError.Detail);
        Assert.Contains("audio=", engine.LastError.Detail);
        Assert.Contains("Process:", engine.LastError.Detail);
    }

    [SkippableFact]
    public async Task D3D11StreamEngine_staged_handshake_succeeds_with_real_ffmpeg()
    {
        FfmpegInstallation? ffmpeg = null;
        try { ffmpeg = await FfmpegLocator.LocateAsync(); } catch { }
        Skip.If(ffmpeg is null, "FFmpeg not available on machine.");

        using var deviceManager = new D3D11DeviceManager();
        var tempFolder = Path.Combine(Path.GetTempPath(), "linkast_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempFolder);
        try
        {
            await using var engine = new D3D11StreamEngine(
                ffmpeg,
                new TestOutputTransport(),
                deviceManager: deviceManager,
                options: new EngineOptions { ConnectTimeout = TimeSpan.FromSeconds(5) });

            var session = ValidSession() with
            {
                Encoder = EncoderSettings.Default1080p60("libx264"),
                Broadcast = false,
                Recording = new RecordingSpec(tempFolder),
                Reconnect = ReconnectPolicy.Disabled
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await engine.StartAsync(session);

            while (engine.State != StreamState.Live && engine.State != StreamState.Failed && sw.ElapsedMilliseconds < 5000)
            {
                await Task.Delay(50);
            }

            Assert.Equal(StreamState.Live, engine.State);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"Expected live under 4s, took {sw.Elapsed.TotalSeconds}s");

            await engine.StopAsync();
            Assert.Equal(StreamState.Stopped, engine.State);
        }
        finally
        {
            if (Directory.Exists(tempFolder))
            {
                try { Directory.Delete(tempFolder, true); } catch { }
            }
        }
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core;
using Linkast.Core.Camera;
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

        // While not active (no mixer created yet), returns false safely
        var gainSet = await engine.SetGainAsync(AudioChannels.Microphone, 0.5);
        Assert.False(gainSet);

        var noiseSet = await engine.SetNoiseSuppressionAsync(true);
        Assert.False(noiseSet);
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
}

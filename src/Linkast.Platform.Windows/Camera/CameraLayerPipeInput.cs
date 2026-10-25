// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO.Pipes;
using Linkast.Core.Camera;

namespace Linkast.Platform.Windows.Camera;

/// <summary>
/// Serves a rendered camera layer to FFmpeg as raw BGRA video over a current-user-only named pipe.
/// Frames are paced to the output frame rate (the last image repeats when the camera is slower),
/// and the layer is transparent while the camera is off, unplugged or failing, so camera problems
/// never interrupt the stream. Device switches and camera-off apply live.
/// </summary>
public sealed class CameraLayerPipeInput : IVideoLayerInput
{
    private readonly LiveCameraLayer _layer;
    private readonly ICameraService _cameras;
    private readonly string _pipeName = $"linkast-cam-{Guid.NewGuid():N}";
    private readonly CameraLayerRenderer _renderer = new();
    private readonly byte[] _frame;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _feedGate = new();
    private NamedPipeServerStream? _pipe;
    private ICameraFeed? _feed;
    private CameraFormatRequest? _feedFormat;
    private Task? _writer;
    private long _lastSequence;
    private bool _transparent = true;

    private readonly LayerEffectsBinding _effects;

    public CameraLayerPipeInput(LiveCameraLayer layer, ICameraService cameras, int width, int height, int frameRate,
        ISegmentationService? segmentation = null, BackgroundImageCache? images = null,
        IFramingService? framing = null, Linkast.Core.Audio.AudioLevelBus? audio = null)
    {
        _effects = new LayerEffectsBinding(segmentation, images, framing, audio);
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(cameras);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameRate);
        _layer = layer;
        _cameras = cameras;
        Width = width;
        Height = height;
        FrameRate = frameRate;
        _frame = new byte[width * height * 4];
    }

    public string LayerId => _layer.LayerId;

    public string PipePath => @"\\.\pipe\" + _pipeName;

    public int Width { get; }

    public int Height { get; }

    public int FrameRate { get; }

    public Task Completion => _completion.Task;

    /// <summary>Frames written to FFmpeg. Diagnostics and tests.</summary>
    public long FramesWritten { get; private set; }

    /// <summary>Frames that contained camera content (not transparent). Diagnostics and tests.</summary>
    public long CameraFramesRendered { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _pipe = new NamedPipeServerStream(
            _pipeName,
            PipeDirection.Out,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            inBufferSize: 0,
            outBufferSize: _frame.Length);

        _layer.SourceChanged += OnSourceChanged;
        SyncFeed();
        _writer = Task.Run(() => WriterLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _layer.SourceChanged -= OnSourceChanged;
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_writer is not null)
        {
            try
            {
                await _writer.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The pipe disposal below unblocks a stuck write.
            }
        }

        if (_pipe is not null)
        {
            await _pipe.DisposeAsync().ConfigureAwait(false);
        }

        ICameraFeed? feed;
        lock (_feedGate)
        {
            feed = _feed;
            _feed = null;
        }

        if (feed is not null)
        {
            await feed.DisposeAsync().ConfigureAwait(false);
        }

        await _effects.DisposeAsync().ConfigureAwait(false);
        _completion.TrySetResult();
        _cts.Dispose();
    }

    private void OnSourceChanged(object? sender, EventArgs e) => SyncFeed();

    /// <summary>Opens, switches or releases the camera to match the layer's device and on/off state.</summary>
    private void SyncFeed()
    {
        ICameraFeed? old = null;
        lock (_feedGate)
        {
            var wanted = _layer.Enabled ? _layer.Device : null;
            var format = _layer.Format;
            if (_feed?.Device.Id == wanted?.Id && (wanted is null || _feedFormat == format))
            {
                return;
            }

            old = _feed;
            _feed = wanted is null ? null : _cameras.Open(wanted, format);
            _feedFormat = format;
            _lastSequence = 0;
            _renderer.Reset();
        }

        if (old is not null)
        {
            _ = old.DisposeAsync().AsTask();
        }
    }

    private async Task WriterLoopAsync(CancellationToken ct)
    {
        var pipe = _pipe!;
        try
        {
            await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            var start = Stopwatch.GetTimestamp();
            long sent = 0;
            var interval = TimeSpan.FromSeconds(1.0 / FrameRate);

            while (!ct.IsCancellationRequested)
            {
                var due = (long)(Stopwatch.GetElapsedTime(start).TotalSeconds * FrameRate) + 1;
                if (due - sent > FrameRate)
                {
                    sent = due - 1; // fell more than a second behind (system stall): skip rather than burst
                }

                if (sent >= due)
                {
                    await Task.Delay(interval / 2, ct).ConfigureAwait(false);
                    continue;
                }

                ProduceFrame();
                await pipe.WriteAsync(_frame, ct).ConfigureAwait(false);
                sent++;
                FramesWritten++;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (IOException)
        {
            // FFmpeg closed the pipe; the engine reports why.
        }
        catch (ObjectDisposedException)
        {
            // Disposed during shutdown.
        }

        _completion.TrySetResult();
    }

    private void ProduceFrame()
    {
        ICameraFeed? feed;
        lock (_feedGate)
        {
            feed = _feed;
        }

        if (feed is null || feed.State != CameraState.Running)
        {
            if (!_transparent)
            {
                CameraLayerRenderer.RenderTransparent(_frame, Width, Height);
                _transparent = true;
            }

            return;
        }

        var fx = _effects.Update(_layer);
        var source = feed.Frames.TryBeginRead(_lastSequence);
        if (source is null)
        {
            return; // no new camera frame: repeat the previous image
        }

        try
        {
            _renderer.Render(source, _layer.Settings, _frame, Width, Height, fx.Mask, fx.BackgroundImage, fx.Context, fx.Framing);
            _lastSequence = source.Sequence;
            _transparent = false;
            CameraFramesRendered++;
        }
        finally
        {
            feed.Frames.EndRead(source);
        }
    }
}

/// <summary>Creates camera layer pipes backed by the shared camera service.</summary>
public sealed class CameraLayerPipeInputProvider(
    ICameraService cameras,
    ISegmentationService? segmentation = null,
    BackgroundImageCache? images = null,
    IFramingService? framing = null,
    Linkast.Core.Audio.AudioLevelBus? audio = null)
    : IVideoLayerInputProvider
{
    public IVideoLayerInput Open(LiveCameraLayer layer, int width, int height, int frameRate) =>
        new CameraLayerPipeInput(layer, cameras, width, height, frameRate, segmentation, images, framing, audio);
}

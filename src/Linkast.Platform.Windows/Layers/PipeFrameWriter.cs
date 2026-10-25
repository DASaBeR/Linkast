// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO.Pipes;
using Linkast.Core.Camera;

namespace Linkast.Platform.Windows.Layers;

/// <summary>
/// Serves a layer to FFmpeg as raw BGRA video (straight alpha) over a current-user-only named pipe,
/// paced to <see cref="FrameRate"/>. Subclasses only fill <see cref="Frame"/>: a camera, a picture or a
/// web page. If nothing new is drawn the previous image repeats, and a stall never causes a burst.
/// </summary>
public abstract class PipeFrameWriter : IVideoLayerInput
{
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NamedPipeServerStream? _pipe;
    private Task? _writer;

    protected PipeFrameWriter(string layerId, string kind, int width, int height, int frameRate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerId);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameRate);
        LayerId = layerId;
        Width = width;
        Height = height;
        FrameRate = frameRate;
        Frame = new byte[width * height * 4];
        _pipeName = $"linkast-{kind}-{Guid.NewGuid():N}";
    }

    public string LayerId { get; }

    public string PipePath => @"\\.\pipe\" + _pipeName;

    public int Width { get; }

    public int Height { get; }

    public int FrameRate { get; }

    public Task Completion => _completion.Task;

    /// <summary>Frames written to FFmpeg. Diagnostics and tests.</summary>
    public long FramesWritten { get; private set; }

    /// <summary>The image sent to FFmpeg: <see cref="Width"/> × <see cref="Height"/> BGRA, straight alpha.</summary>
    protected byte[] Frame { get; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _pipe = new NamedPipeServerStream(
            _pipeName,
            PipeDirection.Out,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            inBufferSize: 0,
            outBufferSize: Frame.Length);

        OnStarting();
        _writer = Task.Run(() => WriterLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
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

        await OnDisposeAsync().ConfigureAwait(false);
        _completion.TrySetResult();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Called once before frames are served (open the camera, load the picture…).</summary>
    protected virtual void OnStarting()
    {
    }

    /// <summary>Updates <see cref="Frame"/> for the next frame. Leaving it unchanged repeats the previous image.</summary>
    protected abstract void ProduceFrame();

    protected virtual ValueTask OnDisposeAsync() => ValueTask.CompletedTask;

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
                await pipe.WriteAsync(Frame, ct).ConfigureAwait(false);
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
}

/// <summary>
/// A layer whose image never changes: a picture overlay or the away screen. The caller renders the
/// image once (straight-alpha BGRA at the layer's size); it is served at a low frame rate.
/// </summary>
public sealed class StaticFramePipeInput : PipeFrameWriter
{
    /// <summary>
    /// Still images are served at 10 fps. FFmpeg's overlay waits for a layer's next frame, so a lower rate
    /// would delay the whole stream; higher would only cost CPU for an image that does not change.
    /// </summary>
    public const int StillFrameRate = 10;

    public StaticFramePipeInput(string layerId, ReadOnlySpan<byte> bgra, int width, int height)
        : base(layerId, "img", width, height, StillFrameRate)
    {
        if (bgra.Length != width * height * 4)
        {
            throw new ArgumentException("The image does not match the layer size.", nameof(bgra));
        }

        bgra.CopyTo(Frame);
    }

    protected override void ProduceFrame()
    {
    }
}

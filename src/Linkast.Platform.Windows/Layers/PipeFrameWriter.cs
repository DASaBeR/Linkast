// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO.Pipes;
using Linkast.Core.Camera;

namespace Linkast.Platform.Windows.Layers;

/// <summary>
/// Serves a layer to FFmpeg as raw BGRA video (straight alpha) over a current-user-only named pipe,
/// paced to <see cref="FrameRate"/>. Subclasses only fill <see cref="Frame"/>: a camera, a picture or a
/// web page. If nothing new is drawn the previous image repeats.
/// FFmpeg timestamps these frames by count, so frame n must be the picture at n / <see cref="FrameRate"/>
/// seconds after FFmpeg connected: after any stall (this process paused) the writer catches up with the
/// current picture instead of skipping. Skipping would put the layer behind the rest of the stream for good.
/// Pictures are taken on that clock into a short queue and sent whenever FFmpeg reads. Sending only on
/// the clock made FFmpeg wait for the pipe; while it waits it pauses its other inputs (the game capture
/// lost frames: 16 of 67 fps reached the stream), and the burst read afterwards got one picture several
/// times (camera at 10 of 30 fps; tester, 1.0.3).
/// </summary>
public abstract partial class PipeFrameWriter : IVideoLayerInput
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

    /// <summary>Frames that repeated the last picture so FFmpeg could catch up. Diagnostics and tests.</summary>
    public long FramesRepeated { get; private set; }

    /// <summary>Pictures dropped because FFmpeg read nothing for longer than the queue holds. Diagnostics and tests.</summary>
    public long PicturesDropped { get; private set; }

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

    /// <summary>
    /// How many pictures may wait for FFmpeg: 2 s of them, at most 96 MB, at least 2. FFmpeg reads one
    /// input at a time (its scheduler pauses the others), so a layer is read in bursts, and it reads the
    /// whole stream about a second behind when the pipe connected (it opens its other inputs and the
    /// output after). The queue keeps the pictures of that time, so the layer is exactly as late as the
    /// game and the sound. 200 ms overflowed all the time: a third of the pictures were dropped and
    /// repeats stood in for them (camera at 21 of 30 fps).
    /// </summary>
    internal static int QueueCapacity(int frameBytes, int frameRate) =>
        Math.Clamp((96 << 20) / Math.Max(1, frameBytes), 2, Math.Max(2, 2 * frameRate));

    /// <summary>
    /// How many frames behind real time FFmpeg must be before the last picture repeats. Reading frame n
    /// the moment it is due, FFmpeg often came before the producer had queued it, and repeating then
    /// instead of waiting a millisecond cost a third of the camera's pictures.
    /// </summary>
    private const int CatchUpMargin = 2;

    private async Task WriterLoopAsync(CancellationToken ct)
    {
        var pipe = _pipe!;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var spare = new System.Collections.Concurrent.ConcurrentBag<byte[]>();
        var queue = System.Threading.Channels.Channel.CreateBounded<byte[]>(
            new System.Threading.Channels.BoundedChannelOptions(QueueCapacity(Frame.Length, FrameRate))
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            },
            old =>
            {
                spare.Add(old);
                PicturesDropped++;
            });
        Thread? producer = null;
        try
        {
            await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            var start = Stopwatch.GetTimestamp();

            // Its own thread above normal priority: on the thread pool, under a game's CPU load, the clock
            // ran late and a 30 fps camera reached the stream at 23.
            producer = new Thread(() => ProduceLoop(start, queue.Writer, spare, stop.Token))
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
                Name = "Linkast layer " + LayerId,
            };
            producer.Start();

            // Frame n is due at n / rate seconds after FFmpeg connected. FFmpeg takes the oldest waiting
            // picture whenever it reads, so it never waits for us (nor do the inputs it holds paused
            // meanwhile, the game capture too). When it reads faster than real time, catching up after
            // a wait (it waits seconds for the server while opening the output), and no picture is
            // waiting, the last one repeats: holding it to real time kept the stream that wait behind
            // the sound for good (sound ~10 s late, FFmpeg's memory growing with it). It never gets
            // frames ahead of real time, and repeats never come in bulk: replacing dropped pictures
            // with repeats sent each one dozens of times while the stream was behind (1 fps camera).
            byte[]? last = null;
            long sent = 0;
            while (!ct.IsCancellationRequested)
            {
                if (queue.Reader.TryRead(out var next))
                {
                    if (last is not null)
                    {
                        spare.Add(last);
                    }

                    last = next;
                }
                else if (last is null || sent + CatchUpMargin >= (long)(Stopwatch.GetElapsedTime(start).TotalSeconds * FrameRate) + 1)
                {
                    if (!await queue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                    {
                        break;
                    }

                    continue;
                }
                else
                {
                    FramesRepeated++;
                }

                await pipe.WriteAsync(last, ct).ConfigureAwait(false);
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
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            producer?.Join(TimeSpan.FromSeconds(2));
        }

        _completion.TrySetResult();
    }

    /// <summary>
    /// One picture per frame interval of real time from when FFmpeg connected (catching up with repeats
    /// after this process paused), so the queue holds the pictures of the last moments.
    /// </summary>
    private void ProduceLoop(long start, System.Threading.Channels.ChannelWriter<byte[]> queue, System.Collections.Concurrent.ConcurrentBag<byte[]> spare, CancellationToken ct)
    {
        long produced = 0;

        // Windows' default 15.6 ms timer tick made the 33 ms clock take two pictures in one tick and miss
        // the next (a camera at 25 of 30 fps); 1 ms while any layer runs, as media players do.
        if (Interlocked.Increment(ref s_fineTimerUsers) == 1)
        {
            _ = TimeBeginPeriod(1);
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var due = (long)(Stopwatch.GetElapsedTime(start).TotalSeconds * FrameRate) + 1;
                if (produced >= due)
                {
                    Thread.Sleep(1);
                    continue;
                }

                ProduceFrame();
                if (!spare.TryTake(out var copy))
                {
                    copy = new byte[Frame.Length];
                }

                Frame.CopyTo(copy, 0);
                queue.TryWrite(copy);
                produced++;
            }
        }
        finally
        {
            queue.TryComplete();
            if (Interlocked.Decrement(ref s_fineTimerUsers) == 0)
            {
                _ = TimeEndPeriod(1);
            }
        }
    }

    private static int s_fineTimerUsers;

    [System.Runtime.InteropServices.LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint milliseconds);

    [System.Runtime.InteropServices.LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriod(uint milliseconds);
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

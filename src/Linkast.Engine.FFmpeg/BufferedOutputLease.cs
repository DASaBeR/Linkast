// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Linkast.Core;
using Microsoft.Extensions.Logging;

namespace Linkast.Engine.FFmpeg;

/// <summary>
/// Puts a memory buffer between the encoding FFmpeg and the server. The encoder writes plain FLV to a
/// local socket that takes it at once; a second FFmpeg copies it (no re-encoding) from the buffer to the
/// server through the real output (the relay). The encoder never waits for the network: when it did, the
/// seconds a server takes to answer and every slow moment of the upload paused all of FFmpeg's inputs,
/// and Windows drops a captured game's frames while its capture is paused (CS2 at 16 of 67 fps through
/// Restream, 52 with the same server answering at once; a 4 s answer alone brought it down to 17).
/// </summary>
internal sealed partial class BufferedOutputLease : IOutputLease
{
    /// <summary>
    /// More than this waiting (about 80 s at 6000 kbps) means the upload can't carry the stream; the encoder
    /// then waits for the network again rather than filling the memory.
    /// </summary>
    internal const long MaxBufferedBytes = 64L << 20;

    private const int ChunkSize = 64 * 1024;

    private readonly IOutputLease _inner;
    private readonly ILogger _logger;
    private readonly TcpListener _encoderListener = new(IPAddress.Loopback, 0);
    private readonly TcpListener _senderListener = new(IPAddress.Loopback, 0);
    private readonly Channel<ArraySegment<byte>> _queue = Channel.CreateUnbounded<ArraySegment<byte>>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly SemaphoreSlim _room = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FfmpegProcess? _sender;
    private Action<string> _senderLine = _ => { };
    private Task? _run;
    private long _buffered;
    /// <summary>First warning at 8 MB (8–11 s of a 6000–8000 kbps stream; a server's slow answer is a few s), then each doubling.</summary>
    private long _warnAt = 8 << 20;

    private BufferedOutputLease(IOutputLease inner, ILogger logger)
    {
        _inner = inner;
        _logger = logger;
    }

    /// <summary>Where the encoding FFmpeg writes its FLV (no RTMP: nothing to answer, so no waiting).</summary>
    public string PublishUrl { get; private set; } = string.Empty;

    /// <summary>None: the encoder's output is a plain socket. The sender announces the server's tcUrl.</summary>
    public string TcUrl => string.Empty;

    public bool IsMeasured => _inner.IsMeasured;

    public Task Completion => _completion.Task;

    /// <summary>Bytes the encoder has written that the server has not been sent yet.</summary>
    public long BufferedBytes => Interlocked.Read(ref _buffered);

    public NetworkSnapshot Snapshot() => _inner.Snapshot();

    /// <summary>Opens the buffer in front of <paramref name="inner"/> and starts the sending FFmpeg.</summary>
    public static BufferedOutputLease Start(FfmpegInstallation ffmpeg, IOutputLease inner, ILogger logger, Action<string> senderLine)
    {
        var lease = new BufferedOutputLease(inner, logger);
        lease._encoderListener.Start(backlog: 1);
        lease._senderListener.Start(backlog: 1);
        var encoderPort = ((IPEndPoint)lease._encoderListener.LocalEndpoint).Port;
        var senderPort = ((IPEndPoint)lease._senderListener.LocalEndpoint).Port;
        lease.PublishUrl = string.Create(CultureInfo.InvariantCulture, $"tcp://127.0.0.1:{encoderPort}");
        lease._senderLine = senderLine;
        lease._sender = FfmpegProcess.Start(ffmpeg.Path, SenderArguments(senderPort, inner.PublishUrl, inner.TcUrl), _ => { }, senderLine);
        lease._run = Task.Run(lease.RunAsync);
        return lease;
    }

    /// <summary>
    /// The sending FFmpeg: reads the FLV back from the buffer and publishes it unchanged. It looks at
    /// half a second of it to find the two tracks (the default 5 s only adds delay).
    /// </summary>
    internal static IReadOnlyList<string> SenderArguments(int bufferPort, string publishUrl, string tcUrl)
    {
        List<string> args =
        [
            "-hide_banner", "-nostats", "-loglevel", "level+warning",
            "-analyzeduration", "500000", "-probesize", "1000000",
            "-f", "flv", "-i", string.Create(CultureInfo.InvariantCulture, $"tcp://127.0.0.1:{bufferPort}"),
            "-map", "0", "-c", "copy",
            "-f", "flv", "-flvflags", "no_duration_filesize",
        ];
        if (!string.IsNullOrEmpty(tcUrl))
        {
            args.AddRange(["-rtmp_tcurl", tcUrl]);
        }

        args.Add(publishUrl);
        return args;
    }

    private async Task RunAsync()
    {
        var ct = _cts.Token;
        TcpClient? encoder = null;
        TcpClient? sender = null;
        try
        {
            var acceptEncoder = _encoderListener.AcceptTcpClientAsync(ct).AsTask();
            var acceptSender = _senderListener.AcceptTcpClientAsync(ct).AsTask();
            encoder = await acceptEncoder.ConfigureAwait(false);
            _encoderListener.Stop();
            var fill = FillAsync(encoder.GetStream(), ct);

            sender = await acceptSender.WaitAsync(ct).ConfigureAwait(false);
            _senderListener.Stop();
            var drain = DrainAsync(sender.GetStream(), ct);

            // The server side ending (relay failure, sender gone) ends the attempt; the encoder finishing
            // (stopped) lets the buffer empty to the server first.
            var senderExit = _sender!.Exited;
            var first = await Task.WhenAny(drain, senderExit, _inner.Completion).ConfigureAwait(false);
            if (first == drain)
            {
                await drain.ConfigureAwait(false);
                sender.Client.Shutdown(SocketShutdown.Send); // end of the FLV: the sender finishes and exits
                await Task.WhenAny(senderExit, Task.Delay(TimeSpan.FromSeconds(5), ct)).ConfigureAwait(false);
            }

            await fill.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
            Finish();
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException or TimeoutException)
        {
            Finish();
        }
        finally
        {
            encoder?.Dispose(); // the encoder's output closes, so it stops too
            sender?.Dispose();
        }
    }

    /// <summary>Completes as the server side did: its classified error, else the sender's exit.</summary>
    private void Finish()
    {
        if (_inner.Completion is { IsFaulted: true } inner && inner.Exception?.GetBaseException() is StreamFailureException sfe)
        {
            _completion.TrySetException(sfe);
        }
        else if (_sender?.Exited is { IsCompleted: true } exit && exit.Result != 0 && !_cts.IsCancellationRequested)
        {
            _completion.TrySetException(new StreamFailureException(new StreamError(ErrorCategory.Network, "network.sender",
                "The connection to the server ended.", string.Create(CultureInfo.InvariantCulture, $"sending FFmpeg exited with code {exit.Result}"))));
        }
        else
        {
            _completion.TrySetResult();
        }
    }

    /// <summary>Takes the encoder's output as fast as it comes; it only waits when the buffer is full.</summary>
    private async Task FillAsync(NetworkStream from, CancellationToken ct)
    {
        var rate = Stopwatch.StartNew();
        long received = 0;
        try
        {
            while (true)
            {
                var chunk = new byte[ChunkSize];
                var read = await from.ReadAsync(chunk, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                received += read;
                var buffered = Interlocked.Add(ref _buffered, read);
                _queue.Writer.TryWrite(new ArraySegment<byte>(chunk, 0, read));
                if (buffered >= _warnAt)
                {
                    var kbps = received * 8 / 1000.0 / Math.Max(1, rate.Elapsed.TotalSeconds);
                    var seconds = buffered * 8 / 1000.0 / Math.Max(1, kbps);
                    LogBacklog(buffered / 1e6, seconds);
                    _senderLine(string.Create(CultureInfo.InvariantCulture,
                        $"[warning] The upload is not keeping up: about {seconds:0} s of the stream is waiting to be sent, so viewers fall behind. Lower the bitrate."));
                    _warnAt *= 2;
                }

                while (Interlocked.Read(ref _buffered) > MaxBufferedBytes)
                {
                    await _room.WaitAsync(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _queue.Writer.TryComplete();
        }
    }

    private async Task DrainAsync(NetworkStream to, CancellationToken ct)
    {
        await foreach (var chunk in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            await to.WriteAsync(chunk, ct).ConfigureAwait(false);
            if (Interlocked.Add(ref _buffered, -chunk.Count) <= MaxBufferedBytes && _room.CurrentCount == 0)
            {
                _room.Release();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Let a stop's last seconds reach the server: the encoder has finished by now.
        if (_run is not null)
        {
            await Task.WhenAny(_run, Task.Delay(TimeSpan.FromSeconds(6))).ConfigureAwait(false);
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        _encoderListener.Stop();
        _senderListener.Stop();
        if (_sender is not null)
        {
            await _sender.DisposeAsync().ConfigureAwait(false);
        }

        if (_run is not null)
        {
            await Task.WhenAny(_run, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        }

        await _inner.DisposeAsync().ConfigureAwait(false);
        _completion.TrySetResult();
        _cts.Dispose();
        _room.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The upload is not keeping up: {Megabytes:0.0} MB (about {Seconds:0} s of the stream) waiting to be sent. Lower the bitrate.")]
    private partial void LogBacklog(double megabytes, double seconds);
}

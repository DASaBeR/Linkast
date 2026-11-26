// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
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
/// <para>
/// When the upload can't carry the stream, the oldest seconds waiting are dropped (whole groups of pictures,
/// sound with them, see <see cref="FlvBacklog"/>) rather than making the encoder wait: a waiting encoder
/// stops reading its pipes, and sound and picture then lose different amounts and stay apart.
/// </para>
/// </summary>
internal sealed partial class BufferedOutputLease : IOutputLease
{
    /// <summary>More than this much stream time waiting means the upload can't carry it: the oldest is dropped.</summary>
    internal static readonly TimeSpan MaxBacklog = TimeSpan.FromSeconds(8);

    /// <summary>What is left waiting after a drop, so a drop is not followed at once by the next.</summary>
    internal static readonly TimeSpan BacklogAfterDrop = TimeSpan.FromSeconds(4);

    /// <summary>A memory bound as well (about 20 s at 6000 kbps), whatever the timestamps say.</summary>
    internal const long MaxBufferedBytes = 16L << 20;

    private const int ChunkSize = 64 * 1024;

    private readonly IOutputLease _inner;
    private readonly ILogger _logger;
    private readonly TcpListener _encoderListener = new(IPAddress.Loopback, 0);
    private readonly TcpListener _senderListener = new(IPAddress.Loopback, 0);
    private readonly FlvBacklog _backlog = new();
    private readonly Lock _backlogGate = new();
    private readonly SemaphoreSlim _available = new(0);
    private bool _inputDone;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FfmpegProcess? _sender;
    private Action<string> _senderLine = _ => { };
    private Task? _run;
    private long _buffered;
    private bool _encoderConnected;
    /// <summary>First warning at 8 MB (8–11 s of a 6000–8000 kbps stream; a server's slow answer is a few s), then each doubling.</summary>
    private long _warnAt = 4 << 20;

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

    /// <summary>Stream time dropped because the upload could not carry it.</summary>
    public TimeSpan DroppedDuration
    {
        get
        {
            lock (_backlogGate)
            {
                return _backlog.DroppedDuration;
            }
        }
    }

    public NetworkSnapshot Snapshot() => _inner.Snapshot() with { BufferedBytes = BufferedBytes, DroppedDuration = DroppedDuration };

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
        var began = Stopwatch.GetTimestamp();
        TcpClient? encoder = null;
        TcpClient? sender = null;
        try
        {
            var acceptEncoder = _encoderListener.AcceptTcpClientAsync(ct).AsTask();
            var acceptSender = _senderListener.AcceptTcpClientAsync(ct).AsTask();
            encoder = await acceptEncoder.ConfigureAwait(false);
            Volatile.Write(ref _encoderConnected, true);
            _encoderListener.Stop();
            LogEncoderConnected(Stopwatch.GetElapsedTime(began).TotalSeconds);
            var fill = FillAsync(encoder.GetStream(), ct);

            sender = await acceptSender.WaitAsync(ct).ConfigureAwait(false);
            _senderListener.Stop();
            LogSenderConnected(Stopwatch.GetElapsedTime(began).TotalSeconds, BufferedBytes);
            var drain = DrainAsync(sender.GetStream(), ct);

            // The server side ending (relay failure, sender gone) ends the attempt; the encoder finishing
            // (stopped) lets the buffer empty to the server first.
            var senderExit = _sender!.Exited;
            var first = await Task.WhenAny(drain, senderExit, _inner.Completion).ConfigureAwait(false);
            LogEnded(first == drain ? "the encoder finished" : first == senderExit ? "the sending FFmpeg exited" : "the server connection ended",
                senderExit.IsCompleted ? senderExit.Result : null, BufferedBytes, Stopwatch.GetElapsedTime(began).TotalSeconds);
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
            if (_cts.IsCancellationRequested)
            {
                _sender?.Kill();
            }
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

    /// <summary>Takes the encoder's output as fast as it comes and never makes it wait.</summary>
    private async Task FillAsync(NetworkStream from, CancellationToken ct)
    {
        var rate = Stopwatch.StartNew();
        long received = 0;
        var chunk = new byte[ChunkSize];
        try
        {
            while (true)
            {
                var read = await from.ReadAsync(chunk, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                received += read;
                long buffered;
                TimeSpan dropped;
                TimeSpan waiting;
                TimeSpan droppedTotal;
                lock (_backlogGate)
                {
                    _backlog.Append(chunk.AsSpan(0, read));
                    dropped = _backlog.DropOldest(MaxBacklog, MaxBufferedBytes, BacklogAfterDrop);
                    buffered = _backlog.Bytes;
                    waiting = _backlog.Duration;
                    droppedTotal = _backlog.DroppedDuration;
                }

                Interlocked.Exchange(ref _buffered, buffered);
                Signal();
                _upload.Sample(rate.Elapsed, _inner.Snapshot().BytesSent);
                if (dropped > TimeSpan.Zero)
                {
                    var streamKbps = received * 8 / 1000.0 / Math.Max(1, rate.Elapsed.TotalSeconds);
                    LogDropped(dropped.TotalSeconds, waiting.TotalSeconds, droppedTotal.TotalSeconds);
                    _senderLine(DropWarning(dropped.TotalSeconds, streamKbps, _upload.Kbps));
                }

                if (buffered >= _warnAt)
                {
                    var kbps = received * 8 / 1000.0 / Math.Max(1, rate.Elapsed.TotalSeconds);
                    var seconds = buffered * 8 / 1000.0 / Math.Max(1, kbps);
                    LogBacklog(buffered / 1e6, seconds);
                    _senderLine(BacklogWarning(seconds, kbps, _upload.Kbps));
                    _warnAt *= 2;
                }
            }
        }
        finally
        {
            lock (_backlogGate)
            {
                _inputDone = true;
            }

            Signal();
        }
    }

    private void Signal()
    {
        if (_available.CurrentCount == 0)
        {
            _available.Release();
        }
    }

    /// <summary>Said when part of the stream was skipped: how much, and a bitrate the upload can carry.</summary>
    internal static string DropWarning(double secondsDropped, double streamKbps, double? uploadKbps)
    {
        var inv = CultureInfo.InvariantCulture;
        var head = string.Create(inv, $"[warning] The upload is not keeping up: {secondsDropped:0.0} s of the stream was skipped so viewers stay close to live (sound and picture stay together).");
        if (uploadKbps is not { } up || up <= 0)
        {
            return head + " Lower the bitrate.";
        }

        var suggested = Math.Max(1000, Math.Floor(((up * 0.85) - 200) / 500) * 500);
        return head + string.Create(inv, $" The connection sends about {up / 1000:0.0} Mbps; the stream needs {streamKbps / 1000:0.0}. Set the bitrate to {suggested:0} kbps or lower.");
    }

    /// <summary>
    /// What to tell the streamer: how far behind viewers are, what the connection really sends and what the
    /// stream needs, and a bitrate that fits with 15 % to spare (audio and the stream's own overhead
    /// included), rounded down to 500 kbps. Without a measured upload only the delay is known.
    /// </summary>
    internal static string BacklogWarning(double secondsBehind, double streamKbps, double? uploadKbps)
    {
        var inv = CultureInfo.InvariantCulture;
        if (uploadKbps is not { } up || up <= 0)
        {
            return string.Create(inv, $"[warning] The upload is not keeping up: about {secondsBehind:0} s of the stream is waiting to be sent, so viewers fall behind. Lower the bitrate.");
        }

        var suggested = Math.Max(1000, Math.Floor(((up * 0.85) - 200) / 500) * 500);
        return string.Create(inv,
            $"[warning] The upload is not keeping up: about {secondsBehind:0} s of the stream is waiting to be sent, so viewers fall behind. The connection sends about {up / 1000:0.0} Mbps; the stream needs {streamKbps / 1000:0.0}. Set the bitrate to {suggested:0} kbps or lower.");
    }

    /// <summary>The upload over the last 20 s, from the bytes the server was sent (one sample a second).</summary>
    private sealed class UploadRate
    {
        private static readonly TimeSpan Window = TimeSpan.FromSeconds(20);
        private readonly Queue<(TimeSpan At, long Bytes)> _samples = new();

        public void Sample(TimeSpan at, long bytesSent)
        {
            if (_samples.Count > 0 && at - _samples.Last().At < TimeSpan.FromSeconds(1))
            {
                return;
            }

            _samples.Enqueue((at, bytesSent));
            while (_samples.Count > 2 && at - _samples.Peek().At > Window)
            {
                _samples.Dequeue();
            }
        }

        /// <summary>Kilobits a second, or null before 5 s of samples.</summary>
        public double? Kbps
        {
            get
            {
                if (_samples.Count < 2)
                {
                    return null;
                }

                var (firstAt, firstBytes) = _samples.Peek();
                var (lastAt, lastBytes) = _samples.Last();
                var span = (lastAt - firstAt).TotalSeconds;
                return span < 5 ? null : (lastBytes - firstBytes) * 8 / 1000.0 / span;
            }
        }
    }

    private readonly UploadRate _upload = new();

    /// <summary>Sends whole FLV tags to the sending FFmpeg, oldest first.</summary>
    private async Task DrainAsync(NetworkStream to, CancellationToken ct)
    {
        while (true)
        {
            byte[]? unit;
            bool done;
            lock (_backlogGate)
            {
                unit = _backlog.TryTake();
                done = _inputDone;
                Interlocked.Exchange(ref _buffered, _backlog.Bytes);
            }

            if (unit is null)
            {
                if (done)
                {
                    return;
                }

                await _available.WaitAsync(ct).ConfigureAwait(false);
                continue;
            }

            await to.WriteAsync(unit, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Let a stop's last seconds reach the server: the encoder has finished by now.
        // An attempt whose encoder never connected has nothing to send, so there is nothing to wait for.
        if (_run is not null && Volatile.Read(ref _encoderConnected))
        {
            await Task.WhenAny(_run, Task.Delay(TimeSpan.FromSeconds(6))).ConfigureAwait(false);
        }
        else
        {
            _sender?.Kill();
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
        _available.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Buffer: the encoder connected after {Seconds:0.00} s.")]
    private partial void LogEncoderConnected(double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Buffer: the sending FFmpeg connected after {Seconds:0.00} s ({Buffered} bytes were waiting).")]
    private partial void LogSenderConnected(double seconds, long buffered);

    [LoggerMessage(Level = LogLevel.Information, Message = "Buffer: this connection ended because {Reason} (sender exit code {SenderExit}, {Buffered} bytes unsent, after {Seconds:0.0} s).")]
    private partial void LogEnded(string reason, int? senderExit, long buffered, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The upload is not keeping up: dropped the oldest {Seconds:0.0} s of the stream (whole groups of pictures with their sound) so the encoder never waits; {Left:0.0} s still waiting, {Total:0.0} s dropped on this connection.")]
    private partial void LogDropped(double seconds, double left, double total);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The upload is not keeping up: {Megabytes:0.0} MB (about {Seconds:0} s of the stream) waiting to be sent. Lower the bitrate.")]
    private partial void LogBacklog(double megabytes, double seconds);
}

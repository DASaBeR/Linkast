// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Linkast.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Linkast.Networking;

/// <summary>
/// Output transport that places a local TCP relay between FFmpeg and the ingest server.
/// FFmpeg publishes plain RTMP to 127.0.0.1; the relay opens the real outbound socket itself,
/// through the network adapter the user selected (or the system default route). This gives Linkast
/// control of the socket (adapter binding, TLS for RTMPS), real byte counters, verification of the
/// interface actually used, and precise network error classification that FFmpeg cannot provide.
/// </summary>
public sealed partial class RelayOutputTransport(ILogger<RelayOutputTransport>? logger = null) : IOutputTransport
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public Task<IOutputLease> OpenAsync(Destination destination, NetworkSelection network, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(network);

        // Fail fast (and let the reconnect policy retry) when the chosen adapter is gone or has no route.
        if (!network.IsSystemDefault)
        {
            OutboundConnector.ResolveAdapter(network);
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(backlog: 1);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // FFmpeg only ever sees a random placeholder of the key's length; the relay puts the real key
        // into the RTMP publish commands. The key is never on FFmpeg's command line (visible in Task
        // Manager, which is easy to show on stream by accident) or in FFmpeg's own messages.
        var server = destination.ServerUrl;
        var appPath = server.AbsolutePath.TrimEnd('/');
        var key = destination.StreamKey.Reveal();
        var placeholder = RtmpKeyRewriter.CreatePlaceholder(key);
        var publishUrl = $"rtmp://127.0.0.1:{port}{appPath}/{placeholder}";
        var tcUrl = server.GetLeftPart(UriPartial.Path).TrimEnd('/');

        var lease = new RelayLease(listener, publishUrl, tcUrl, server, network, ConnectTimeout, _logger, new RtmpKeyRewriter(placeholder, key));
        lease.Start();
        LogOpened(port, server.Host, network.DisplayName ?? "system default route");
        return Task.FromResult<IOutputLease>(lease);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Relay listening on 127.0.0.1:{Port} for {Host} via {Network}")]
    private partial void LogOpened(int port, string host, string network);
}

internal sealed partial class RelayLease : IOutputLease
{
    private const int BufferSize = 64 * 1024;

    private readonly TcpListener _listener;
    private readonly Uri _server;
    private readonly NetworkSelection _network;
    private readonly TimeSpan _connectTimeout;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _rateGate = new();

    private Task? _run;
    private long _bytesSent;
    private long _bytesReceived;
    private volatile bool _connected;
    private string? _localEndpoint;
    private string? _remoteEndpoint;
    private string? _interfaceName;
    private long _rateBytes;
    private long _rateTimestamp = Stopwatch.GetTimestamp();
    private double _lastKbps;

    private readonly RtmpKeyRewriter _rewriter;

    public RelayLease(TcpListener listener, string publishUrl, string tcUrl, Uri server, NetworkSelection network, TimeSpan connectTimeout, ILogger logger, RtmpKeyRewriter rewriter)
    {
        _rewriter = rewriter;
        _listener = listener;
        PublishUrl = publishUrl;
        TcUrl = tcUrl;
        _server = server;
        _network = network;
        _connectTimeout = connectTimeout;
        _logger = logger;
    }

    public string PublishUrl { get; }

    public string TcUrl { get; }

    public bool IsMeasured => true;

    public Task Completion => _completion.Task;

    public void Start() => _run = Task.Run(RunAsync);

    public NetworkSnapshot Snapshot()
    {
        var sent = Interlocked.Read(ref _bytesSent);
        double kbps;
        lock (_rateGate)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(_rateTimestamp, now);
            if (elapsed >= TimeSpan.FromMilliseconds(500))
            {
                _lastKbps = (sent - _rateBytes) * 8 / 1000.0 / elapsed.TotalSeconds;
                _rateBytes = sent;
                _rateTimestamp = now;
            }

            kbps = _lastKbps;
        }

        return new NetworkSnapshot(sent, Interlocked.Read(ref _bytesReceived), kbps, _localEndpoint, _remoteEndpoint, _interfaceName, _connected);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        if (_run is not null)
        {
            try
            {
                await _run.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Sockets are closed; the task will observe it.
            }
        }

        _completion.TrySetResult();
        _cts.Dispose();
    }

    private async Task RunAsync()
    {
        var ct = _cts.Token;
        TcpClient? local = null;
        OutboundConnection? remote = null;
        try
        {
            local = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            _listener.Stop(); // exactly one publisher per lease

            if (local.Client.RemoteEndPoint is not IPEndPoint { } peer || !IPAddress.IsLoopback(peer.Address))
            {
                throw OutboundConnector.Fail("relay.peer", "Unexpected connection to the local relay.", null);
            }

            remote = await OutboundConnector.ConnectAsync(_server, _network, _connectTimeout, ct).ConfigureAwait(false);
            _localEndpoint = remote.Local.ToString();
            _remoteEndpoint = $"{_server.IdnHost} ({remote.RemoteAddress}:{remote.Port})";
            _interfaceName = remote.InterfaceName;
            _connected = true;
            LogConnected(_remoteEndpoint, _localEndpoint, _interfaceName ?? "unknown", _network.IsSystemDefault ? "default route" : "selected adapter (verified)", remote.ConnectTime.TotalMilliseconds, remote.UsedAdapterDns);

            using var localStream = local.GetStream();
            var up = PumpAsync(localStream, remote.Stream, isUpstream: true, ct);
            var down = PumpAsync(remote.Stream, localStream, isUpstream: false, ct);
            var first = await Task.WhenAny(up, down).ConfigureAwait(false);
            await first.ConfigureAwait(false); // propagate a classified failure, if any

            if (first == down)
            {
                // The server closed its side while FFmpeg was still publishing. Right after connecting
                // this is almost always a rejected key; later it is a server-side disconnect.
                throw Interlocked.Read(ref _bytesSent) < 256 * 1024
                    ? OutboundConnector.Fail("network.closed.early", "The server closed the connection right away. The stream key may be wrong or already in use.", null)
                    : OutboundConnector.Fail("network.closed", "The server closed the connection.", null);
            }

            _completion.TrySetResult();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _completion.TrySetResult();
        }
        catch (StreamFailureException ex)
        {
            LogFailure(ex.Error.Code, ex.Error.Detail);
            _completion.TrySetException(ex);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            if (ct.IsCancellationRequested)
            {
                _completion.TrySetResult();
            }
            else
            {
                var error = OutboundConnector.Fail("network.reset", LostMessage(), ex);
                LogFailure(error.Error.Code, error.Error.Detail);
                _completion.TrySetException(error);
            }
        }
        finally
        {
            _connected = false;
            if (remote is not null)
            {
                await remote.DisposeAsync().ConfigureAwait(false);
            }

            local?.Dispose();
        }
    }

    private string LostMessage() => _network.IsSystemDefault
        ? "The connection to the server was lost."
        : $"The connection to the server through '{_network.DisplayName}' was lost.";

    private async Task PumpAsync(Stream from, Stream to, bool isUpstream, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        while (true)
        {
            int read;
            try
            {
                read = await from.ReadAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException && !isUpstream && !ct.IsCancellationRequested)
            {
                throw OutboundConnector.Fail("network.reset", LostMessage(), ex);
            }

            if (read == 0)
            {
                return;
            }

            // Upstream bytes go through the key rewriter until FFmpeg has sent its publish command.
            var outgoing = isUpstream && !_rewriter.IsPassthrough
                ? _rewriter.Process(buffer.AsSpan(0, read))
                : buffer.AsMemory(0, read);
            if (isUpstream && _rewriter.IsPassthrough && _rewriter.Replacements == 0 && !_warnedNoKey)
            {
                _warnedNoKey = true;
                LogNoKeyReplaced();
            }

            try
            {
                await to.WriteAsync(outgoing, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException && isUpstream && !ct.IsCancellationRequested)
            {
                throw OutboundConnector.Fail("network.reset", LostMessage(), ex);
            }

            Interlocked.Add(ref isUpstream ? ref _bytesSent : ref _bytesReceived, outgoing.Length);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Relay connected to {Remote} from {Local} via {Interface} ({Mode}) in {Ms:0} ms; adapter DNS: {AdapterDns}")]
    private partial void LogConnected(string remote, string local, string @interface, string mode, double ms, bool adapterDns);

    private bool _warnedNoKey;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Relay could not place the stream key into FFmpeg's publish commands; the server will refuse the placeholder")]
    private partial void LogNoKeyReplaced();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Relay failed: {Code} {Detail}")]
    private partial void LogFailure(string code, string? detail);
}

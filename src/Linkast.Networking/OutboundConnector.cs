// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using Linkast.Core;

namespace Linkast.Networking;

/// <summary>An established, verified outbound connection to an ingest server.</summary>
public sealed class OutboundConnection : IAsyncDisposable
{
    internal OutboundConnection(Socket socket, Stream stream, IPEndPoint local, IPAddress remote, int port, NetworkAdapterInfo? adapter, string? interfaceName, TimeSpan connectTime, bool usedAdapterDns)
    {
        Socket = socket;
        Stream = stream;
        Local = local;
        RemoteAddress = remote;
        Port = port;
        Adapter = adapter;
        InterfaceName = interfaceName;
        ConnectTime = connectTime;
        UsedAdapterDns = usedAdapterDns;
    }

    public Socket Socket { get; }

    /// <summary>The socket stream, wrapped in TLS for rtmps.</summary>
    public Stream Stream { get; }

    public IPEndPoint Local { get; }

    public IPAddress RemoteAddress { get; }

    public int Port { get; }

    /// <summary>The adapter the user selected, or null for the system default route.</summary>
    public NetworkAdapterInfo? Adapter { get; }

    /// <summary>Name of the adapter that actually owns <see cref="Local"/>.</summary>
    public string? InterfaceName { get; }

    public TimeSpan ConnectTime { get; }

    /// <summary>True when the name was resolved through the adapter's own DNS servers.</summary>
    public bool UsedAdapterDns { get; }

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync().ConfigureAwait(false);
        Socket.Dispose();
    }
}

/// <summary>
/// Opens the outbound connection for a stream through the selected network adapter, or the
/// system default route. Never falls back silently: if the selected adapter can't be used, it
/// fails with a classified, user-presentable error. Shared by the relay and "Test connection".
/// </summary>
public static class OutboundConnector
{
    public static async Task<OutboundConnection> ConnectAsync(Uri server, NetworkSelection selection, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(selection);
        var host = server.IdnHost;
        var tls = server.Scheme == "rtmps";
        var port = server.IsDefaultPort || server.Port <= 0 ? (tls ? 443 : 1935) : server.Port;
        var adapter = selection.IsSystemDefault ? null : ResolveAdapter(selection);

        var (addresses, usedAdapterDns) = await ResolveAsync(host, adapter, timeout, ct).ConfigureAwait(false);

        // One overall deadline for the whole attempt; hosts with many addresses must not multiply it.
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(timeout * 1.5);
        SocketException? lastError = null;
        foreach (var address in addresses.Take(3))
        {
            if (overall.IsCancellationRequested)
            {
                break;
            }

            Socket socket;
            try
            {
                socket = adapter is null
                    ? new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                    : InterfaceBinding.CreateBoundSocket(adapter, address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            }
            catch (SocketException ex)
            {
                lastError = ex;
                continue;
            }

            var sw = Stopwatch.StartNew();
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
            attempt.CancelAfter(timeout);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), attempt.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                socket.Dispose();
                lastError = new SocketException((int)SocketError.TimedOut);
                continue;
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                lastError = ex;
                continue;
            }

            var connectTime = sw.Elapsed;
            var local = (IPEndPoint)socket.LocalEndPoint!;
            var actual = NetworkAdapterCatalog.FindByAddress(local.Address);

            // Selecting an adapter is not proof: check the socket really left through it.
            if (adapter is not null && !adapter.Owns(local.Address))
            {
                socket.Dispose();
                throw Fail("network.route.mismatch",
                    $"Windows sent the connection through '{actual?.Name ?? local.Address.ToString()}' instead of '{adapter.Name}'. A VPN or firewall may be forcing all traffic through one network.",
                    null);
            }

            Stream stream = new NetworkStream(socket, ownsSocket: false);
            if (tls)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                try
                {
                    using var tlsTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    tlsTimeout.CancelAfter(timeout);
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, tlsTimeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is System.Security.Authentication.AuthenticationException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
                {
                    await ssl.DisposeAsync().ConfigureAwait(false);
                    socket.Dispose();
                    throw Fail("network.tls", "A secure (RTMPS) connection could not be established with the server.", ex);
                }

                stream = ssl;
            }

            return new OutboundConnection(socket, stream, local, address, port, adapter, actual?.Name ?? adapter?.Name, connectTime, usedAdapterDns);
        }

        var via = adapter is null ? string.Empty : $" through '{adapter.Name}'";
        var noInternet = adapter?.Access is InternetAccess.LocalOnly or InternetAccess.None;
        if (noInternet && lastError?.SocketErrorCode is SocketError.TimedOut or SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown)
        {
            throw Fail("network.interface.no_internet",
                $"'{adapter!.Name}' could not reach the server, and Windows reports this network has no internet access (local network only). " +
                "Check that mobile data and tethering are switched on. A VPN can also block traffic on other networks.",
                lastError);
        }

        throw lastError?.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => Fail("network.refused", "The server refused the connection. Check the server URL and port.", lastError),
            SocketError.TimedOut => Fail("network.timeout", $"The connection to the server timed out{via}.", lastError),
            SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown or SocketError.AddressNotAvailable when adapter is not null =>
                Fail("network.interface.no_route", $"'{adapter.Name}' could not reach the server. It may have no internet access right now, or Windows does not allow this route.", lastError),
            SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown =>
                Fail("network.unreachable", "The server is unreachable from this network connection.", lastError),
            _ => Fail("network.connect", $"Could not connect to the server{via}.", lastError),
        };
    }

    /// <summary>Looks up the selected adapter fresh (addresses change) and checks it can be used now.</summary>
    public static NetworkAdapterInfo ResolveAdapter(NetworkSelection selection, IReadOnlyList<NetworkAdapterInfo>? adapters = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var name = selection.DisplayName ?? "the selected network";
        var adapter = (adapters ?? NetworkAdapterCatalog.List())
            .FirstOrDefault(a => string.Equals(a.Id, selection.InterfaceId, StringComparison.OrdinalIgnoreCase));
        if (adapter is null)
        {
            throw Fail("network.interface.missing", $"'{name}' is not connected. Plug it back in or choose another network; Linkast will keep retrying.", null);
        }

        if (!adapter.IsUp)
        {
            throw Fail("network.interface.down", $"'{adapter.Name}' is disconnected. Linkast will keep retrying until it is back.", null);
        }

        if (!adapter.IsUsable)
        {
            throw Fail("network.interface.no_internet",
                $"'{adapter.Name}' is connected but has no internet route (no address or gateway). Choose a network that has internet access.", null);
        }

        return adapter;
    }

    private static async Task<(IReadOnlyList<IPAddress> Addresses, bool AdapterDns)> ResolveAsync(string host, NetworkAdapterInfo? adapter, TimeSpan timeout, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            if (adapter is not null && !CanReach(adapter, literal.AddressFamily))
            {
                throw Fail("network.interface.family",
                    $"'{adapter.Name}' has no {(literal.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6")} connection, which this server address needs.", null);
            }

            return ([literal], false);
        }

        IPAddress[] system = [];
        SocketException? dnsError = null;
        try
        {
            system = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            dnsError = ex;
        }

        var usable = adapter is null ? system : [.. system.Where(a => CanReach(adapter, a.AddressFamily))];
        if (usable.Length > 0)
        {
            return (OrderAddresses(usable), false);
        }

        if (adapter is not null)
        {
            // Windows' resolver failed or only answered in families this adapter can't reach:
            // ask the adapter's own DNS servers through the adapter.
            var viaAdapter = (await BoundDnsResolver.ResolveAsync(host, adapter, TimeSpan.FromSeconds(Math.Min(3, timeout.TotalSeconds)), ct).ConfigureAwait(false))
                .Where(a => CanReach(adapter, a.AddressFamily))
                .ToArray();
            if (viaAdapter.Length > 0)
            {
                return (OrderAddresses(viaAdapter), true);
            }
        }

        throw Fail("network.dns",
            adapter is null
                ? $"The server name '{host}' could not be resolved."
                : adapter.Access is InternetAccess.LocalOnly or InternetAccess.None
                    ? $"The server name '{host}' could not be resolved through '{adapter.Name}', which Windows reports has no internet access. Check that mobile data and tethering are on."
                    : $"The server name '{host}' could not be resolved for '{adapter.Name}'.",
            dnsError);
    }

    private static bool CanReach(NetworkAdapterInfo adapter, AddressFamily family) =>
        family == AddressFamily.InterNetwork ? adapter.CanReachIPv4 : adapter.CanReachIPv6;

    /// <summary>IPv4 first: more predictable across tethering and VPN setups; IPv6 is still used when it is all there is.</summary>
    private static IReadOnlyList<IPAddress> OrderAddresses(IEnumerable<IPAddress> addresses) =>
        [.. addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)];

    internal static StreamFailureException Fail(string code, string message, Exception? inner)
    {
        var error = new StreamError(ErrorCategory.Network, code, message, inner?.Message);
        return inner is null ? new StreamFailureException(error) : new StreamFailureException(error, inner);
    }
}

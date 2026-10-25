// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using Linkast.Core;

namespace Linkast.Networking;

/// <summary>Outcome of "Test connection".</summary>
/// <param name="DefaultRouteAdapter">The adapter Windows would use without a selection (what games and other apps use).</param>
public sealed record NetworkProbeResult(
    bool Success,
    StreamError? Error,
    string? AdapterUsed,
    string? LocalEndpoint,
    string? RemoteEndpoint,
    TimeSpan? ConnectTime,
    bool UsedAdapterDns,
    string? DefaultRouteAdapter);

/// <summary>
/// "Test connection": opens a real connection (including the TLS handshake for rtmps) to the
/// configured server through the selected adapter, verifies the interface actually used, then
/// closes it. Sends no stream data and contacts no other server.
/// </summary>
public static class NetworkProbe
{
    public static async Task<NetworkProbeResult> TestAsync(Uri server, NetworkSelection selection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(selection);
        try
        {
            await using var connection = await OutboundConnector.ConnectAsync(server, selection, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            return new NetworkProbeResult(
                true,
                null,
                connection.InterfaceName,
                connection.Local.ToString(),
                $"{server.IdnHost} ({connection.RemoteAddress}:{connection.Port})",
                connection.ConnectTime,
                connection.UsedAdapterDns,
                DefaultRouteAdapterFor(connection.RemoteAddress));
        }
        catch (StreamFailureException ex)
        {
            IPAddress? remote = null;
            if (!IPAddress.TryParse(server.IdnHost, out remote))
            {
                try
                {
                    remote = (await Dns.GetHostAddressesAsync(server.IdnHost, ct).ConfigureAwait(false)).FirstOrDefault();
                }
                catch (System.Net.Sockets.SocketException)
                {
                }
            }

            return new NetworkProbeResult(false, ex.Error, null, null, null, null, false, remote is null ? null : DefaultRouteAdapterFor(remote));
        }
    }

    public static string? DefaultRouteAdapterFor(IPAddress remote)
    {
        var source = InterfaceBinding.DefaultRouteSource(remote);
        return source is null ? null : NetworkAdapterCatalog.FindByAddress(source)?.Name ?? source.ToString();
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Linkast.Networking;

/// <summary>
/// Pins a socket to one network adapter without touching the routing table or needing admin rights:
/// <c>IP_UNICAST_IF</c> selects the outgoing interface and binding to the adapter's address fixes the
/// source address (Windows uses the strong host model, so both must agree).
/// </summary>
public static class InterfaceBinding
{
    /// <summary>IP_UNICAST_IF / IPV6_UNICAST_IF option number (ws2ipdef.h).</summary>
    public const int UnicastInterfaceOption = 31;

    /// <summary>
    /// The option value. For IPv4 Windows expects the interface index in network byte order;
    /// for IPv6 in host byte order.
    /// </summary>
    public static byte[] UnicastInterfaceValue(AddressFamily family, int interfaceIndex)
    {
        var value = new byte[4];
        if (family == AddressFamily.InterNetwork)
        {
            BinaryPrimitives.WriteInt32BigEndian(value, interfaceIndex);
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(value, interfaceIndex);
        }

        return value;
    }

    /// <summary>Creates a socket of <paramref name="family"/> bound to <paramref name="adapter"/>.</summary>
    public static Socket CreateBoundSocket(NetworkAdapterInfo adapter, AddressFamily family, SocketType type, ProtocolType protocol)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        var candidates = family == AddressFamily.InterNetwork ? adapter.IPv4 : adapter.IPv6;
        var source = candidates.Count > 0 ? candidates[0] : throw new SocketException((int)SocketError.AddressNotAvailable);
        var index = family == AddressFamily.InterNetwork ? adapter.IPv4Index : adapter.IPv6Index;

        var socket = new Socket(family, type, protocol);
        try
        {
            if (OperatingSystem.IsWindows() && index > 0)
            {
                var level = family == AddressFamily.InterNetwork ? SocketOptionLevel.IP : SocketOptionLevel.IPv6;
                socket.SetSocketOption(level, (SocketOptionName)UnicastInterfaceOption, UnicastInterfaceValue(family, index));
            }

            socket.Bind(new IPEndPoint(source, 0));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Which local address Windows would use to reach <paramref name="remote"/> with no binding
    /// (the default route, used by games and every other app). Sends no packets.
    /// </summary>
    public static IPAddress? DefaultRouteSource(IPAddress remote)
    {
        ArgumentNullException.ThrowIfNull(remote);
        try
        {
            using var probe = new Socket(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(new IPEndPoint(remote, 9)); // UDP connect only selects a route
            return (probe.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException)
        {
            return null;
        }
    }
}

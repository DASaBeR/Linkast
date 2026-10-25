// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Linkast.Networking;

/// <summary>
/// Minimal DNS client (RFC 1035, A and AAAA over UDP) that sends its queries through a chosen
/// adapter to that adapter's own DNS servers. Used when Windows' resolver fails or answers only
/// with address families the chosen adapter cannot reach, for example when the default route has
/// no internet but the selected phone connection does.
/// </summary>
public static class BoundDnsResolver
{
    public const ushort TypeA = 1;
    public const ushort TypeAaaa = 28;

    private static readonly System.Globalization.IdnMapping Idn = new();

    public static async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, NetworkAdapterInfo adapter, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentNullException.ThrowIfNull(adapter);
        var results = new List<IPAddress>();
        foreach (var server in adapter.DnsServers)
        {
            // The query must leave through the adapter, so it needs a source address of the server's family.
            if ((server.AddressFamily == AddressFamily.InterNetwork ? adapter.IPv4.Count : adapter.IPv6.Count) == 0)
            {
                continue;
            }

            foreach (var type in new[] { TypeA, TypeAaaa })
            {
                try
                {
                    results.AddRange(await QueryAsync(host, type, server, adapter, timeout, ct).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is SocketException or TimeoutException or FormatException or OperationCanceledException && !ct.IsCancellationRequested)
                {
                    // Try the next record type / server.
                }
            }

            if (results.Count > 0)
            {
                break;
            }
        }

        return [.. results.Distinct()];
    }

    private static async Task<IReadOnlyList<IPAddress>> QueryAsync(string host, ushort type, IPAddress server, NetworkAdapterInfo adapter, TimeSpan timeout, CancellationToken ct)
    {
        using var socket = InterfaceBinding.CreateBoundSocket(adapter, server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        var id = (ushort)Random.Shared.Next(ushort.MaxValue);
        var query = BuildQuery(id, host, type);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        await socket.SendToAsync(query, new IPEndPoint(server, 53), cts.Token).ConfigureAwait(false);
        var buffer = new byte[1500];
        while (true)
        {
            var received = await socket.ReceiveFromAsync(buffer, new IPEndPoint(server.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0), cts.Token).ConfigureAwait(false);
            var reply = buffer.AsSpan(0, received.ReceivedBytes);
            if (reply.Length >= 2 && BinaryPrimitives.ReadUInt16BigEndian(reply) == id)
            {
                return ParseAnswers(reply, type);
            }
        }
    }

    /// <summary>Builds a standard recursive query for one name and type.</summary>
    public static byte[] BuildQuery(ushort id, string host, ushort type)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        var name = host.TrimEnd('.');
        var bytes = new List<byte>(32 + name.Length);
        bytes.AddRange([(byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0]); // RD=1, QDCOUNT=1
        foreach (var label in name.Split('.'))
        {
            var l = Encoding.ASCII.GetBytes(Idn.GetAscii(label));
            if (l.Length is 0 or > 63)
            {
                throw new FormatException("Invalid host name label.");
            }

            bytes.Add((byte)l.Length);
            bytes.AddRange(l);
        }

        bytes.AddRange([0, (byte)(type >> 8), (byte)type, 0, 1]); // root, QTYPE, QCLASS=IN
        return [.. bytes];
    }

    /// <summary>Extracts A/AAAA records of <paramref name="type"/> from a reply. Follows name compression.</summary>
    public static IReadOnlyList<IPAddress> ParseAnswers(ReadOnlySpan<byte> reply, ushort type)
    {
        if (reply.Length < 12)
        {
            throw new FormatException("DNS reply too short.");
        }

        var rcode = reply[3] & 0x0F;
        if (rcode != 0)
        {
            return [];
        }

        int qd = BinaryPrimitives.ReadUInt16BigEndian(reply[4..]);
        int an = BinaryPrimitives.ReadUInt16BigEndian(reply[6..]);
        var pos = 12;
        for (var i = 0; i < qd; i++)
        {
            pos = SkipName(reply, pos) + 4;
        }

        var result = new List<IPAddress>();
        for (var i = 0; i < an; i++)
        {
            pos = SkipName(reply, pos);
            if (pos + 10 > reply.Length)
            {
                throw new FormatException("Truncated DNS answer.");
            }

            var rtype = BinaryPrimitives.ReadUInt16BigEndian(reply[pos..]);
            var rdLength = BinaryPrimitives.ReadUInt16BigEndian(reply[(pos + 8)..]);
            pos += 10;
            if (pos + rdLength > reply.Length)
            {
                throw new FormatException("Truncated DNS record.");
            }

            if (rtype == type && ((type == TypeA && rdLength == 4) || (type == TypeAaaa && rdLength == 16)))
            {
                result.Add(new IPAddress(reply.Slice(pos, rdLength)));
            }

            pos += rdLength;
        }

        return result;
    }

    private static int SkipName(ReadOnlySpan<byte> msg, int pos)
    {
        while (true)
        {
            if (pos >= msg.Length)
            {
                throw new FormatException("Truncated DNS name.");
            }

            var len = msg[pos];
            if ((len & 0xC0) == 0xC0)
            {
                return pos + 2; // compression pointer ends the name
            }

            if (len == 0)
            {
                return pos + 1;
            }

            pos += len + 1;
        }
    }

}

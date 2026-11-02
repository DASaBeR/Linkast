// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Text;
using Linkast.Core;

namespace Linkast.Networking;

/// <summary>
/// Opens a tunnel to the ingest server through a SOCKS5 or HTTP (CONNECT) proxy on an already
/// connected socket. The server name is handed to the proxy, so the proxy resolves it (what v2rayN,
/// Clash and similar apps expect: the name is looked up on the far side, not on this PC).
/// </summary>
public static class ProxyTunnel
{
    public static Task EstablishAsync(Stream stream, ProxySettings proxy, string host, int port, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(proxy);
        return proxy.Kind switch
        {
            ProxyKind.Socks5 => Socks5Async(stream, proxy, host, port, ct),
            ProxyKind.Http => HttpConnectAsync(stream, proxy, host, port, ct),
            _ => throw Fail("network.proxy.kind", "No proxy type is selected.", null),
        };
    }

    private static async Task Socks5Async(Stream s, ProxySettings proxy, string host, int port, CancellationToken ct)
    {
        var offerAuth = proxy.HasCredentials;
        await s.WriteAsync(offerAuth ? new byte[] { 5, 2, 0, 2 } : new byte[] { 5, 1, 0 }, ct).ConfigureAwait(false);

        var choice = await ReadExactAsync(s, 2, ct).ConfigureAwait(false);
        if (choice[0] != 5)
        {
            throw Fail("network.proxy.handshake", "The proxy does not speak SOCKS5. Check the proxy type and port (v2rayN: SOCKS is usually 10808, HTTP 10809).", null);
        }

        switch (choice[1])
        {
            case 0:
                break;
            case 2 when offerAuth:
                var user = Encoding.UTF8.GetBytes(proxy.Username!);
                var pass = Encoding.UTF8.GetBytes(proxy.Password?.Reveal() ?? string.Empty);
                if (user.Length > 255 || pass.Length > 255)
                {
                    throw Fail("network.proxy.auth", "The proxy user name or password is too long (255 bytes at most).", null);
                }

                var auth = new byte[3 + user.Length + pass.Length];
                auth[0] = 1;
                auth[1] = (byte)user.Length;
                user.CopyTo(auth, 2);
                auth[2 + user.Length] = (byte)pass.Length;
                pass.CopyTo(auth, 3 + user.Length);
                await s.WriteAsync(auth, ct).ConfigureAwait(false);
                var ok = await ReadExactAsync(s, 2, ct).ConfigureAwait(false);
                if (ok[1] != 0)
                {
                    throw Fail("network.proxy.auth", "The proxy refused the user name or password.", null);
                }

                break;
            case 0xFF:
                throw Fail("network.proxy.auth", proxy.HasCredentials
                    ? "The proxy accepts none of the sign-in methods Linkast offers."
                    : "The proxy needs a user name and password. Enter them in the proxy settings.", null);
            default:
                throw Fail("network.proxy.handshake", "The proxy asked for a sign-in method Linkast does not support.", null);
        }

        byte[] target;
        if (IPAddress.TryParse(host, out var literal))
        {
            var bytes = literal.GetAddressBytes();
            target = [5, 1, 0, literal.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4, .. bytes, (byte)(port >> 8), (byte)port];
        }
        else
        {
            var name = Encoding.ASCII.GetBytes(host);
            if (name.Length is 0 or > 255)
            {
                throw Fail("network.proxy.handshake", "The server name is not valid for a proxy.", null);
            }

            target = [5, 1, 0, 3, (byte)name.Length, .. name, (byte)(port >> 8), (byte)port];
        }

        await s.WriteAsync(target, ct).ConfigureAwait(false);

        var head = await ReadExactAsync(s, 4, ct).ConfigureAwait(false);
        if (head[1] != 0)
        {
            throw Fail("network.proxy.refused", $"The proxy could not connect to {host}: {Socks5Reason(head[1])}", null);
        }

        // Skip the bound address: its length depends on the address type.
        var remaining = head[3] switch
        {
            1 => 4 + 2,
            4 => 16 + 2,
            3 => (await ReadExactAsync(s, 1, ct).ConfigureAwait(false))[0] + 2,
            _ => throw Fail("network.proxy.handshake", "The proxy sent an answer Linkast does not understand.", null),
        };
        await ReadExactAsync(s, remaining, ct).ConfigureAwait(false);
    }

    private static string Socks5Reason(byte code) => code switch
    {
        2 => "the proxy's rules do not allow it.",
        3 => "the network is unreachable from the proxy.",
        4 => "the server could not be reached from the proxy (check the server name).",
        5 => "the server refused the connection.",
        6 => "the connection timed out.",
        7 => "the proxy does not support this command.",
        8 => "the proxy does not support this address type.",
        _ => "general failure of the proxy.",
    };

    private static async Task HttpConnectAsync(Stream s, ProxySettings proxy, string host, int port, CancellationToken ct)
    {
        var authority = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]:{port}" : $"{host}:{port}";
        var request = new StringBuilder()
            .Append("CONNECT ").Append(authority).Append(" HTTP/1.1\r\n")
            .Append("Host: ").Append(authority).Append("\r\n")
            .Append("Proxy-Connection: Keep-Alive\r\n");
        if (proxy.HasCredentials)
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{proxy.Username}:{proxy.Password?.Reveal()}"));
            request.Append("Proxy-Authorization: Basic ").Append(token).Append("\r\n");
        }

        request.Append("\r\n");
        await s.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), ct).ConfigureAwait(false);

        // Read byte by byte up to the blank line: anything after it already belongs to the tunnel.
        var header = new StringBuilder();
        var one = new byte[1];
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (header.Length > 16 * 1024)
            {
                throw Fail("network.proxy.handshake", "The proxy sent an answer that is too long to be an HTTP proxy reply.", null);
            }

            if (await s.ReadAsync(one, ct).ConfigureAwait(false) == 0)
            {
                throw Fail("network.proxy.handshake", "The proxy closed the connection. Check the proxy type and port (v2rayN: HTTP is usually 10809).", null);
            }

            header.Append((char)one[0]);
        }

        var statusLine = header.ToString().Split("\r\n", 2)[0];
        var parts = statusLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].StartsWith("HTTP/", StringComparison.Ordinal) || !int.TryParse(parts[1], out var status))
        {
            throw Fail("network.proxy.handshake", "The proxy does not speak HTTP. Check the proxy type and port (v2rayN: HTTP is usually 10809, SOCKS 10808).", null);
        }

        if (status == 407)
        {
            throw Fail("network.proxy.auth", proxy.HasCredentials
                ? "The proxy refused the user name or password."
                : "The proxy needs a user name and password. Enter them in the proxy settings.", null);
        }

        if (status is < 200 or >= 300)
        {
            throw Fail("network.proxy.refused", $"The proxy could not connect to {host} (HTTP {status}).", null);
        }
    }

    private static async Task<byte[]> ReadExactAsync(Stream s, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await s.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                throw Fail("network.proxy.handshake", "The proxy closed the connection during the handshake. Check the proxy type and port.", null);
            }

            read += n;
        }

        return buffer;
    }

    private static StreamFailureException Fail(string code, string message, Exception? inner) =>
        OutboundConnector.Fail(code, message, inner);
}

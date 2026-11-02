// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Text;
using Linkast.Core;

namespace Linkast.Networking.Tests;

public class ProxyTunnelTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>A one-connection fake proxy. <paramref name="serve"/> gets the accepted stream; it may echo to prove the tunnel works.</summary>
    private sealed class FakeProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _task;

        public FakeProxy(Func<Stream, Task> serve)
        {
            _listener.Start();
            _task = Task.Run(async () =>
            {
                using var client = await _listener.AcceptTcpClientAsync();
                await serve(client.GetStream());
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try
            {
                await _task.WaitAsync(Timeout);
            }
            catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or ObjectDisposedException)
            {
            }
        }
    }

    private static async Task<byte[]> ReadAsync(Stream s, int count)
    {
        var b = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await s.ReadAsync(b.AsMemory(read));
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        return b[..read];
    }

    private static async Task<string> ReadHeaderAsync(Stream s)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (!sb.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await s.ReadAsync(one) == 1)
        {
            sb.Append((char)one[0]);
        }

        return sb.ToString();
    }

    private static ProxySettings Proxy(ProxyKind kind, int port, string? user = null, string? pass = null) =>
        new(kind, "127.0.0.1", port, user, pass is null ? null : new Secret(pass));

    [Fact]
    public async Task Socks5_sends_the_server_name_to_the_proxy_and_opens_the_tunnel()
    {
        string? requestedHost = null;
        await using var proxy = new FakeProxy(async s =>
        {
            Assert.Equal(new byte[] { 5, 1, 0 }, await ReadAsync(s, 3));
            await s.WriteAsync(new byte[] { 5, 0 });
            var head = await ReadAsync(s, 5);
            Assert.Equal(new byte[] { 5, 1, 0, 3 }, head[..4]);
            var rest = await ReadAsync(s, head[4] + 2);
            requestedHost = Encoding.ASCII.GetString(rest, 0, head[4]);
            Assert.Equal(1935, (rest[^2] << 8) | rest[^1]);
            await s.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
            await s.WriteAsync("hello"u8.ToArray());
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.Port);
        var stream = client.GetStream();
        await ProxyTunnel.EstablishAsync(stream, Proxy(ProxyKind.Socks5, proxy.Port), "live.example.com", 1935, CancellationToken.None);

        Assert.Equal("live.example.com", requestedHost);
        Assert.Equal("hello", Encoding.ASCII.GetString(await ReadAsync(stream, 5)));
    }

    [Fact]
    public async Task Socks5_signs_in_with_user_name_and_password()
    {
        string? user = null, pass = null;
        await using var proxy = new FakeProxy(async s =>
        {
            Assert.Equal(new byte[] { 5, 2, 0, 2 }, await ReadAsync(s, 4));
            await s.WriteAsync(new byte[] { 5, 2 });
            var ver = await ReadAsync(s, 2);
            user = Encoding.UTF8.GetString(await ReadAsync(s, ver[1]));
            var plen = (await ReadAsync(s, 1))[0];
            pass = Encoding.UTF8.GetString(await ReadAsync(s, plen));
            await s.WriteAsync(new byte[] { 1, 0 });
            var head = await ReadAsync(s, 5);
            await ReadAsync(s, head[4] + 2);
            await s.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.Port);
        await ProxyTunnel.EstablishAsync(client.GetStream(), Proxy(ProxyKind.Socks5, proxy.Port, "arka", "s3cret"), "live.example.com", 1935, CancellationToken.None);

        Assert.Equal("arka", user);
        Assert.Equal("s3cret", pass);
    }

    [Fact]
    public async Task Socks5_wrong_password_is_reported_as_an_auth_error()
    {
        await using var proxy = new FakeProxy(async s =>
        {
            await ReadAsync(s, 4);
            await s.WriteAsync(new byte[] { 5, 2 });
            var ver = await ReadAsync(s, 2);
            await ReadAsync(s, ver[1]);
            var plen = (await ReadAsync(s, 1))[0];
            await ReadAsync(s, plen);
            await s.WriteAsync(new byte[] { 1, 1 });
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.Port);
        var e = await Assert.ThrowsAsync<StreamFailureException>(() =>
            ProxyTunnel.EstablishAsync(client.GetStream(), Proxy(ProxyKind.Socks5, proxy.Port, "a", "wrong"), "live.example.com", 1935, CancellationToken.None));

        Assert.Equal("network.proxy.auth", e.Error.Code);
    }

    [Fact]
    public async Task Socks5_connection_refused_by_the_proxy_is_reported_with_the_reason()
    {
        await using var proxy = new FakeProxy(async s =>
        {
            await ReadAsync(s, 3);
            await s.WriteAsync(new byte[] { 5, 0 });
            var head = await ReadAsync(s, 5);
            await ReadAsync(s, head[4] + 2);
            await s.WriteAsync(new byte[] { 5, 5, 0, 1, 0, 0, 0, 0, 0, 0 });
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.Port);
        var e = await Assert.ThrowsAsync<StreamFailureException>(() =>
            ProxyTunnel.EstablishAsync(client.GetStream(), Proxy(ProxyKind.Socks5, proxy.Port), "live.example.com", 1935, CancellationToken.None));

        Assert.Equal("network.proxy.refused", e.Error.Code);
    }

    [Fact]
    public async Task Http_connect_sends_the_target_and_credentials_and_leaves_tunnel_bytes_unread()
    {
        string? header = null;
        await using var proxy = new FakeProxy(async s =>
        {
            header = await ReadHeaderAsync(s);
            // The tunnel bytes arrive in the same write as the status line: they must not be swallowed.
            await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\nProxy-Agent: fake\r\n\r\nhello"));
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.Port);
        var stream = client.GetStream();
        await ProxyTunnel.EstablishAsync(stream, Proxy(ProxyKind.Http, proxy.Port, "arka", "s3cret"), "live.example.com", 1935, CancellationToken.None);

        Assert.StartsWith("CONNECT live.example.com:1935 HTTP/1.1\r\n", header);
        Assert.Contains("Proxy-Authorization: Basic " + Convert.ToBase64String("arka:s3cret"u8.ToArray()), header);
        Assert.Equal("hello", Encoding.ASCII.GetString(await ReadAsync(stream, 5)));
    }

    [Theory]
    [InlineData("HTTP/1.1 407 Proxy Authentication Required\r\n\r\n", "network.proxy.auth")]
    [InlineData("HTTP/1.1 403 Forbidden\r\n\r\n", "network.proxy.refused")]
    [InlineData("RTMP nonsense\r\n\r\n", "network.proxy.handshake")]
    public async Task Http_connect_failures_are_classified(string reply, string code)
    {
        await using var proxy = new FakeProxy(async s =>
        {
            await ReadHeaderAsync(s);
            await s.WriteAsync(Encoding.ASCII.GetBytes(reply));
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.Port);
        var e = await Assert.ThrowsAsync<StreamFailureException>(() =>
            ProxyTunnel.EstablishAsync(client.GetStream(), Proxy(ProxyKind.Http, proxy.Port), "live.example.com", 1935, CancellationToken.None));

        Assert.Equal(code, e.Error.Code);
    }

    [Fact]
    public async Task Connector_goes_through_the_proxy_and_never_resolves_the_server_name_itself()
    {
        await using var proxy = new FakeProxy(async s =>
        {
            await ReadAsync(s, 3);
            await s.WriteAsync(new byte[] { 5, 0 });
            var head = await ReadAsync(s, 5);
            await ReadAsync(s, head[4] + 2);
            await s.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
            await s.WriteAsync("ok"u8.ToArray());
        });

        // A name that cannot be resolved here: only the proxy could know it.
        var selection = NetworkSelection.SystemDefault with { Proxy = Proxy(ProxyKind.Socks5, proxy.Port) };
        await using var connection = await OutboundConnector.ConnectAsync(new Uri("rtmp://ingest.invalid.linkast.test/app"), selection, Timeout, CancellationToken.None);

        Assert.NotNull(connection.ViaProxy);
        Assert.Equal("ok", Encoding.ASCII.GetString(await ReadAsync(connection.Stream, 2)));
    }

    [Fact]
    public async Task Connector_reports_a_proxy_that_is_not_running()
    {
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();

        var selection = NetworkSelection.SystemDefault with { Proxy = Proxy(ProxyKind.Socks5, port) };
        var e = await Assert.ThrowsAsync<StreamFailureException>(() =>
            OutboundConnector.ConnectAsync(new Uri("rtmp://live.example.com/app"), selection, Timeout, CancellationToken.None));

        Assert.Equal("network.proxy.connect", e.Error.Code);
    }

    [Fact]
    public void An_unfinished_proxy_setting_is_not_enabled()
    {
        Assert.False(new ProxySettings(ProxyKind.None, "127.0.0.1", 10808).IsEnabled);
        Assert.False(new ProxySettings(ProxyKind.Socks5, " ", 10808).IsEnabled);
        Assert.False(new ProxySettings(ProxyKind.Socks5, "127.0.0.1", 0).IsEnabled);
        Assert.True(new ProxySettings(ProxyKind.Http, "127.0.0.1", 10809).IsEnabled);
    }
}

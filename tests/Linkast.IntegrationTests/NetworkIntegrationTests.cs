// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Linkast.Core;
using Linkast.Engine.FFmpeg;
using Linkast.Networking;
using Xunit.Abstractions;

namespace Linkast.IntegrationTests;

/// <summary>
/// Network adapter selection against the real adapters of this PC. Never changes adapter or routing
/// state. Tests that contact servers on the internet are opt-in: set LINKAST_NETWORK_EXTERNAL=1.
/// </summary>
[Collection("ingest")]
public sealed class NetworkIntegrationTests(ITestOutputHelper output)
{
    private static bool External => Environment.GetEnvironmentVariable("LINKAST_NETWORK_EXTERNAL") == "1";

    private static IReadOnlyList<NetworkAdapterInfo> Usable =>
        [.. NetworkAdapterCatalog.List().Where(a => a.IsUsable && a.Kind != NetworkAdapterKind.Virtual)];

    /// <summary>Adapters Windows reports as having internet access; the external tests can only pass on these.</summary>
    private static IReadOnlyList<NetworkAdapterInfo> Online => [.. Usable.Where(a => a.Access == InternetAccess.Internet)];

    [Fact]
    public void Lists_and_classifies_adapters()
    {
        var all = NetworkAdapterCatalog.List();
        foreach (var a in all)
        {
            output.WriteLine($"{a.Name,-36} {a.KindLabel,-18} up={a.IsUp,-5} usable={a.IsUsable,-5} windows={a.Access,-10} v4={string.Join(',', a.IPv4)} gw4={a.HasIPv4Gateway} idx={a.IPv4Index} dns={string.Join(',', a.DnsServers)}");
        }

        Assert.NotEmpty(all);
        Assert.Contains(all, a => a.IsUsable);
        Assert.DoesNotContain(all, a => a.Kind == NetworkAdapterKind.Loopback);
    }

    [Fact]
    public void Disconnected_missing_and_no_gateway_adapters_fail_clearly()
    {
        var missing = Assert.Throws<StreamFailureException>(() => OutboundConnector.ResolveAdapter(new NetworkSelection("{00000000-dead-beef-0000-000000000000}", "Old modem")));
        Assert.Equal("network.interface.missing", missing.Error.Code);
        Assert.Contains("Old modem", missing.Error.UserMessage, StringComparison.Ordinal);
        Assert.True(missing.Error.IsRetryable);

        var down = NetworkAdapterCatalog.List().FirstOrDefault(a => !a.IsUp);
        if (down is not null)
        {
            var e = Assert.Throws<StreamFailureException>(() => OutboundConnector.ResolveAdapter(new NetworkSelection(down.Id, down.Name)));
            output.WriteLine($"{down.Name}: {e.Error.Code}");
            Assert.Equal("network.interface.down", e.Error.Code);
        }

        var noGateway = NetworkAdapterCatalog.List().FirstOrDefault(a => a.IsUp && !a.IsUsable);
        if (noGateway is not null)
        {
            var e = Assert.Throws<StreamFailureException>(() => OutboundConnector.ResolveAdapter(new NetworkSelection(noGateway.Id, noGateway.Name)));
            output.WriteLine($"{noGateway.Name}: {e.Error.Code}");
            Assert.Equal("network.interface.no_internet", e.Error.Code);
        }
    }

    [Fact]
    public void Default_route_is_reported()
    {
        var name = NetworkProbe.DefaultRouteAdapterFor(IPAddress.Parse("1.1.1.1"));
        output.WriteLine($"Windows default route for the internet: {name}");
        Assert.False(string.IsNullOrEmpty(name));
    }

    [SkippableFact]
    public async Task Bound_connection_leaves_through_each_selected_adapter()
    {
        Skip.IfNot(External, "Set LINKAST_NETWORK_EXTERNAL=1 to contact live.twitch.tv.");
        Skip.If(Online.Count == 0, "No adapter with internet access.");
        foreach (var a in Usable.Except(Online))
        {
            output.WriteLine($"skipped {a.Name}: Windows reports {a.Access}");
        }

        foreach (var adapter in Online)
        {
            var result = await NetworkProbe.TestAsync(new Uri("rtmp://live.twitch.tv/app"), new NetworkSelection(adapter.Id, adapter.Name));
            output.WriteLine($"{adapter.Name}: ok={result.Success} via={result.AdapterUsed} local={result.LocalEndpoint} remote={result.RemoteEndpoint} {result.ConnectTime?.TotalMilliseconds:0} ms default={result.DefaultRouteAdapter} err={result.Error}");
            Assert.True(result.Success, $"{adapter.Name}: {result.Error}");
            Assert.Equal(adapter.Name, result.AdapterUsed);
        }
    }

    [SkippableFact]
    public async Task Each_adapter_has_its_own_public_address()
    {
        Skip.IfNot(External, "Set LINKAST_NETWORK_EXTERNAL=1 to contact api.ipify.org.");
        Skip.If(Online.Count < 2, "Needs two adapters with internet access.");
        var ips = new Dictionary<string, string>();
        foreach (var adapter in Online)
        {
            using var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (ctx, ct) =>
                {
                    var c = await OutboundConnector.ConnectAsync(new Uri($"rtmp://{ctx.DnsEndPoint.Host}:{ctx.DnsEndPoint.Port}/x"), new NetworkSelection(adapter.Id, adapter.Name), TimeSpan.FromSeconds(10), ct);
                    return c.Stream;
                },
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            var ip = (await http.GetStringAsync(new Uri("http://api.ipify.org"))).Trim();
            output.WriteLine($"{adapter.Name} ({adapter.KindLabel}) -> public IP {ip}");
            ips[adapter.Name] = ip;
        }

        // A phone or cellular modem uses a different ISP, so its public address must differ from the
        // others. Two wired paths (or a split-tunnel VPN) can legitimately share one public address.
        var mobile = Online.FirstOrDefault(a => a.Kind is NetworkAdapterKind.UsbTethering or NetworkAdapterKind.MobileBroadband);
        if (mobile is null)
        {
            output.WriteLine("No mobile/tethered adapter with internet right now; public addresses recorded, distinctness not required.");
            return;
        }

        Assert.All(ips.Where(kv => kv.Key != mobile.Name), kv => Assert.NotEqual(ips[mobile.Name], kv.Value));
    }

    [SkippableFact]
    public async Task Dns_through_adapter_resolves()
    {
        Skip.IfNot(External, "Set LINKAST_NETWORK_EXTERNAL=1 to query the adapter's DNS server.");
        var adapter = Online.FirstOrDefault(a => a.Kind is NetworkAdapterKind.UsbTethering or NetworkAdapterKind.MobileBroadband) ?? Online.FirstOrDefault();
        Skip.If(adapter is null, "No adapter with internet access.");
        var addresses = await BoundDnsResolver.ResolveAsync("live.twitch.tv", adapter, TimeSpan.FromSeconds(3), CancellationToken.None);
        output.WriteLine($"{adapter.Name} DNS {string.Join(',', adapter.DnsServers)} -> {string.Join(',', addresses)}");
        Assert.NotEmpty(addresses);
    }

    [SkippableFact]
    public async Task Streams_through_selected_adapter_end_to_end()
    {
        var adapter = Usable.FirstOrDefault(a => a.Kind is NetworkAdapterKind.UsbTethering or NetworkAdapterKind.MobileBroadband) ?? Usable.FirstOrDefault(a => a.CanReachIPv4);
        Skip.If(adapter is null || !adapter.CanReachIPv4, "No usable IPv4 adapter.");
        FfmpegInstallation ffmpeg;
        try
        {
            ffmpeg = await FfmpegLocator.LocateAsync();
        }
        catch (StreamFailureException)
        {
            Skip.If(true, "ffmpeg not found.");
            return;
        }

        Skip.IfNot(Reachable(IPAddress.Loopback, 1935), "MediaMTX not running.");

        // The test ingest only listens on loopback. A forwarder on the adapter's own address carries
        // the relay's bound connection into it, so the stream's socket really is bound to the adapter.
        await using var forwarder = await LocalForwarder.StartAsync(adapter.IPv4[0], new IPEndPoint(IPAddress.Loopback, 1935));
        var key = "net-" + Guid.NewGuid().ToString("N")[..8];
        await using var engine = new FfmpegStreamEngine(ffmpeg, new RelayOutputTransport());
        StreamTelemetry? last = null;
        engine.TelemetryUpdated += (_, t) => last = t;
        engine.StateChanged += (_, e) => output.WriteLine($"state {e.Current} {e.Error}");

        await engine.StartAsync(new StreamSession(
            new TestPatternSource(),
            [new TestToneSource()],
            new EncoderSettings("libx264", 640, 360, 30, 1500, Preset: "ultrafast"),
            new Destination("via adapter", new Uri($"rtmp://{adapter.IPv4[0]}:{forwarder.Port}/live"), new Secret(key)),
            new NetworkSelection(adapter.Id, adapter.Name),
            ReconnectPolicy.Disabled));

        var sw = Stopwatch.StartNew();
        while (engine.State != StreamState.Live && sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            await Task.Delay(200);
        }

        await Task.Delay(1500);
        output.WriteLine($"interface used: {last?.Network?.InterfaceName} local {last?.Network?.LocalEndpoint}");
        Assert.Equal(StreamState.Live, engine.State);
        Assert.Equal(adapter.Name, last?.Network?.InterfaceName);
        Assert.StartsWith(adapter.IPv4[0].ToString(), last?.Network?.LocalEndpoint, StringComparison.Ordinal);
        await engine.StopAsync();
    }

    [SkippableFact]
    public async Task Missing_adapter_never_falls_back_and_retries_within_budget()
    {
        FfmpegInstallation ffmpeg;
        try
        {
            ffmpeg = await FfmpegLocator.LocateAsync();
        }
        catch (StreamFailureException)
        {
            Skip.If(true, "ffmpeg not found.");
            return;
        }

        Skip.IfNot(Reachable(IPAddress.Loopback, 1935), "MediaMTX not running.");
        await using var engine = new FfmpegStreamEngine(ffmpeg, new RelayOutputTransport());
        var states = new List<StreamState>();
        StreamError? final = null;
        var reconnects = 0;
        engine.StateChanged += (_, e) =>
        {
            states.Add(e.Current);
            if (e.Current == StreamState.Reconnecting)
            {
                reconnects++;
            }

            if (e.Current == StreamState.Failed)
            {
                final = e.Error;
            }

            output.WriteLine($"state {e.Current} {e.Error}");
        };

        // The destination itself is reachable over the default route; only the chosen adapter is missing.
        await engine.StartAsync(new StreamSession(
            new TestPatternSource(),
            [new TestToneSource()],
            new EncoderSettings("libx264", 640, 360, 30, 1500, Preset: "ultrafast"),
            new Destination("local", new Uri("rtmp://127.0.0.1:1935/live"), new Secret("never-live")),
            new NetworkSelection("{00000000-0000-0000-0000-00000000dead}", "Unplugged 5G modem"),
            new ReconnectPolicy(2, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(60))));

        var sw = Stopwatch.StartNew();
        while (engine.State != StreamState.Failed && sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            await Task.Delay(100);
        }

        Assert.Equal(StreamState.Failed, engine.State);
        Assert.DoesNotContain(StreamState.Live, states);
        Assert.Equal(2, reconnects);
        Assert.Equal("network.interface.missing", final?.Code);
        Assert.Contains("Unplugged 5G modem", final?.UserMessage, StringComparison.Ordinal);
    }

    private static bool Reachable(IPAddress address, int port)
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync(address, port).Wait(TimeSpan.FromSeconds(1)) && c.Connected;
        }
        catch (AggregateException)
        {
            return false;
        }
    }

    /// <summary>Test-only TCP forwarder listening on one local address.</summary>
    private sealed class LocalForwarder : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly IPEndPoint _target;
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;

        private LocalForwarder(TcpListener listener, IPEndPoint target)
        {
            _listener = listener;
            _target = target;
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public static Task<LocalForwarder> StartAsync(IPAddress bind, IPEndPoint target)
        {
            var l = new TcpListener(bind, 0);
            l.Start();
            var f = new LocalForwarder(l, target);
            f._loop = Task.Run(f.RunAsync);
            return Task.FromResult(f);
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            if (_loop is not null)
            {
                await Task.WhenAny(_loop, Task.Delay(2000));
            }

            _cts.Dispose();
        }

        private async Task RunAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var inbound = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(async () =>
                    {
                        using var a = inbound;
                        using var b = new TcpClient();
                        await b.ConnectAsync(_target, _cts.Token);
                        using var sa = a.GetStream();
                        using var sb = b.GetStream();
                        await Task.WhenAny(sa.CopyToAsync(sb, _cts.Token), sb.CopyToAsync(sa, _cts.Token));
                    });
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }
    }
}

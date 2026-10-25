// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Linkast.Core;
using Linkast.Networking;

namespace Linkast.Networking.Tests;

public class AdapterClassificationTests
{
    [Theory]
    [InlineData(NetworkInterfaceType.Ethernet, "Realtek Gaming 2.5GbE Family Controller", "Ethernet", NetworkAdapterKind.Ethernet)]
    [InlineData(NetworkInterfaceType.Ethernet, "Remote NDIS Compatible Device #2", "Ethernet 4", NetworkAdapterKind.UsbTethering)]
    [InlineData(NetworkInterfaceType.Ethernet, "Apple Mobile Device Ethernet", "Ethernet 5", NetworkAdapterKind.UsbTethering)]
    [InlineData(NetworkInterfaceType.Wwanpp, "Generic Mobile Broadband Adapter", "Cellular", NetworkAdapterKind.MobileBroadband)]
    [InlineData(NetworkInterfaceType.Wireless80211, "Intel(R) Wi-Fi 6 AX201 160MHz", "Wi-Fi", NetworkAdapterKind.WiFi)]
    [InlineData(NetworkInterfaceType.Ethernet, "WireSock Virtual Adapter", "Ezconnect Virtual Adapter", NetworkAdapterKind.Vpn)]
    [InlineData(NetworkInterfaceType.Ethernet, "TAP-Windows Adapter V9 for OpenVPN Connect", "Local Area Connection 2", NetworkAdapterKind.Vpn)]
    [InlineData(NetworkInterfaceType.Ethernet, "Hyper-V Virtual Ethernet Adapter", "vEthernet (WSL (Hyper-V firewall))", NetworkAdapterKind.Virtual)]
    [InlineData(NetworkInterfaceType.Loopback, "Software Loopback Interface 1", "Loopback", NetworkAdapterKind.Loopback)]
    public void Classifies_real_adapter_descriptions(NetworkInterfaceType type, string description, string name, NetworkAdapterKind expected) =>
        Assert.Equal(expected, NetworkAdapterCatalog.Classify(type, description, name));
}

public class PseudoAdapterTests
{
    [Theory]
    [InlineData("Ethernet-QoS Packet Scheduler-0000", "Realtek", true)]
    [InlineData("Ethernet 4-WireSock VPN Client Filter Driver-0000", "Remote NDIS", true)]
    [InlineData("Local Area Connection* 6", "WAN Miniport (IP)", true)]
    [InlineData("vSwitch (WSL (Hyper-V firewall))", "Hyper-V Virtual Switch", true)]
    [InlineData("Ethernet (Kernel Debugger)", "Microsoft Kernel Debug Network Adapter", true)]
    [InlineData("Ethernet 4", "Remote NDIS Compatible Device #2", false)]
    [InlineData("Wi-Fi", "Intel(R) Wi-Fi 6 AX201", false)]
    [InlineData("Ezconnect Virtual Adapter", "WireSock Virtual Adapter", false)]
    public void Filters_driver_bindings_and_hidden_miniports(string name, string description, bool pseudo) =>
        Assert.Equal(pseudo, NetworkAdapterCatalog.IsPseudoAdapter(name, description));
}

public class InterfaceBindingTests
{
    [Fact]
    public void Ipv4_index_is_network_byte_order() =>
        Assert.Equal(new byte[] { 0, 0, 0, 26 }, InterfaceBinding.UnicastInterfaceValue(AddressFamily.InterNetwork, 26));

    [Fact]
    public void Ipv6_index_is_host_byte_order() =>
        Assert.Equal(new byte[] { 26, 0, 0, 0 }, InterfaceBinding.UnicastInterfaceValue(AddressFamily.InterNetworkV6, 26));
}

public class AdapterResolutionTests
{
    private static NetworkAdapterInfo Adapter(string id, bool up = true, bool gateway = true, string name = "Phone") => new(
        id, name, "Remote NDIS", NetworkAdapterKind.UsbTethering, up,
        [IPAddress.Parse("10.0.0.2")], [], gateway, false, 7, 0, 0, []);

    [Fact]
    public void Missing_adapter_is_a_retryable_network_error_naming_it()
    {
        var e = Assert.Throws<StreamFailureException>(() => OutboundConnector.ResolveAdapter(new NetworkSelection("gone", "My 5G modem"), [Adapter("other")]));
        Assert.Equal("network.interface.missing", e.Error.Code);
        Assert.Contains("My 5G modem", e.Error.UserMessage, StringComparison.Ordinal);
        Assert.True(e.Error.IsRetryable);
    }

    [Fact]
    public void Down_and_no_gateway_are_reported()
    {
        Assert.Equal("network.interface.down",
            Assert.Throws<StreamFailureException>(() => OutboundConnector.ResolveAdapter(new NetworkSelection("a", "Phone"), [Adapter("a", up: false)])).Error.Code);
        Assert.Equal("network.interface.no_internet",
            Assert.Throws<StreamFailureException>(() => OutboundConnector.ResolveAdapter(new NetworkSelection("a", "Phone"), [Adapter("a", gateway: false)])).Error.Code);
    }

    [Fact]
    public void Usable_adapter_is_returned_and_matches_case_insensitively() =>
        Assert.Equal("Phone", OutboundConnector.ResolveAdapter(new NetworkSelection("{ABC}", "Phone"), [Adapter("{abc}")]).Name);

    [Fact]
    public void Owns_handles_ipv4_mapped_addresses()
    {
        var a = Adapter("a");
        Assert.True(a.Owns(IPAddress.Parse("10.0.0.2").MapToIPv6()));
        Assert.False(a.Owns(IPAddress.Parse("10.0.0.3")));
    }
}

public class DnsMessageTests
{
    [Fact]
    public void Query_has_header_and_encoded_name()
    {
        var q = BoundDnsResolver.BuildQuery(0x1234, "live.twitch.tv", BoundDnsResolver.TypeA);
        Assert.Equal(new byte[] { 0x12, 0x34, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 }, q[..12]);
        Assert.Equal(new byte[] { 4, (byte)'l', (byte)'i', (byte)'v', (byte)'e', 6 }, q[12..18]);
        Assert.Equal(new byte[] { 0, 0, 1, 0, 1 }, q[^5..]);
    }

    [Fact]
    public void Parses_answers_with_compression_and_cname()
    {
        // Reply: question live.twitch.tv A; answers: CNAME (skipped) + A 35.55.18.18, names compressed.
        var question = BoundDnsResolver.BuildQuery(7, "live.twitch.tv", BoundDnsResolver.TypeA);
        var reply = new List<byte>(question);
        reply[2] = 0x81;
        reply[3] = 0x80; // response, no error
        reply[7] = 2; // ANCOUNT
        reply.AddRange([0xC0, 12, 0, 5, 0, 1, 0, 0, 0, 60, 0, 2, 0xC0, 12]); // CNAME -> pointer
        reply.AddRange([0xC0, 12, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 35, 55, 18, 18]); // A
        var result = BoundDnsResolver.ParseAnswers(reply.ToArray(), BoundDnsResolver.TypeA);
        Assert.Equal([IPAddress.Parse("35.55.18.18")], result);
    }

    [Fact]
    public void Error_rcode_gives_no_addresses()
    {
        var reply = BoundDnsResolver.BuildQuery(7, "nope.invalid", BoundDnsResolver.TypeA);
        reply[3] = 0x83; // NXDOMAIN
        Assert.Empty(BoundDnsResolver.ParseAnswers(reply, BoundDnsResolver.TypeA));
    }

    [Fact]
    public void Truncated_reply_is_rejected() =>
        Assert.Throws<FormatException>(() => BoundDnsResolver.ParseAnswers(new byte[] { 0, 1, 0x81, 0x80, 0, 0, 0, 1, 0, 0, 0, 0, 0xC0 }, BoundDnsResolver.TypeA));
}

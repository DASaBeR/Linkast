// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Linkast.Networking;

/// <summary>What kind of connection an adapter is, as a user would describe it.</summary>
public enum NetworkAdapterKind
{
    Ethernet,
    WiFi,

    /// <summary>Built-in or USB cellular modem exposed as a mobile broadband (WWAN) adapter.</summary>
    MobileBroadband,

    /// <summary>A phone or 5G router shared over USB (RNDIS / NCM / CDC Ethernet).</summary>
    UsbTethering,
    Vpn,

    /// <summary>Hyper-V, WSL, VirtualBox, VMware and similar host-only adapters.</summary>
    Virtual,
    Loopback,
    Other,
}

/// <summary>What Windows itself reports about a network's reachability (Network Connectivity Status Indicator).</summary>
public enum InternetAccess
{
    Unknown,
    None,

    /// <summary>Connected, but Windows found no internet access (local network only).</summary>
    LocalOnly,

    /// <summary>A captive portal or similar restriction (for example hotel Wi-Fi sign-in).</summary>
    Constrained,
    Internet,
}

/// <summary>A snapshot of one network adapter, taken fresh whenever needed (addresses change).</summary>
public sealed record NetworkAdapterInfo(
    string Id,
    string Name,
    string Description,
    NetworkAdapterKind Kind,
    bool IsUp,
    IReadOnlyList<IPAddress> IPv4,
    IReadOnlyList<IPAddress> IPv6,
    bool HasIPv4Gateway,
    bool HasIPv6Gateway,
    int IPv4Index,
    int IPv6Index,
    long SpeedBitsPerSecond,
    IReadOnlyList<IPAddress> DnsServers)
{
    /// <summary>Windows' own view of this network. Advisory: Linkast still tests the real connection.</summary>
    public InternetAccess Access { get; init; } = InternetAccess.Unknown;

    public string AccessLabel => Access switch
    {
        InternetAccess.Internet => "Internet",
        InternetAccess.LocalOnly => "Local network only (no internet)",
        InternetAccess.Constrained => "Limited (sign-in may be needed)",
        InternetAccess.None => "No connectivity",
        _ => "Unknown",
    };

    public bool CanReachIPv4 => IsUp && IPv4.Count > 0 && HasIPv4Gateway && IPv4Index > 0;

    public bool CanReachIPv6 => IsUp && IPv6.Count > 0 && HasIPv6Gateway && IPv6Index > 0;

    /// <summary>Up, with an address and a gateway in at least one family: it can reach the internet.</summary>
    public bool IsUsable => CanReachIPv4 || CanReachIPv6;

    public string KindLabel => Kind switch
    {
        NetworkAdapterKind.WiFi => "Wi-Fi",
        NetworkAdapterKind.MobileBroadband => "Mobile broadband",
        NetworkAdapterKind.UsbTethering => "USB phone / modem",
        NetworkAdapterKind.Vpn => "VPN",
        NetworkAdapterKind.Virtual => "Virtual",
        _ => Kind.ToString(),
    };

    public string AddressSummary => IPv4.Count + IPv6.Count == 0
        ? "no address"
        : string.Join(", ", IPv4.Concat(IPv6).Select(a => a.ToString()));

    public bool Owns(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var a = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return IPv4.Contains(a) || IPv6.Contains(a);
    }

    public override string ToString() =>
        $"{Name} — {KindLabel}{(IsUp ? string.Empty : " (disconnected)")}{(IsUp && !IsUsable ? " (no internet route)" : string.Empty)}";
}

/// <summary>Enumerates adapters with System.Net.NetworkInformation (no admin rights, no routing changes).</summary>
public static class NetworkAdapterCatalog
{
    public static IReadOnlyList<NetworkAdapterInfo> List()
    {
        var result = new List<NetworkAdapterInfo>();
        var access = ReadConnectivity();
        NetworkInterface[] nics;
        try
        {
            nics = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return result;
        }

        foreach (var nic in nics)
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || IsPseudoAdapter(nic.Name, nic.Description))
            {
                continue;
            }

            try
            {
                var info = Describe(nic);
                result.Add(access.TryGetValue(info.Id, out var level) ? info with { Access = level } : info);
            }
            catch (NetworkInformationException)
            {
                // Adapter vanished while we were reading it.
            }
        }

        return
        [
            .. result
                .OrderByDescending(a => a.IsUsable)
                .ThenByDescending(a => a.IsUp)
                .ThenBy(a => KindOrder(a.Kind))
                .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
        ];
    }

    public static NetworkAdapterInfo? Find(string id) =>
        List().FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The adapter that owns a local address, for example a connected socket's local endpoint.</summary>
    public static NetworkAdapterInfo? FindByAddress(IPAddress address) => List().FirstOrDefault(a => a.Owns(address));

    /// <summary>
    /// Driver filter bindings ("Ethernet-QoS Packet Scheduler-0000"), hidden WAN/Wi-Fi Direct miniports
    /// ("Local Area Connection* 6"), virtual switches and the kernel debugger are not connections a
    /// user can stream over, so they are not listed.
    /// </summary>
    public static bool IsPseudoAdapter(string name, string description)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(description);
        return System.Text.RegularExpressions.Regex.IsMatch(name, @"-\d{4}$")
            || name.Contains('*', StringComparison.Ordinal)
            || name.StartsWith("vSwitch", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Kernel Debugger", StringComparison.OrdinalIgnoreCase)
            || description.Contains("Kernel Debug", StringComparison.OrdinalIgnoreCase)
            || description.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase)
            || description.Contains("Wi-Fi Direct Virtual", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Connectivity per adapter GUID from Windows' connection profiles.</summary>
    private static Dictionary<string, InternetAccess> ReadConnectivity()
    {
        var map = new Dictionary<string, InternetAccess>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var profile in global::Windows.Networking.Connectivity.NetworkInformation.GetConnectionProfiles())
            {
                var adapterId = profile.NetworkAdapter?.NetworkAdapterId;
                if (adapterId is null)
                {
                    continue;
                }

                var level = profile.GetNetworkConnectivityLevel() switch
                {
                    global::Windows.Networking.Connectivity.NetworkConnectivityLevel.InternetAccess => InternetAccess.Internet,
                    global::Windows.Networking.Connectivity.NetworkConnectivityLevel.ConstrainedInternetAccess => InternetAccess.Constrained,
                    global::Windows.Networking.Connectivity.NetworkConnectivityLevel.LocalAccess => InternetAccess.LocalOnly,
                    global::Windows.Networking.Connectivity.NetworkConnectivityLevel.None => InternetAccess.None,
                    _ => InternetAccess.Unknown,
                };
                var key = adapterId.Value.ToString("B");
                if (!map.TryGetValue(key, out var existing) || level > existing)
                {
                    map[key] = level;
                }
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // Connectivity is advisory; adapters are still listed.
        }

        return map;
    }

    internal static NetworkAdapterInfo Describe(NetworkInterface nic)
    {
        var props = nic.GetIPProperties();
        var v4 = props.UnicastAddresses.Select(u => u.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IsLinkLocalV4(a)).ToList();
        var v6 = props.UnicastAddresses.Select(u => u.Address).Where(a => a.AddressFamily == AddressFamily.InterNetworkV6 && IsGlobalV6(a)).ToList();
        var gateways = props.GatewayAddresses.Select(g => g.Address).ToList();
        int v4Index = 0, v6Index = 0;
        try
        {
            v4Index = props.GetIPv4Properties()?.Index ?? 0;
        }
        catch (NetworkInformationException)
        {
        }

        try
        {
            v6Index = props.GetIPv6Properties()?.Index ?? 0;
        }
        catch (NetworkInformationException)
        {
        }

        long speed = 0;
        try
        {
            speed = nic.Speed;
        }
        catch (PlatformNotSupportedException)
        {
        }

        return new NetworkAdapterInfo(
            nic.Id,
            nic.Name,
            nic.Description,
            Classify(nic.NetworkInterfaceType, nic.Description, nic.Name),
            nic.OperationalStatus == OperationalStatus.Up,
            v4,
            v6,
            gateways.Any(g => g.AddressFamily == AddressFamily.InterNetwork), // 0.0.0.0 = on-link (point-to-point VPNs)
            gateways.Any(g => g.AddressFamily == AddressFamily.InterNetworkV6),
            v4Index,
            v6Index,
            speed,
            [.. props.DnsAddresses.Where(d => !(d.AddressFamily == AddressFamily.InterNetworkV6 && d.IsIPv6SiteLocal))]);
    }

    /// <summary>Maps Windows' adapter type plus its description to a user-facing kind.</summary>
    public static NetworkAdapterKind Classify(NetworkInterfaceType type, string description, string name)
    {
        var d = (description + " " + name).ToUpperInvariant();
        if (type == NetworkInterfaceType.Loopback)
        {
            return NetworkAdapterKind.Loopback;
        }

        if (type is NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 || d.Contains("MOBILE BROADBAND", StringComparison.Ordinal) || d.Contains("WWAN", StringComparison.Ordinal))
        {
            return NetworkAdapterKind.MobileBroadband;
        }

        if (d.Contains("REMOTE NDIS", StringComparison.Ordinal) || d.Contains("RNDIS", StringComparison.Ordinal)
            || d.Contains("NCM", StringComparison.Ordinal) || d.Contains("CDC ETHERNET", StringComparison.Ordinal)
            || d.Contains("APPLE MOBILE DEVICE ETHERNET", StringComparison.Ordinal) || d.Contains("IPHONE", StringComparison.Ordinal)
            || d.Contains("ANDROID", StringComparison.Ordinal) || d.Contains("USB TETHER", StringComparison.Ordinal))
        {
            return NetworkAdapterKind.UsbTethering;
        }

        string[] vpn = ["VPN", "WIREGUARD", "WIRESOCK", "OPENVPN", "TAP-WINDOWS", "WINTUN", "TAILSCALE", "ZEROTIER", "NORDLYNX", "FORTINET", "CISCO ANYCONNECT", "PANGP", "DATA CHANNEL OFFLOAD"];
        if (type is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel || vpn.Any(v => d.Contains(v, StringComparison.Ordinal)))
        {
            return NetworkAdapterKind.Vpn;
        }

        string[] virtualNames = ["HYPER-V", "VETHERNET", "VIRTUALBOX", "VMWARE", "VIRTUAL ETHERNET", "WSL", "DOCKER", "LOOPBACK ADAPTER"];
        if (virtualNames.Any(v => d.Contains(v, StringComparison.Ordinal)))
        {
            return NetworkAdapterKind.Virtual;
        }

        if (type == NetworkInterfaceType.Wireless80211 || d.Contains("WI-FI", StringComparison.Ordinal) || d.Contains("WIRELESS", StringComparison.Ordinal) || d.Contains("802.11", StringComparison.Ordinal))
        {
            return NetworkAdapterKind.WiFi;
        }

        if (type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit)
        {
            return NetworkAdapterKind.Ethernet;
        }

        return NetworkAdapterKind.Other;
    }

    private static int KindOrder(NetworkAdapterKind k) => k switch
    {
        NetworkAdapterKind.Ethernet => 0,
        NetworkAdapterKind.WiFi => 1,
        NetworkAdapterKind.UsbTethering => 2,
        NetworkAdapterKind.MobileBroadband => 3,
        NetworkAdapterKind.Vpn => 4,
        NetworkAdapterKind.Other => 5,
        _ => 6,
    };

    private static bool IsLinkLocalV4(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b[0] == 169 && b[1] == 254;
    }

    private static bool IsGlobalV6(IPAddress a) =>
        !a.IsIPv6LinkLocal && !a.IsIPv6SiteLocal && !a.IsIPv6Multicast && !IPAddress.IsLoopback(a) && !a.IsIPv6Teredo
        && (a.GetAddressBytes()[0] & 0xFE) != 0xFC; // exclude unique-local fc00::/7
}

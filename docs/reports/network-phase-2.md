# Phase 2 report: network adapter selection

**Status:** Implemented. Validated on the development machine for Ethernet, a VPN adapter, and the failure paths. Streaming over the internet through the USB-tethered phone could not be confirmed: during testing Windows reported the phone connection as "local network only", and independent tools (curl, ping) also could not reach the internet through it. See the field tests in [field-test-plan.md](../field-test-plan.md#c-network-adapter-selection-from-phase-2).

**Test machine adapters:**

| Adapter | Kind as Linkast shows it | Windows says | Notes |
|---|---|---|---|
| Ethernet (Realtek 2.5GbE) | Ethernet | Internet | Private LAN address |
| Ethernet 4 (Remote NDIS) | USB phone / modem | Local network only | A phone's USB tethering, private 10.x address |
| WireSock VPN adapter | VPN | Internet | Windows' default route (metric 5) |
| vEthernet (WSL) | Virtual | Local only | No gateway: not usable |
| OpenVPN / WireSock spares, Ethernet 2 and 3 | VPN / USB | — | Disconnected |

No adapter, VPN or routing setting was changed during testing.

## What was built

- **Adapter catalog.**
  - Lists real connections with kind (Ethernet, Wi-Fi, USB phone or modem, mobile broadband, VPN, virtual), status, addresses, gateway, DNS servers, and Windows' connectivity verdict.
  - Hides driver filter bindings, hidden miniports, virtual switches and the kernel debugger.
- **Binding and verification** ([ADR 0006](../adr/0006-adapter-binding.md)).
  - The relay's socket uses `IP_UNICAST_IF` plus a source-address bind.
  - The real local endpoint is verified after connecting; a mismatch is an error.
  - No routing changes and no admin rights.
- **DNS.** Answers are filtered to the families the adapter can reach. If that leaves nothing, Linkast falls back to a minimal DNS client that asks the adapter's own DNS servers through the adapter.
- **Clear errors** (Network category, so retried within the reconnect budget):

| Code | When |
|---|---|
| `network.interface.missing` | The chosen adapter isn't present |
| `network.interface.down` | It's present but disconnected |
| `network.interface.no_internet` | No gateway, or Windows says local-only and the connection failed, with hints about mobile data and VPNs |
| `network.interface.no_route` | The adapter can't reach the server |
| `network.interface.family` | The server address is IPv4 or IPv6 but the adapter lacks that family |
| `network.route.mismatch` | The socket left through a different adapter |
| `network.dns` | The server name can't be resolved for that adapter |

- **No silent fallback.** A remembered adapter that isn't connected stays selected and is shown as "(not connected)".
- **Test connection.** It connects to the real server through the chosen network (including TLS for rtmps), sends no video, and reports:
  - the adapter actually used and the local and remote endpoints;
  - the connect time, and whether the adapter's own DNS was used;
  - which adapter Windows would have used by default.
- **UI.** A "Network for your stream" card with Refresh. The list refreshes automatically when networks change. The stream's telemetry shows "✓ verified: your chosen network".
- **Persistence.** The adapter ID and name are saved as additive settings fields, so no migration is needed.
- **Connector hardening.** One overall deadline per connection attempt, and at most three addresses tried, so failures arrive in about 15 s rather than minutes.

## PRD 4.6 checklist

| Requirement | State | Evidence |
|---|---|---|
| View available interfaces | Validated | Adapter list on this PC (table above) |
| Identify Ethernet, Wi-Fi, USB broadband, others | Validated (Ethernet, USB tethering, VPN, virtual); Implemented (Wi-Fi, WWAN) | Unit tests cover Wi-Fi and WWAN descriptions; no such adapter on this PC |
| Name, status, addresses shown | Validated | UI screenshot |
| Select the interface for streaming | Validated | UI and persisted setting |
| See whether it can reach the destination | Validated | Test connection: Ethernet OK in 104 ms; Ethernet 4 failure explained |
| Diagnostics for unexpected route or no connection | Validated | Default-route comparison shown; mismatch check in the connector; no-internet explanation |
| Retry after adapter disconnection or change | Implemented | Missing or down adapter retried within budget (integration test); real unplug while live is a field test |
| DNS handled correctly | Validated | Family filtering; DNS through the adapter (integration test); DNS packet unit tests |
| IPv4 and IPv6 | Implemented | Both supported in binding and DNS; this PC has no IPv6 connectivity to test |
| Changing addresses | Implemented | Adapter re-read on every attempt |
| Detect unavailable interfaces | Validated | Missing, down and no-gateway tests on real adapters |
| No silent fallback | Validated | Integration test: missing adapter retried twice then Failed, never Live, though the server was reachable by default route |
| Explain when routing prevents it | Validated | Local-only and VPN hint; route mismatch error |
| No global routing changes; no needless admin | Validated | Socket options only; app runs as standard user |
| Outgoing stream proven on the intended interface | Validated (Ethernet); Blocked (phone: no internet during tests) | Bound connect to live.twitch.tv left from the Ethernet address while the default route was the VPN; full stream through the selected adapter verified by local endpoint |

## Files

**Added**
- `src/Linkast.Networking/`: NetworkAdapters, InterfaceBinding, BoundDnsResolver, OutboundConnector, NetworkProbe
- `src/Linkast.App/ViewModels/NetworkOption.cs`
- `tests/Linkast.Networking.Tests/` (new project), `tests/Linkast.IntegrationTests/NetworkIntegrationTests.cs`
- `docs/adr/0006-adapter-binding.md`, this report

**Changed**
- RelayOutputTransport: uses OutboundConnector; adapter selection supported
- `Linkast.Networking` target framework is now `net10.0-windows10.0.19041.0`, for Windows' connectivity API
- MainViewModel and MainWindow: network card, Test connection, persistence, verification text
- StreamSettings: `NetworkInterfaceId`, `NetworkInterfaceName`
- StreamingIntegrationTests: the speed sanity bound is widened, because the test pattern isn't a real-time source

**New dependencies:** none (System.Net and WinRT `Windows.Networking.Connectivity`, both part of the platform).

## Tests

| Suite | Result |
|---|---|
| Release build | 0 warnings |
| Networking unit tests | 27 pass: classification of this PC's real adapter names, pseudo-adapter filtering, `IP_UNICAST_IF` byte order, DNS query build and answer parse (compression, CNAME, NXDOMAIN, truncation), adapter-resolution errors |
| Core | 67 pass |
| Engine | 34 pass |
| Integration | 12 pass, 4 opt-in skipped |

External tests (opt-in, `LINKAST_NETWORK_EXTERNAL=1`), run on this PC:

| Test | Result |
|---|---|
| Bound connect to live.twitch.tv through Ethernet | From the Ethernet address, 97–106 ms, while Windows' default was the VPN |
| Bound connect through Ethernet 4 | Failed with `network.interface.no_internet` (Windows: local only). Matches curl and ping: no internet through the phone at test time. |
| DNS through an adapter's own server | Resolved live.twitch.tv |
| Public IP through Ethernet and through the VPN | The same public address for both, so the VPN is a split tunnel. Distinct IPs are only expected for a mobile or tethered adapter, which was offline. |

## Manual UI test

| Test | Result |
|---|---|
| Adapter list | Ethernet, Ethernet 4 (USB phone / modem), the VPN, WSL (no internet route), disconnected spares; no pseudo-adapters |
| Test connection on Ethernet | "Connected to live.twitch.tv (35.55.18.18:1935) through 'Ethernet' from <LAN address>:12698 in 104 ms. Without a choice, Windows would use '<VPN adapter>'." |
| Test connection on Ethernet 4 | "'Ethernet 4' could not reach the server, and Windows reports this network has no internet access (local network only). Check that mobile data and tethering are switched on. A VPN can also block traffic on other networks." |

## Known limitations

- **Phone path unconfirmed.** A real stream over the internet through a phone or 5G modem awaits a tethering connection that has internet; see field test C1.
- **DNS fallback only.** The first DNS lookup uses Windows' resolver, which may go out over the default network. The adapter's own DNS is used only when that fails or gives unusable families.
- **VPN filtering.** A VPN with a kill switch, or firewall filtering, can block other adapters. Linkast reports it but can't override it.
- **Switching adapters while live.** Changing the network means stopping and starting the stream. Seamless failover is a later feature.
- **Game routing.** The game's own traffic is not moved; that remains a separate, later feature.

## Reproduce

```powershell
docker compose -f tools/test-ingest/docker-compose.yml up -d
dotnet test Linkast.slnx -c Release
$env:LINKAST_NETWORK_EXTERNAL = "1"   # also contacts live.twitch.tv, api.ipify.org and the adapters' DNS servers
dotnet test tests/Linkast.IntegrationTests --filter "FullyQualifiedName~NetworkIntegration"
```

## Recommended next phase

**P3: background effects and GPU processing**, as agreed. Before or alongside it, the maintainer should run field test C1 once the phone tethering has internet access.

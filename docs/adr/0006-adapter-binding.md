# ADR 0006: Choosing the stream's network by binding the relay socket

**Status:** Accepted (Phase 2)

## Context

The core scenario: a user plays over Ethernet but wants the stream to go out over a USB 5G modem or a tethered phone. Requirement 4.6 rules out the obvious shortcuts:

- No unnecessary changes to global routing.
- Admin rights only when unavoidable.
- No silent fallback to another network.
- The connection actually used must be verified; a UI selection is not proof.

## Options considered

1. **Change the routing table** (add a host route to the ingest via the modem's gateway). Rejected: it needs admin rights, it affects every app sending to that address, and it leaves state behind after a crash.
2. **Per-process routing with a WFP driver.** Rejected for now: kernel components are out of MVP scope.
3. **Bind only the stream's own socket to the adapter.** Accepted.

## Decision

The relay (ADR 0001) already owns the outgoing connection. For a selected adapter it:

1. Re-reads the adapter on every attempt, because addresses change. It fails with a retryable error if the adapter is missing, down, or has no address or gateway.
2. Resolves the server with Windows' resolver and keeps only address families the adapter can reach. If nothing usable comes back, it queries the adapter's own DNS servers with a UDP socket bound to the adapter (`BoundDnsResolver`).
3. Creates the TCP socket with `IP_UNICAST_IF` set to the adapter's interface index, and binds it to the adapter's address. Windows' strong host model then sends it out that interface. No routes are added and no admin rights are needed.
4. After connecting, checks that the socket's real local endpoint belongs to the selected adapter. If not, it fails with `network.route.mismatch` instead of streaming over the wrong network.
5. Reports which adapter carried the stream in telemetry ("verified: your chosen network").

Windows' own connectivity verdict per adapter (Internet / Local only / Limited) is shown in the UI and used to explain failures. It is advisory only: the real connection attempt decides.

## Consequences

- Only Linkast's stream uses the chosen adapter. Games and other apps keep the default route, which is exactly the core scenario.
- A missing or failing adapter makes the stream retry within the reconnect budget, then fail clearly. It never moves to another network.
- A VPN or firewall that filters traffic on other adapters can still block the chosen one. Linkast reports this as no internet or a timeout on that adapter, with a hint about VPNs. It does not try to work around it.
- Routing the game itself through a different adapter remains a separate, later feature.

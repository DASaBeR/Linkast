# ADR 0001: Output through a local relay

**Status:** Accepted (Milestone 1)

## Context

Linkast's main differentiator is letting the user choose, and verify, the network interface their stream uses. Requirement 4.6 says selecting an adapter in the UI is not proof; the actual outgoing connection must be validated.

FFmpeg's `tcp` protocol has a `local_addr` option. Whether it passes through the RTMP and TLS layers is not documented or verified. Even if it did, Linkast could not observe the resulting socket: its local endpoint, bytes sent, or why it failed.

## Decision

FFmpeg publishes plain RTMP to a single-use listener on `127.0.0.1`. Linkast's `RelayOutputTransport` opens the real outbound connection and copies bytes both ways. For `rtmps://` the relay performs TLS with standard certificate validation. FFmpeg's `-rtmp_tcurl` is set to the real server URL so the server sees its own address.

## Consequences

- Linkast measures real throughput, knows the exact local endpoint and interface, and classifies DNS, refusal, timeout, TLS and server-close errors itself.
- Milestone 3 binding is a change in one place: the relay's `Socket.Bind` plus `IP_UNICAST_IF`.
- RTMPS works even when FFmpeg is built without a TLS library.
- Cost: one extra in-process copy, which is negligible at streaming bitrates. Verified at 1080p60 and 6 Mbps.
- The stream key still reaches FFmpeg's command line. Making the relay RTMP-aware, so that it performs the publish itself, is the path to removing it. That is tracked in risks.md.
- `DirectOutputTransport` remains as a diagnostic fallback.

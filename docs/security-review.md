# Security review (Phase 5, beta)

Scope: Linkast 0.2.0-beta.1 as installed by the beta installer. Linkast is local-only: no accounts, servers, telemetry or remote control. The assets worth protecting are:

- the **stream key** (whoever has it can stream to your channel);
- the **camera and microphone** (privacy);
- the **integrity of what is installed and updated**.

## Summary

| Area | Finding | Status |
|---|---|---|
| Stream key on FFmpeg's command line | The key was part of FFmpeg's arguments, visible in Task Manager's *Command line* column. That is easy to show on stream by accident, and any process of the same user can read it. | **Fixed.** FFmpeg gets a random placeholder of the same length; Linkast's relay writes the real key into the RTMP publish commands ([RtmpKeyRewriter](../src/Linkast.Networking/RtmpKeyRewriter.cs)). Integration test: the key is absent from every `ffmpeg.exe` command line and the server still receives it. |
| Stream key at rest | DPAPI (CurrentUser) in `%LOCALAPPDATA%\Linkast\secrets.dat`, never in `settings.json`. | OK. Protects against other Windows users and offline disk access, not against malware running as the same user (inherent to DPAPI). |
| Stream key in logs, diagnostics, errors | Redacted at the source and again at the log sink; *Copy diagnostics* redacts too. FFmpeg now never sees the key, so its own messages can't contain it. | OK (tests: diagnostics and command line). |
| Stream key in the UI | Masked PasswordBox; *Show* is explicit. | OK. |
| Local relay (127.0.0.1, random port) | Accepts one connection per stream, then closes the listener; the peer must be loopback. A local process of the same user could race FFmpeg to connect, but it would need the random placeholder from FFmpeg's command line, and such a process could read the DPAPI key anyway. | Accepted risk. |
| FFmpeg live-command port (zmq, 127.0.0.1) | Any local process could send overlay commands, for example moving or hiding a camera in the running stream. It can't read anything back. | Accepted risk (low impact); noted for a later per-session token. |
| Camera and audio named pipes | Created with `PipeOptions.CurrentUserOnly`, so other users can't connect. | OK. |
| Camera privacy | A camera-active indicator, one-click camera off that releases the device, frames never logged or stored, and all AI processing local. | OK. |
| Outbound TLS (RTMPS) | `SslStream` with Windows' default certificate validation; no validation override anywhere. | OK. |
| Network binding | The chosen adapter is verified on every connection, with no silent fallback; Windows network settings are never changed and no admin rights are needed. | OK (Phase 2). |
| Process launch | FFmpeg is started by absolute path with an argument list (no shell) and in a kill-on-close Job Object. Lookup order: `LINKAST_FFMPEG`, the bundled copy, then `PATH` as a development fallback. | OK. Setting a user environment variable already implies code execution as that user. |
| Third-party binaries | The FFmpeg build and the AI model are pinned by SHA-256 at build time. The model is verified again when it loads; a damaged or substituted model is refused. | OK. FFmpeg isn't re-verified at runtime; the per-user install folder is writable only by that user. |
| Updates | Velopack checks the GitHub Releases feed over HTTPS **once at start-up and when the user presses the button**. It installs only on request, never while live. The package hash in the feed is checked before applying. | OK, but see below. |
| Update trust | Update integrity rests on the GitHub account and repository: a compromised account could publish a malicious update to anyone who presses *Update now*. Releases are created as **drafts** and published by a maintainer. Builds are **not code-signed** yet. | Open. Before 1.0: require 2FA and protected tags on the repository, and sign the installer and executables. |
| Settings files | Atomic write, corrupt files kept aside, values clamped on load; no secrets inside. | OK. |
| Uninstall | Removes the app and shortcuts; keeps `%LOCALAPPDATA%\Linkast` (settings, encrypted key, logs). The beta notes say so and how to remove it. | OK. A "remove my data" option could come later. |

## Linkast Camera (Phase 6)

| Area | Finding | Status |
|---|---|---|
| Code loaded into a system service | Frame Server (Local Service) loads `Linkast.VirtualCamera.dll`. If the DLL lived in a user-writable folder, that user could run code as Local Service. | Mitigated. The helper copies it to `%ProgramFiles%\Linkast\Virtual Camera` (writable only by administrators) and registers that copy. The per-user install folder is never registered. |
| Elevation | `LinkastCameraSetup.exe` runs elevated (manifest plus UAC), only when the user presses Set up, Repair or Remove. It only copies the DLL from the Linkast folder it was given, registers it and creates or removes the camera. Linkast itself never runs elevated. | Accepted. The source folder is the per-user install, so a user who controls it can only install a DLL they could already run. The UAC prompt names the helper. |
| Shared memory between session 0 and the user | `Global\LinkastVirtualCamera.v1`. DACL: full access for SYSTEM, Local Service and Administrators; read/write for interactive users only; Medium integrity label. | OK. Content is pictures only. Any interactive user can write frames into the Linkast Camera or read what it shows; the same holds for any camera on a shared PC. |
| Parsing in the service | The source reads only fixed-size header fields and copies fixed-size frames. A garbled or foreign header shows the placeholder; sizes come from compile-time constants, never from the section. | OK |
| Stability of the service | No exceptions cross COM; a missing section, WIC failure or allocator problem falls back to the placeholder or a navy frame; static CRT. | OK. Field tests G1–G3 cover real apps. |
| Removal | Unregisters, removes the device and deletes the files. Files still loaded are deleted at the next restart. | OK |

## Recommendations before 1.0

1. Code-sign the installer and `Linkast.exe` (for example an OV certificate or Azure Trusted Signing). Velopack supports both (`--signParams` / `--azureTrustedSignFile`).
2. Protect the release path: 2FA for maintainers, protected `v*` tags, and draft-only automated releases (already done).
3. Add a per-session token to FFmpeg's command channel if it ever does more than move layers.
4. Re-run this review after any change to the relay, secrets, update or process-launch code.

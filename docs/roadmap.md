# Roadmap

Linkast is local-only and open source: no backend, website, accounts or telemetry. Work is delivered phase by phase. Each phase ends with a written report, and the Webcam Studio requirements are tracked in [webcam-checklist.md](webcam-checklist.md).

| Phase | Scope | Status |
|---|---|---|
| Milestone 1 | Streaming prototype | Done (manual checks open, see below) |
| P0 | Webcam Studio assessment and design | Done ([webcam-architecture.md](webcam-architecture.md)) |
| P1 | Webcam Studio core | Done ([report](reports/webcam-phase-1.md)); real-webcam checks open |
| P2 | Network adapter selection | Done ([report](reports/network-phase-2.md)); phone-over-internet field test open |
| P3 | Webcam background effects and GPU processing | Done ([report](reports/webcam-phase-3.md)); real-person field tests open |
| P4 | Webcam advanced features | Done ([report](reports/webcam-phase-4.md)); real-person and multi-camera field tests open |
| P5 | Installer and beta | Done ([report](reports/beta-phase-5.md)); first GitHub release and beta-tester field tests open |
| P6 | Webcam ecosystem: virtual camera, phone | Done ([report](reports/webcam-phase-6.md)); video-call app and phone field tests open |

## Milestone 1: streaming prototype (done)

Capture, audio, encoder probing, relay output, measured health, reconnect, classified errors, redaction, CI, and a 30-minute soak test. See [capture-compatibility.md](capture-compatibility.md).

Still open, and needing the maintainer:
- [ ] A 30-minute RTMPS session to a real platform.
- [ ] A pulled-cable reconnect test.
- [ ] The game capture matrix.
- [ ] An A/V clap test.

## P1: Webcam Studio core

- Camera discovery, hot-plug, selection, format choice, live preview, camera on/off, clear errors.
- Software image controls (brightness, contrast, saturation, sharpen, denoise) and hardware controls where supported.
- Crop and zoom; rectangle, rounded and circle shapes; border.
- The camera layer composited over gameplay, or camera-only, in the real stream. Live move and resize without a restart. A camera failure never ends the stream.
- A scene placement editor with drag, resize and snap.
- Versioned settings persistence, camera presets (Gaming, Just Chatting, Professional, Low Light, Minimal, custom), and the stream key saved encrypted with DPAPI.

## P2: Network adapter selection

- An adapter list showing type, status and addresses.
- The relay binds to the chosen adapter (`IP_UNICAST_IF`) and verifies the actual local endpoint, with no silent fallback.
- DNS and IPv4/IPv6 handling per adapter, adapter-loss recovery, and documented tests with Ethernet, Wi-Fi and USB 5G.

## P3: Background effects and GPU processing

- Local person segmentation (ONNX Runtime; DirectML GPU with CPU fallback), model chosen by benchmark and license.
- Blur, removal and static image backgrounds, with edge refinement and temporal smoothing.
- A GPU frame path, with the active processing mode shown.

## P4: Advanced webcam features

- Auto-framing with smoothing (Windows face detection, on this PC only).
- Animated borders (pulse, colour cycle) and glow.
- Audio-reactive effects using the existing microphone capture (one capture session per device).
- Up to 4 camera layers; the same camera can be used twice and is opened once.
- Landscape and vertical (1080×1920) layouts with their own placements; live switching where possible, confirmed restart otherwise.

## P5: Installer and beta

- A per-user Velopack installer (no admin rights) with a portable copy. Updates come from GitHub Releases: one check at start and a button; a new version shows as text and installs only on request.
- The FFmpeg 9.0.1 essentials build, pinned by hash, with the corresponding source assembled for every release ([licensing.md](licensing.md)).
- A security review ([security-review.md](security-review.md)); the stream key is no longer on FFmpeg's command line.
- A compatibility matrix ([compatibility-matrix.md](compatibility-matrix.md)), measurements of the installed build, and a draft-release workflow ([releasing.md](releasing.md)).
- Still open:
  - code signing;
  - a self-built, reduced FFmpeg, which closes the remaining source gaps and shrinks the download.

## P6: Webcam ecosystem

- **Linkast Camera** for Zoom, Teams, Discord, Meet and OBS (Windows 11). It is an optional add-on installed with one administrator prompt, stays listed when Linkast is closed, and shows the chosen camera with its look ([ADR 0009](adr/0009-linkast-camera.md)).
- **Phone as webcam** through phone apps and Windows 11's connected camera ([phone-as-webcam.md](phone-as-webcam.md)).
- New brand mark, and dark mode by default.

## Next

- A self-built, reduced FFmpeg, which closes the remaining source gaps and shrinks the download.
- Code signing.
- A GPU path for camera layers and the Linkast Camera.
- The first public beta release, plus the field tests.

## Later, each needing its own assessment

- Multiple simultaneous outputs.
- Per-application audio capture.
- Adapter failover during a live stream.
- Per-application network routing for games.
- SRT output.

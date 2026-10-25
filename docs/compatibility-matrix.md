# Compatibility matrix (beta)

What has been verified, on which hardware, and what Linkast does when something isn't available. "Verified" means tested on real hardware through the real pipeline. "Expected" means the same code path is used, but nobody has tested it on that hardware yet; please report it (see [field-test-plan.md](field-test-plan.md)).

Development machine:
- Windows 11 Pro build 26200;
- Intel Core i5-12400F (no integrated GPU);
- NVIDIA GeForce RTX 3070;
- Iriun Webcam (phone as webcam);
- Realtek audio;
- bundled FFmpeg 9.0.1 essentials (gyan.dev).

## Operating system

| Windows | Status | Notes |
|---|---|---|
| Windows 11 24H2/25H2 | Verified (build 26200) | Everything in this matrix |
| Windows 11 21H2–23H2 | Expected | |
| Windows 10 22H2 | Expected | Background effects may fall back to the CPU if the system DirectML is too old (field test D6). |
| Windows 10 before 1903 | Not supported | The app targets 10.0.19041 APIs |
| Windows on ARM | Not supported | x64 build only |

## Video encoders

Linkast tests every encoder when it starts and offers only those that work. Hardware is preferred; x264 is always available as a fallback.

| Encoder | Hardware | Status |
|---|---|---|
| NVENC (h264_nvenc) | NVIDIA GPUs with NVENC and a current driver | **Verified** (RTX 3070): 1080p60 landscape and vertical, 30-minute soaks |
| AMF (h264_amf) | AMD Radeon | Expected; probed at start-up. Not available on the dev PC. |
| Quick Sync (h264_qsv) | Intel integrated or Arc | Expected; probed at start-up. Not available on the dev PC (CPU without an iGPU). |
| Media Foundation (h264_mf) | Any GPU with a Windows H.264 encoder | Verified working. Constrained Baseline only, so the last resort among the hardware options. |
| x264 (libx264) | CPU | Verified. Needs a fast CPU for 1080p60; Linkast warns when encoding falls behind. |

## Capture

| Source | Method | Status |
|---|---|---|
| Display | Windows Graphics Capture | Verified |
| Display | Desktop Duplication | Verified (option under *Advanced capture*) |
| Window or game | Windows Graphics Capture | Verified for normal and borderless windows. **Exclusive-fullscreen games** may capture black; use borderless (field test A3). |
| Multiple monitors, mixed DPI | | Expected |
| Hybrid-graphics laptops | | Expected. Capture runs on the GPU that drives the display. |

## Webcam and effects

| Feature | Status | Notes |
|---|---|---|
| USB and built-in webcams | Expected | Same Windows camera path as the verified phone webcam (field test B1) |
| Phone as webcam (Iriun) | Verified | DroidCam and Camo are expected to work the same way |
| Background effects on an NVIDIA GPU | Verified (DirectML) | 8–12 ms per frame |
| Background effects on AMD or Intel GPUs | Expected (DirectML) | Field test D3 |
| Background effects on the CPU | Verified | 21–48 ms per frame on 3 threads |
| Auto-framing | Verified on a still portrait | Windows face detection; field test E1 for a real person |
| Two or more physical cameras | Expected | One camera used twice is verified (field test E3) |

## Linkast Camera (virtual camera)

| Item | Status |
|---|---|
| Windows 11 (build 22000+) | Verified on 26200: install, list (Media Foundation and DirectShow), frames, placeholder, removal |
| Windows 10 | Not supported; the Windows virtual camera API needs Windows 11 |
| Zoom, Teams, Discord, Meet, OBS | Expected (a standard Windows camera); field test G1 |

## Audio

| Feature | Status |
|---|---|
| System audio (WASAPI loopback) | Verified |
| Microphone (WASAPI) | Verified (Realtek) |
| USB and Bluetooth microphones | Expected |
| Voice-reactive effects sharing the microphone capture | Verified |

## Network

| Setup | Status |
|---|---|
| Ethernet | Verified |
| Stream over a USB-tethered phone while gaming on Ethernet | Binding verified. Streaming over the phone's mobile data is open (field test C1). |
| Wi-Fi, laptop SIM (WWAN) | Expected (C3, C4) |
| VPN present | Verified, without changing it; the kill-switch case is C5 |
| RTMP | Verified (MediaMTX) |
| RTMPS (TLS) | Implemented; a real platform test is A1 |

## Installer and updates

| Item | Status |
|---|---|
| Per-user install without admin rights, Desktop and Start-menu shortcuts | Verified |
| Update from a newer release when **Update now** is pressed | Verified with a local feed (0.2.0-beta.1 to beta.2, restart in 6 s) |
| Update from GitHub Releases | Expected; verified once the first release is published |
| Uninstall keeps settings and the encrypted key | Verified |
| SmartScreen | Warns on first run until builds are code-signed |

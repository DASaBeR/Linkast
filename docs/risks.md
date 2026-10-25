# Technical risks and open questions

Status as of Milestone 1. "Verified" means demonstrated on the development machine (Windows 11 build 26200, RTX 3070, FFmpeg 9.0.1).

## Capture

| Risk | Status | Mitigation / next step |
|---|---|---|
| Exclusive-fullscreen games capture as black with Windows Graphics Capture. | Open. Needs the game matrix in [capture-compatibility.md](capture-compatibility.md). | Offer Desktop Duplication for displays. Guide users to borderless mode. A hook-based game capture is out of scope without a separate anti-cheat assessment. |
| Desktop Duplication output index may not match Linkast's monitor index on multi-GPU or hybrid-graphics laptops. | Open. WGC uses the exact HMONITOR, so it is unaffected. | Enumerate DXGI outputs directly in Milestone 2. |
| GPU-side scaling (`scale_d3d11`) fails on this driver with E_INVALIDARG. | Verified failure. Linkast downloads frames to the CPU and scales there. | NVENC zero-copy at native resolution works. Use it when the output size equals the capture size. Planned optimization. |
| A minimized window stops producing frames and the stall detector reconnects in a loop. | Open. | Detect minimized windows and show a dedicated message instead of reconnecting. |
| HDR displays. | Untested. | Add to the compatibility matrix. |

## Audio

| Risk | Status | Mitigation |
|---|---|---|
| FFmpeg has no WASAPI loopback input. | Resolved. Linkast captures with NAudio and serves PCM over named pipes. | — |
| Loopback produces no data while nothing plays, which would stall FFmpeg. | Resolved and verified by integration test. | The writer pads silence after 60 ms without data. |
| Audio drifts against video over long streams. | Mitigated. An early build used wall-clock timestamps on pipes, which FFmpeg did not rebase; a regression test now guards this. | The writer locks each pipe to wall-clock rate (pads when behind, drops when more than 200 ms ahead). The A/V offset still needs measuring with a clap test over 30+ minutes. |
| An audio device is unplugged mid-stream. | Handled. The stream stops with an Audio error. | Consider continuing without that source. |

## Encoding

| Risk | Status | Mitigation |
|---|---|---|
| An encoder is listed by FFmpeg but fails at runtime. | Resolved. Verified: AMF and QSV are listed but fail on this NVIDIA machine, and are hidden. | Real-frame probe at startup. |
| NVENC session limits on consumer GPUs. | Handled by classification (`encoder.nvenc.session`). | — |
| Encoder overload during heavy games. | Surfaced through FFmpeg speed and FPS, shown as a warning. | Process priority is above normal. Measure CPU and GPU cost per preset in the compatibility matrix. |

## Network

| Risk | Status | Mitigation |
|---|---|---|
| Selecting an adapter must provably control the route. | Implemented in Phase 2 and validated on Ethernet versus the VPN default route. | Binding plus `IP_UNICAST_IF`, verified local endpoint, no fallback. Phone-over-internet is field test C1. |
| A VPN or firewall blocks traffic on non-VPN adapters (kill switch). | Observed as likely on the development PC: its phone tethering had no internet path. | Reported clearly with a VPN hint; Linkast never modifies VPN or firewall settings. |
| The first DNS lookup goes through Windows' resolver on the default network. | Accepted. | Falls back to the adapter's own DNS when that fails or gives unusable address families. |
| Stream key visible in FFmpeg's command line to same-user processes. | Accepted for now, documented in SECURITY.md. | Evaluate making the relay RTMP-aware: FFmpeg sends FLV, and the relay performs the RTMP publish with the real key. |
| RTMPS certificate problems behind corporate TLS inspection. | Untested. | The error is classified as `network.tls`. Never disable validation. |
| Another local process connects to the relay port before FFmpeg. | Low impact. That process never receives the key, because the key travels from FFmpeg to the server. | The relay accepts a single loopback connection only. Consider verifying the peer process ID. |
| Some ingests validate the tcUrl. | Handled. `-rtmp_tcurl` carries the real server URL. | Verify against Twitch, YouTube and Facebook in the real-service test. |

## Product and process

| Risk | Status | Mitigation |
|---|---|---|
| Profile and settings persistence. | Not built. Settings are lost on exit in Milestone 1. | Milestone 2: JSON profiles, with the key protected by DPAPI and stored separately. |
| Codec patent exposure (H.264, AAC) for distributors. | Open, same position as OBS. | Document in licensing.md. Seek legal review before any commercial redistribution. |

## Webcam Studio (Phase 4)

| Risk | Status | Mitigation |
|---|---|---|
| Several large camera layers at a high resolution: each layer is a raw BGRA pipe and a CPU render. | Measured in the [Phase 4 report](reports/webcam-phase-4.md) with two cameras, blur and animated effects at 1080p60. | Layers are capped at 30 fps and blended in YUV. A GPU render path for layers would remove most of the cost. |
| A live input drifting from the capture clock makes FFmpeg buffer without limit. | Fixed. Seen as FFmpeg growing to 9.6 GB in a 30-minute soak. | Camera pipes use wall-clock timestamps; the soak is repeated after any compositing change. |
| Auto-framing with several faces, profile views or poor light. | Open (field test E1). | Windows' detector picks the largest face. Framing holds still when no face is found. |

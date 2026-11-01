# ADR 0010: Direct3D 11 In-App Scene Compositor

**Status:** Accepted (supersedes [ADR 0002](0002-capture-inside-ffmpeg.md) and [ADR 0008](0008-layers-layouts-and-shared-inputs.md))

## Context

Linkast originally delegated live capture and scene composition to `ffmpeg.exe` via `-filter_complex` ([ADR 0002](0002-capture-inside-ffmpeg.md) and [ADR 0008](0008-layers-layouts-and-shared-inputs.md)). Each scene element (screen capture, multiple camera pipes, audio pipes, backgrounds) was wired as an input in an immutable FFmpeg filtergraph.

Real-world production logs revealed that delegating live composition to FFmpeg CLI creates structural failure modes:

1. **Fixed Command Line:** Switching games, changing windows, or reordering layers required terminating and restarting `ffmpeg.exe`. Ingest servers frequently reject rapid reconnects (`"Already publishing"`), cutting stream delivery.
2. **Pipeline Stalls:** FFmpeg's filtergraph blocks until all inputs emit frames. An idle or minimized window (e.g. background Chrome) caused 20-second hangs and consecutive `"could not find codec parameters"` failures.
3. **CPU Saturation:** Inter-process BGRA pipes, software scaling, and CPU overlay filters saturated multiple CPU cores (e.g. heavy gameplay running at 0.7× real-time).
4. **Coupled Backpressure:** When upload bandwidth dropped, output buffers backed up and stalled the encoder, starving the capture demuxer and reducing game capture from 60 fps to 17 fps.

To resolve these root causes, live composition and capture must move inside Linkast on the GPU.

---

## Decisions

| Concern | Decision | Why |
|---|---|---|
| **Compositor Engine** | **Direct3D 11 (`Vortice.Direct3D11`).** Scene composition, layer blending, scaling, and window capture execute directly on the GPU. | Native Windows API with zero redistributable licensing hurdles. D3D11 is already present in Linkast via `WebOverlayRenderer`. |
| **Threading & Context Ownership** | **Single primary D3D11 device, dedicated render thread.** One thread exclusively owns the `ID3D11DeviceContext` (immediate context). Cross-thread resource creation is protected via `ID3D11Multithread`. | Direct3D 11 immediate contexts are not thread-safe. Avoids race conditions between WPF, capture callbacks, and composition. |
| **Frame Scheduling & Pacing** | **Monotonic Media Output Scheduler.** Render scheduling is driven by a monotonic clock (`QueryPerformanceCounter`) and decoupled from source frame arrival. | A 30 FPS camera or an idle game window does not stall the compositor; Linkast reuses the latest valid texture from a `SourceFrameCache`. |
| **Source Lifecycle Abstraction** | **`ISceneSource` interface.** Unified lifecycle (`Available`, `Capturing`, `TemporarilyUnavailable`, `Lost`) with asynchronous acquisition and state-change events. | Decouples source-specific acquisition logic (WGC, MediaCapture, WebView2) from rendering shaders. |
| **Color Conversion** | **GPU-native NV12 shader.** RGBA composited render target is converted to NV12 (BT.709 limited range) via a GPU compute or pixel shader. | Zero CPU readback during compositing. Full picture remains on GPU before output streaming. |
| **FFmpeg Ingest Pipeline** | **Single Video Pipe + Single Audio Pipe.** FFmpeg is started with 2 static inputs: `\\.\pipe\linkast-video` (NV12 60fps) and `\\.\pipe\linkast-audio` (48kHz stereo s16le PCM). | Eliminates `-filter_complex`, ZMQ command sockets, layer teleportation hacks, and process restart storms. Scene switches are 0 ms GPU texture swaps. |
| **A/V Synchronization** | **Master Audio Sample Reference.** Audio samples establish the continuous time base ($t = samples / 48000$); the video scheduler aligns frame output against this timeline. | Prevents raw pipe timestamp drift between audio and video streams. |
| **Device-Loss Recovery** | **Orderly GPU recreation pipeline.** Catch `DXGI_ERROR_DEVICE_REMOVED` / `RESET`, recreate device, frame pools, and render targets. | The encoder process remains alive; fallback frames are pumped to the pipe during the ~500 ms device recreation. |

---

## Consequences

- **Zero stream restarts on scene changes:** Switching scenes, adding/removing layers, toggling cameras, or changing window captures never restarts FFmpeg or reconnects to the ingest server.
- **Zero stalls on idle/minimized windows:** Windows that stop painting do not stall the pipeline; the compositor repeats the last frame or renders background/camera layers smoothly at 60 fps.
- **CPU overhead drops dramatically:** All scaling, aspect-ratio fitting, and alpha blending happen in GPU shaders.
- **Game capture isolated from network lag:** Capture and composition run on their own clock; network backpressure affects only the output transmission queue.
- **Code simplification:** Eliminates dynamic `-filter_complex` building, ZMQ command channels, and multi-pipe orchestration in `FfmpegStreamEngine.cs`, reducing its size from 1,233 lines to ~250 lines.

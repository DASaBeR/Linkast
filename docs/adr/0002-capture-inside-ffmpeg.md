# ADR 0002: Video capture inside FFmpeg using Windows capture APIs

**Status:** Accepted (Milestone 1)

## Context

Requirement 4.2 asks for Windows-native capture (Windows Graphics Capture or Desktop Duplication), separated from the engine behind an interface, and with no injection-based game capture.

The FFmpeg 9 build in use includes two lavfi source filters:

- `gfxcapture`: Windows Graphics Capture for a monitor (`hmonitor`, `monitor_idx`) or a window (`hwnd`), with cursor control.
- `ddagrab`: Desktop Duplication for a monitor.

## Decision

Capture runs inside the FFmpeg process through these filters. Linkast describes the source with `VideoSourceSpec` records (`DisplaySource`, `WindowSource`, `TestPatternSource`). `FfmpegArgumentBuilder` maps them to filter strings containing only numbers and constants. Window titles never reach FFmpeg; only the HWND does.

Pipeline: capture as D3D11 frames, then `hwdownload`, `fps` to make the rate constant, scale and pad, then encode.

## Verified (spike, 3-second captures at 1280x720 unless stated)

| Path | Result |
|---|---|
| ddagrab, CPU scale, x264 | OK, 59 fps |
| gfxcapture, CPU scale, x264 | OK, but only about 32 fps without `fps`. WGC emits frames on change, so `fps` is required. |
| gfxcapture with `fps`, CPU scale, x264 or NVENC | OK, 60 fps |
| ddagrab, NVENC, native 1080p, zero-copy | OK |
| ddagrab or gfxcapture with `scale_d3d11` | **Fails** (texture creation E_INVALIDARG) on driver 32.0.16.1088 |

## Consequences

- No capture code to maintain in Linkast. If a capture integration outside FFmpeg is ever needed, it can feed FFmpeg over a pipe, as audio does, without touching the UI.
- Capture compatibility depends on the FFmpeg version, so the release must pin it.
- CPU scaling costs some CPU at high resolutions. A zero-copy NVENC path for the no-scale case is planned.

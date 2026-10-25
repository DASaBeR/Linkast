# ADR 0005: Linkast owns the camera; FFmpeg composites the camera layer

**Status:** Accepted (Webcam Phase 1)

## Context

The Webcam Studio needs:

- a live preview before streaming,
- image adjustments and hardware controls,
- shapes and borders,
- later, AI background effects and face tracking,
- changes applied without restarting the stream,
- the preview and the output must match,
- and the same physical camera must not be opened twice.

Linkast has no compositor; FFmpeg's filter graph is the composition step.

## Options considered

1. **FFmpeg opens the camera (DirectShow) and applies effects with filters.** Rejected:
   - A second open is needed for the preview, and most webcams allow one open at a time.
   - Hardware controls can't be changed live.
   - AI effects can't run inside FFmpeg with ONNX.
   - The preview would not show the effects.
2. **Linkast builds its own full GPU compositor and sends finished frames to FFmpeg.** Rejected for now. It would move gameplay capture out of FFmpeg and replace working, verified infrastructure. It can be revisited if multi-scene compositing outgrows FFmpeg.
3. **Linkast owns the camera and renders the camera layer; FFmpeg overlays it.** Accepted.

## Decision

- **Capture.** WinRT `MediaCapture` with `MediaFrameReader`, in shared-read-only mode when another app already controls the camera.
- **Rendering.** Linkast renders the processed, shaped, bordered camera layer as BGRA with alpha and serves it over a named pipe, as it already does for audio.
- **Composition.** FFmpeg scales and overlays the layer onto the base capture.
- **Live placement.** Position and size changes go to FFmpeg's `zmq` command filter on a random loopback port. Everything else changes inside Linkast without touching FFmpeg.

## Consequences

- The preview and the stream come from the same renderer, so they match.
- The camera opens once, and the preview, stream and future virtual camera share it.
- A camera fault does not end the stream; the layer becomes transparent.
- Cost: frames cross a pipe as raw BGRA, about 110 MB/s at 1280x720 and 30 fps, and processing starts on the CPU. Both are measured and reported, and a GPU path is planned for Phase 3.
- The zmq port is local-only. It is randomized per stream, and only filter commands are possible through it.

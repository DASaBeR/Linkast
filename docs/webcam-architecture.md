# Webcam Studio: architecture (Phase 0)

This is the Phase 0 assessment for the Webcam Studio brief. Requirement status is tracked in [webcam-checklist.md](webcam-checklist.md), and the phase order is in [roadmap.md](roadmap.md).

## What existed before Webcam Studio

- **Composition.** FFmpeg is the only compositor. One `-filter_complex` takes input 0 (`gfxcapture`, `ddagrab` or `testsrc2`) plus audio inputs 1..N and produces `[vout]` and `[aout]`. There was no scene graph, GPU compositor or preview renderer.
- **Live media over pipes.** Linkast already captures live media itself and serves it to FFmpeg over named pipes (WASAPI audio), with the pipes paced to wall-clock time. The camera reuses this proven pattern.
- **Missing pieces.** There was no camera code, no persistence and no virtual camera.
- **Patterns to reuse.** Generic Host DI, `IAsyncDisposable` with cancellation tokens throughout, the Job Object for FFmpeg, classified `StreamError`s, and redaction.
- **Hardware on the development machine.** The only camera is "Iriun Webcam", a phone-as-webcam app. It offers yuyv422 and bgr24 from 640x360 to 3840x2160 at 30 to 60 fps. There is no physical USB or integrated webcam.

## Decision: Linkast owns the camera, FFmpeg composites

```
WinRT MediaCapture ─► CameraSession (one per physical device, ref-counted)
      │ BGRA frames             ├─► Preview (WriteableBitmap on the UI thread)
      ▼                         └─► CameraLayerRenderer: adjust → crop/zoom → shape + border → BGRA layer
  VideoDeviceController (hardware controls)                │
                                                           ▼
                                  CameraPipeWriter (latest frame wins, paced to output fps)
                                                           │  \\.\pipe\linkast-cam-<guid>  (rawvideo bgra)
ffmpeg:  [0:v] base capture ──┐                            ▼
         [k:v] camera layer ──┴─ scale@camN ─► overlay@camN (x, y, w, h changeable live over zmq) ─► encoder
```

See [ADR 0005](adr/0005-camera-owned-by-linkast.md) for the reasoning. In short:

- **One device session** feeds the preview, the stream and a future virtual camera. The camera is never opened twice, and the preview equals the output.
- **Effects run in Linkast,** where AI segmentation (Phase 3) and face tracking (Phase 4) must run anyway.
- **Live changes without restarting the stream.**
  - Image, shape, crop, border and camera-off changes take effect in Linkast immediately.
  - Position and size changes reach FFmpeg as runtime commands.
  - Only base capture, output resolution and encoder changes need a restart.
- **A camera failure is never fatal to the stream.** The layer turns transparent and recovers when the device returns.

## Component responsibilities

| Concern | Where |
|---|---|
| Discovery and hot-plug | `Linkast.Platform.Windows/Camera` (`DeviceWatcher`) |
| Capture, format negotiation, hardware controls | `Linkast.Platform.Windows/Camera` (`MediaCapture`, `MediaFrameReader`, `VideoDeviceController`) |
| Frame ownership and conversion | The capture session copies each frame into a pooled BGRA buffer. Consumers read the latest frame and never hold it across frames. |
| Camera-level processing | `CameraLayerRenderer`: pure CPU/SIMD code in Phase 1, with a GPU path in Phase 3 |
| Scene model and validation | `Linkast.Core/Scenes`: normalized placement, shape, border |
| Composition | FFmpeg `overlay` |
| Preview | WPF `WriteableBitmap` fed by the same renderer as the output |
| Persistence | `Linkast.Core/Settings` (versioned model) plus a JSON store and DPAPI secrets in the app |

## Threading and ownership rules

- Capture callbacks run on WinRT threads. They convert the frame, publish it to a single-slot "latest frame" holder, and return immediately. Stale frames are overwritten, never queued.
- The renderer runs on one dedicated worker per layer, driven by new frames. Its output buffer is double-buffered and reused.
- The pipe writer ticks at the output frame rate. It sends the newest rendered layer, repeats the previous one if nothing new arrived, and sends a transparent frame when the camera is off or has failed.
- The UI preview is throttled to at most 30 updates per second and copies from the renderer's output buffer on the dispatcher.
- Every component is `IAsyncDisposable`. Stopping the preview and the stream drops the session's reference count to zero, which closes `MediaCapture`.

## Privacy

- Frames never leave the PC except inside the user's own stream.
- Frames are never logged or written to disk.
- The UI always shows whether the camera is active, and camera-off immediately stops capture.
- Windows camera privacy denials are reported as such.

## Dependencies

| Need | Choice | License |
|---|---|---|
| Camera capture and controls | WinRT `Windows.Media.Capture` (OS) | None. Needs the Windows 10 1903+ TFM. |
| Encrypted stream key | `System.Security.Cryptography.ProtectedData` | MIT |
| Live FFmpeg commands | ZeroMQ REQ client (NetMQ, pure C#) | LGPL-3.0, compatible with GPL-3.0 |
| Segmentation (Phase 3) | ONNX Runtime 1.24.4 with Windows' own DirectML; RobustVideoMatting MobileNetV3 model ([ADR 0007](adr/0007-local-segmentation.md)) | MIT runtime; GPL-3.0 model; DirectML is an OS component (not shipped) |
| Auto-framing (Phase 4) | `Windows.Media.FaceAnalysis.FaceDetector` (OS) on a 480-px grey copy, about 10 Hz ([ADR 0008](adr/0008-layers-layouts-and-shared-inputs.md)) | None |
| Phase 6 virtual camera | Windows 11 `MFCreateVirtualCamera` + native media source | Windows 11 only |

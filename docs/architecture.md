# Linkast architecture

Linkast is a single Windows desktop application. Everything runs on the user's PC: there is no backend, account system or telemetry service. The stream travels directly from the PC to the destination the user configures.

## Components

```
┌──────────────────────────── Linkast.exe (WPF, .NET 10) ─────────────────────────────┐
│                                                                                       │
│  Linkast.App ── MainViewModel ──► IStreamEngine (Linkast.Core)                        │
│                                        │                                              │
│                         FfmpegStreamEngine (Linkast.Engine.FFmpeg)                    │
│               ┌────────────────┬───────┴─────────┬───────────────────────┐            │
│               ▼                ▼                 ▼                       ▼            │
│      IAudioInputProvider  FfmpegArgumentBuilder  FfmpegProcess    IOutputTransport    │
│      (Platform.Windows)   (pure)                 (Job Object)     (Networking)        │
│        WASAPI capture                                               RelayOutputTransport
│          │  named pipes                   │ stdin 'q' / stdout progress / stderr      │
└──────────┼────────────────────────────────┼────────────────────────────┼──────────────┘
           ▼                                ▼                            ▼
    \\.\pipe\linkast-*  ──────────►   ffmpeg.exe  ── RTMP ──► 127.0.0.1:<port> relay ── RTMP/RTMPS ──► ingest server
                                (gfxcapture/ddagrab, encode, FLV)            (owns the real outbound socket)
```

| Project | Responsibility | Depends on |
|---|---|---|
| `Linkast.Core` | Session model, `IStreamEngine`, state machine, reconnect policy, validation, redaction, telemetry records, audio and transport abstractions. | nothing |
| `Linkast.Engine.FFmpeg` | Locating ffmpeg.exe, encoder probing, argument building, process supervision, progress parsing, error classification, the engine itself. | Core |
| `Linkast.Networking` | `RelayOutputTransport` (the local relay) and `DirectOutputTransport` (diagnostic fallback). | Core |
| `Linkast.Platform.Windows` | Monitor and window enumeration, WASAPI microphone and loopback capture served over named pipes. | Core, NAudio |
| `Linkast.App` | WPF UI (Fluent theme), dependency injection, logging. | all of the above |

The UI talks only to `IStreamEngine`. Replacing WPF, or replacing the FFmpeg engine, does not affect the other side.

## Media pipeline

- **In-app D3D11 GPU compositor and capture (`D3D11StreamEngine`).** Display and window capture (`WindowsGraphicsCaptureSource`), cameras (`CameraD3D11SceneSource`), images/scene backgrounds (`ImageD3D11SceneSource`), and web overlays (`WebOverlayD3D11SceneSource`) are composited directly on the GPU in `D3D11SceneCompositor` and converted to BT.709 NV12 on a dedicated monotonic render thread (`MediaOutputScheduler`). See [ADR 0010](adr/0010-d3d11-scene-compositor.md).
- **Audio capture and mixing in Linkast (`UnifiedAudioMixer`).** Linkast captures each WASAPI device and process loopback with NAudio, normalizes to 48 kHz stereo float, applies noise removal (RNNoise), peak limiting and jitter buffering, and writes s16le PCM locked to the video timeline.
- **One FFmpeg process per output (`RecordingOutput`).** The primary output streams FLV (through `BufferedOutputLease` and `RelayOutputTransport`), records MKV in record-only mode, or writes both at once via `tee` when recording quality is "Same as stream". When a separate recording quality or a simultaneous vertical 9:16 canvas is enabled, each extra output gets its own `D3D11OutputTarget` (scaled on GPU if needed), audio sink fan-out, and `ffmpeg.exe` process so a recording failure never interrupts the live stream. See [ADR 0011](adr/0011-extra-recording-outputs.md).
- **Live vertical canvas updates (`SetCanvasLayoutAsync`).** While a vertical recording is running, edits in the vertical preview update only the vertical scene in `WebcamStudioViewModel`, rebuild its normalized `CanvasLayout`, and push it into `D3D11StreamEngine.SetCanvasLayoutAsync("vertical", layout)` so the scheduler's portrait canvas picks up moved, resized, or quick-arranged layers immediately (and retains them across reconnects) without disturbing the landscape stream.
- With no audio sources selected, a silent track is still sent, because most ingests require audio.

## Camera layers (Webcam Studio)

Linkast captures the webcam itself with WinRT `MediaCapture` and renders the finished layer: crop, adjustments, shape and border. The same renderer drives the preview. The layer reaches FFmpeg as raw BGRA over a named pipe and is composited with `scale@camN` and `overlay@camN`. A `zmq` filter lets Linkast move or resize the layer live. Camera problems only make the layer transparent; they never end the stream. See [webcam-architecture.md](webcam-architecture.md) and [ADR 0005](adr/0005-camera-owned-by-linkast.md).

Up to four camera layers are composited bottom to top, blended in YUV 4:2:0. The active layout decides the output orientation (vertical rotates the resolution), where the gameplay goes (Fit or Fill) and where each camera goes. Auto-framing, the voice level and segmentation are shared services: one worker or capture per camera or device, used by both the preview and the stream. See [ADR 0008](adr/0008-layers-layouts-and-shared-inputs.md).

## Settings and secrets

Settings are versioned JSON in `%LOCALAPPDATA%\Linkast\settings.json`, written atomically. They are clamped on load, and a corrupt file is kept aside rather than overwritten. The stream key is DPAPI-encrypted in `secrets.dat`.

## Output and the local relay

FFmpeg does not connect to the ingest server. It publishes plain RTMP to `127.0.0.1:<ephemeral port>`, with `-rtmp_tcurl` set to the real server URL. `RelayOutputTransport` accepts exactly one loopback connection, opens the outbound socket itself (adding TLS with normal certificate validation for `rtmps://`), and copies bytes both ways. See ADR 0001.

This gives Linkast:

- **Real throughput,** counted on the socket that leaves the PC.
- **The actual local endpoint and interface** carrying the stream, shown in the UI.
- **Precise network errors** such as DNS failure, refusal, timeout, TLS failure, or the server closing the connection.
- **Adapter selection** (Phase 2, [ADR 0006](adr/0006-adapter-binding.md)). The outbound socket is bound to the chosen adapter with `IP_UNICAST_IF` and a source-address bind. Linkast verifies the real local endpoint, resolves DNS through the adapter when needed, and never falls back to another network. The shared `OutboundConnector` also powers **Test connection**.

## Session lifecycle

```
Idle ─► Preparing ─► Connecting ─► Live ─► Stopping ─► Stopped
              │           │  ▲        │
              │           ▼  │        ▼
              └──► Failed ◄─ Reconnecting
```

- `StartAsync` validates the session. Invalid values fail immediately as a Configuration error and nothing is launched.
- Each attempt opens the relay, starts the audio pipes, launches FFmpeg and supervises it once per second.
- **Live requires evidence.** Encoded frames must have advanced within the health window, the relay must be connected, it must have forwarded more than 32 KB, and its byte count must still be rising. A running process alone never counts.
- **Stall detection.** If frames or socket bytes stop advancing for longer than the stall timeout while live, the attempt ends with a classified error.
- **Reconnect.** Only Network-category errors are retried, with exponential backoff (1 s doubling to 30 s, at most 10 attempts by default). The budget resets after 60 s of stable streaming. Capture, encoder, audio and configuration errors stop the session, because retrying would not help.
- **Stop** sends `q` to FFmpeg so the stream ends cleanly, and kills it after 5 s if needed.
- FFmpeg runs in a Windows Job Object with kill-on-close, so it cannot outlive Linkast even after a crash. It runs at above-normal priority so a game cannot starve the encoder.

## Errors

`ErrorClassifier` maps FFmpeg's stderr lines (using `-loglevel level+warning`) to `StreamError` values with a category, a stable code, a plain-language message and the redacted source line. The relay's own error is preferred over FFmpeg's, because FFmpeg only sees a local socket. The UI labels each error as a capture, audio, encoding, network, settings or FFmpeg problem.

## Secrets

- `Secret` wraps the stream key. Its `ToString()` always returns a mask.
- `Redactor` removes registered secrets, including their URL-encoded forms, and structurally masks the path segment after the RTMP application name in any URL.
- Redaction is applied at the source (engine diagnostics, the UI log, diagnostics export) and again in the Serilog file sink.
- Logs live in `%LOCALAPPDATA%\Linkast\logs`, roll daily, are capped at 10 MB per file, and are kept for 7 files.

## Threading

FFmpeg output is read on thread-pool threads. Engine events fire on arbitrary threads, and the view model marshals them to the WPF dispatcher. Starting and stopping are asynchronous, so the UI never blocks on the media pipeline.

## Encoder selection

At startup `EncoderCatalog` encodes five real frames with each candidate in parallel: NVENC, AMF, Quick Sync, Media Foundation, then x264. Only encoders that succeed are offered. Hardware is preferred, and x264 is the explicit software fallback.

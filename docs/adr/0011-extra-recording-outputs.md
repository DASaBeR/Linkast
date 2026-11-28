# ADR 0011: Extra Recording Outputs (Separate Quality and Vertical Canvas)

**Status:** Accepted (extends [ADR 0010](0010-d3d11-scene-compositor.md))

## Context

Streamers need two recording capabilities that single-encode `tee` muxing cannot provide:

1. **Separate recording quality:** Stream at one resolution and bitrate (for example 720p at 4500 kbps) while recording a higher- or lower-quality local MKV (for example 1080p at 12000 kbps) from the same canvas.
2. **Simultaneous vertical recording:** Record a separate 9:16 portrait canvas (for Shorts, Reels, and TikTok) built from a dedicated vertical scene while streaming a normal landscape scene.

Putting multiple outputs into a single FFmpeg process couples their lifetimes: a disk-full error, recording folder problem, or encoder session refusal on a local recording would terminate the entire FFmpeg process and drop the live stream.

---

## Decisions

| Concern | Decision | Why |
|---|---|---|
| **Process isolation** | **One FFmpeg process per extra output (`RecordingOutput`).** Each secondary recording gets its own video/audio named pipes, `D3D11OutputTarget`, and `ffmpeg.exe` process via `FfmpegArgumentBuilder.BuildSinglePipe`. | A failure on a recording (disk full, encoder session limit, broken pipe) never stops the primary live stream. When recording quality is "Same as stream", the stream's FFmpeg keeps using the zero-overhead `tee` muxer. |
| **Tick & A/V sync** | **Synchronous writes in the same render tick and audio fan-out.** All attached `D3D11OutputTarget` instances are written synchronously inside `MediaOutputScheduler.RenderSingleFrame`, and `UnifiedAudioMixer.WriteAsync` fans out the identical PCM slice to every audio sink. | Keeps one shared `LostTime` and sample timeline across all outputs without per-output frame queues or independent frame dropping that would drift audio from video. |
| **Canvas sizing & GPU scaling** | **Render main canvas at the largest output size; scale down per target.** When the main recording differs from the stream size, the main canvas renders at the larger size and `D3D11OutputTarget` scales down via an internal `D3D11SceneCompositor`. | Preserves full detail for the highest-resolution output while avoiding duplicated scene composition work. |
| **Primary vs secondary lifecycle** | **Primary failure drives reconnect; secondary failure detaches and warns.** The primary output (stream when broadcasting, main recording in record-only mode) controls session state and reconnects. A secondary output failing detaches its video target and audio sink, stops its FFmpeg, and raises `RecordingFailed`. | Keeps viewers online even if local storage or extra encoder capacity runs out. |
| **Audio mix (version 1)** | **Every output receives the same mixed audio.** Scene mutes and mixer faders apply equally to the stream and extra recordings. | Simple, deterministic A/V sync; independent per-canvas audio mixes can be added later without changing the pipe contract. |

---

## Consequences

- **Extra hardware encodes:** Each active extra output consumes an additional GPU encode session, an NV12 GPU-to-CPU readback, and an `ffmpeg.exe` process. Consumer GPUs enforce a concurrent encode session limit (surfaced via `ErrorClassifier` as `encoder.nvenc.session`). Using a software encoder (`libx264`) for multiple outputs significantly increases CPU load.
- **Width divisibility:** The GPU NV12 compute shader packs 4 luma pixels per `uint`; widths must be divisible by 4 (`SessionValidator` enforces `encoder.resolution.width4`, and 480p uses `852×480`).
- **Attempt-scoped lifetime (version 1):** Secondary recordings are started after the primary handshake and closed when the attempt ends. A network reconnect closes the current MKV files and starts new ones on the next attempt.

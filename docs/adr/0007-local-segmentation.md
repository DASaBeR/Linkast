# ADR 0007: Local person segmentation with RobustVideoMatting, ONNX Runtime and Windows' DirectML

**Status:** Accepted (Phase 3)

## Context

Background blur, removal and replacement need a person-segmentation model that:

- runs locally, with no cloud and no uploads;
- runs in real time next to a game and the encoder;
- is stable over time, without a flickering outline;
- can legally be distributed with GPL-3.0 software.

GPU inference is preferred, with a CPU fallback.

## Decision

| Concern | Choice | Why |
|---|---|---|
| Model | **RobustVideoMatting, MobileNetV3, fp32** (official v1.0.0 ONNX, 14,975,696 bytes, SHA-256 `88D45312…BD2828`) | GPL-3.0, the same license as Linkast. It's a video model with recurrent state, so it gives temporally stable masks without extra smoothing. Matting (soft alpha) handles hair better than hard segmentation. |
| Runtime | **ONNX Runtime 1.24.4**, `Microsoft.ML.OnnxRuntime.DirectML` | MIT. One API for GPU and CPU. |
| GPU | **DirectML from Windows (System32)**, *not* the redistributable `DirectML.dll` | The redistributable is proprietary and its terms include a data-collection clause, so bundling it in a GPL app is unclear. Windows 10 and 11 ship DirectML as an OS component, which falls under GPL's system-library exception. The dev PC has 1.15.5, newer than the 1.15.4 redistributable. The NuGet package's copy is disabled build-wide and the publish script fails if a DirectML DLL ever appears in the package. |
| CPU fallback | Automatic when DirectML is missing, too old or fails; also selectable ("Use the graphics card" off) | The active provider is always shown in the UI and logs. |
| Delivery | The model is fetched at build time by `tools/fetch-models.ps1` (size and hash checked), shipped in `models/`, and re-verified before loading | Nothing is downloaded or executed at runtime. A damaged or substituted model is refused. |

## Architecture

- **One worker per camera and setting.** `SegmentationService` runs one worker per (camera, quality, GPU preference) on a dedicated below-normal-priority thread. The preview and the stream subscribe to the same worker, so each frame is processed once.
- **Latest frame wins.** The worker reads the newest camera frame, runs the model, and publishes the mask from a three-buffer rotation, never queuing frames.
- **Compositing in the shared renderer.** `CameraLayerRenderer` samples the mask in source coordinates, so crop, zoom and mirror line up. It softens edges and composites:
  - blur that excludes the person, at quarter resolution, with no halo;
  - a transparent cut-out;
  - an image scaled to cover the layer.
- **Off means released.** Switching to Original disposes the subscription. The last subscriber stops the worker and frees the model and GPU memory, including while live.

## Consequences

- **Real-time on the dev PC.** On the RTX 3070, segmentation takes 8–12 ms. On the CPU (i5-12400F, 3 threads) it takes 21–48 ms depending on quality.
- **Older DirectML.** On Windows builds whose DirectML is too old for ONNX Runtime 1.24, effects run on the CPU and the UI says so.
- **DirectML is in maintenance mode.** Microsoft's successor (Windows ML) can replace the provider later without touching the rest of the pipeline.
- **Compositing is on the CPU.** It costs 4–9 ms per 640×360 layer. A GPU compositing path remains a possible optimization; it isn't needed at current costs.

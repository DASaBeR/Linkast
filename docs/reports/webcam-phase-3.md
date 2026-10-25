# Phase 3 report: background effects and GPU processing

**Status:** Implemented and validated on the development PC, through to the received stream. Not yet validated with a live person on a physical webcam (the only camera is Iriun showing its placeholder), on AMD or Intel GPUs, or on a PC without a usable GPU; see [field-test-plan.md](../field-test-plan.md#d-background-effects-from-phase-3).

**Test machine:** Windows 11 build 26200 (DirectML 1.15.5 in System32), i5-12400F (12 threads), RTX 3070, FFmpeg 9.0.1. Quality tests use a public-domain NASA portrait, 1280×720, test-only and not shipped.

## What was built

- **Modes:** Original, Blur (strength), Remove (transparent: whatever is behind the layer shows), and Image (any picture). Plus edge softness and three quality levels (Performance, Balanced, Quality).
- **Local AI segmentation** ([ADR 0007](../adr/0007-local-segmentation.md)):
  - RobustVideoMatting on ONNX Runtime;
  - the GPU through Windows' own DirectML, with an automatic or user-chosen CPU fallback;
  - the model is hash-verified at build and at load, and never downloaded at runtime.
- **One inference per frame.** The preview and the stream share one worker per camera. A dedicated below-normal thread processes the latest frame only.
- **Compositing in the shared renderer,** so the preview equals the stream. The mask follows crop, zoom and mirror. The blur excludes the person, so there's no halo. Images are scaled to cover. A missing image falls back to blur, and a missing mask shows the original image.
- **Background images** are copied into `%LOCALAPPDATA%\Linkast\backgrounds` by content hash, so moving or deleting the original file doesn't break a saved look. They're decoded with Windows' codecs off the render thread.
- **Live status** shows, for example, "Running on GPU (DirectML) · 10 ms per frame · 30 masks/s", or explains why it can't run (model missing or damaged, GPU unavailable so CPU is used, processing error). The details line also names the model's provider.
- **Turning it off releases resources.** Switching to Original frees the model and GPU memory at once, including while live.
- **Low-light boost** (checklist 2.4). Adaptive gamma lifts dark scenes toward normal exposure, smoothed over time, and leaves bright scenes alone. The built-in Low Light preset uses it.
- **Persistence.** All background settings are part of the camera look, so they're saved per camera and in presets. Older settings files default to Original.

## Measurements

**Segmentation, real model and real portrait, median of 30 frames:**

| Quality | Input | GPU (DirectML, RTX 3070) | CPU (3 threads) |
|---|---|---|---|
| Performance | 384×224 | 8.0 ms | 21.4 ms |
| Balanced (default) | 512×288 | 10.3 ms | 36.8 ms |
| Quality | 768×448 | 11.9 ms | 48.1 ms |

Model load including GPU warm-up takes 0.9–1.3 s on the GPU and 0.16–0.35 s on the CPU. It happens on the worker thread; the UI shows "Loading background model…".

**Compositing, 640×360 layer, Release:** original 2.2 ms, blur 8.6 ms, remove 4.0 ms, image 4.4 ms.

**Packaged app, live** at 1080p60 NVENC desktop capture plus the webcam (1280×720, about 21.7 fps from Iriun), preview visible:

| Background | Linkast CPU (whole machine) | FFmpeg CPU | Linkast GPU | Linkast memory |
|---|---|---|---|---|
| Original | 4.5 % | 12.9 % | 0.1 % | 370 MB |
| Blur | 8.9 % | 9.9 % | 13.4 % | 464 MB |
| Remove | 7.7 % | 10.9 % | 20.8 % | 465 MB |

Mode changes while live kept the stream LIVE. Switching to Original returned the GPU to 0.1% and freed about 94 MB.

**Proposed performance targets** (to be agreed): on a GPU of RTX 3060 class or better, the effect should add at most 6% whole-machine CPU and at most 15 ms segmentation latency at Balanced. On CPU-only machines, Performance quality should reach at least 20 masks/s on a 6-core CPU. The RTX 3070 and i5-12400F meet these.

## Validation

| Check | Result |
|---|---|
| Model hash verification; a tampered copy is refused | Pass |
| Real model on the portrait, all 3 qualities × GPU/CPU | Face 255, body 255, flag 0, wall 0 on every run; GPU provider confirmed |
| Renderer with the real mask | Face pixels identical in every mode; background visibly blurred, transparent (alpha 0), or the image colour exactly |
| Full stream with a real person: portrait as a 30 fps test camera through the shared worker (GPU), layer pipe, FFmpeg, relay and MediaMTX | Received frame: background replaced by the image (0,200,0), face intact (140,75,56). Turning the effect off live gave 0 workers and the stream stayed Live. |
| Unit tests with synthetic masks: remove, blur excludes person, image and fallback, no mask gives original, mask follows mirror, low-light boost | Pass |
| Settings round trip; Phase 1 files default to Original | Pass |
| UI: Blur on Iriun's placeholder (no person, so all background) blurs it; status "Running on GPU (DirectML) · 20 ms · 22 masks/s" in a Debug build | Pass |
| Package contains no DirectML redistributable; GPU via Windows' DirectML works in the packaged app | Pass |

**Test totals:** 148 pass in the default run (Core 74, Engine 34, Networking 27, Effects 1, Integration 12), and 9 opt-in real-model tests pass.

## Files

**Added**
- `src/Linkast.Effects/` (new project): ModelCatalog, RvmSegmenter, SegmentationService
- `src/Linkast.Core/Camera/Background.cs`
- `src/Linkast.Platform.Windows/Camera/`: BackgroundImageCache, LayerEffectsBinding
- `tests/Linkast.Effects.Tests/` (new), `tests/Linkast.IntegrationTests/BackgroundEffectsIntegrationTests.cs`
- `tools/fetch-models.ps1`, `models/README.md`, `docs/adr/0007-local-segmentation.md`, this report

**Changed**
- CameraLayerRenderer: mask sampling, edge softness, blur/remove/image compositing, low-light gamma
- CameraLayerSettings: `Background`, `LowLightBoost`
- CameraLayerPipeInput and provider: effects binding
- WebcamViewModel and WebcamPanel: Background card, image picker, status, low-light slider
- App: DI for the segmentation service and image cache
- CameraPresets: Low Light uses the boost
- `Directory.Build.props`: never copy the DirectML redistributable
- `tools/publish.ps1`: ships models, refuses DirectML DLLs, drops `.lib` files
- `.gitignore`: model binaries

## New dependencies

| Component | License | Notes |
|---|---|---|
| Microsoft.ML.OnnxRuntime.DirectML 1.24.4 (onnxruntime.dll) | MIT | Embedded in Linkast.exe (self-extracting single file) |
| RobustVideoMatting MobileNetV3 model | GPL-3.0 | `models/`, with its license file |
| DirectML | Part of Windows | Not shipped |

## Known limitations

- **Not tested with a real person on a webcam.** Hair detail, motion, low light and lighting changes need real-user testing.
- **Video backgrounds are not implemented.** The brief marks them optional.
- **Compositing runs on the CPU.** About 4–9 ms per layer per consumer; acceptable, but a GPU path would lower it.
- **Older Windows.** Builds with an older DirectML fall back to the CPU. Which Windows 10 builds can run GPU inference needs field data.
- **One frame behind.** The mask can trail the picture by one camera frame on very fast motion. This is inherent to latest-wins processing.
- **Remove mode and borders.** With Remove, the border still follows the layer shape, so on a transparent background only the person shows. A border that follows the person is a Phase 4 idea.

## Reproduce

```powershell
powershell -ExecutionPolicy Bypass -File tools/fetch-models.ps1
dotnet test Linkast.slnx -c Release
# real-model quality and full-stream tests need a 1280x720 BGRA photo of a person:
$env:LINKAST_TEST_PORTRAIT = "C:\path\portrait_1280x720.bgra"   # ffmpeg -i photo.jpg -vf scale=1280:720 -f rawvideo -pix_fmt bgra portrait_1280x720.bgra
dotnet test tests/Linkast.Effects.Tests -c Release
dotnet test tests/Linkast.IntegrationTests -c Release --filter "FullyQualifiedName~BackgroundEffects"
```

## Next phase

**P4: advanced webcam features.** Auto-framing with face tracking, animated borders, audio-reactive effects, multiple cameras, and landscape and vertical layouts.

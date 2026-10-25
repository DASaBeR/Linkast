# Webcam Studio, Phase 1 report

**Status:** Phase 1 is implemented. Everything is validated on the development machine except the items marked Implemented or Blocked in [webcam-checklist.md](../webcam-checklist.md); those need a physical webcam or user action.

**Test machine:** Windows 11 Pro build 26200, Intel Core i5-12400F (12 threads), NVIDIA RTX 3070, FFmpeg 9.0.1. The only camera is Iriun Webcam, a phone-as-webcam driver, delivering its "Please start Iriun Webcam" image at 1280×720 YUY2.

## What was built

- **Camera discovery and capture.** WinRT `MediaCapture`, with hot-plug watching. Each physical device is opened once and shared by reference count between preview and stream. Linkast picks a format automatically or uses the one chosen, falls back to shared read-only mode when another app holds the camera, and reports clear errors for in use, access denied, disconnected and unsupported format. It reopens the camera automatically after a failure or a stall.
- **Camera layer renderer** (CPU), shared by preview and stream so they always match:
  - crop and zoom, mirror;
  - brightness, contrast, saturation;
  - temporal noise reduction, sharpen;
  - rectangle, rounded and circle shapes with an anti-aliased border.
- **Hardware controls.** Exposure, focus and white balance appear only when the camera reports support, each with an Auto option, and are remembered per camera.
- **Stream integration.** The rendered layer reaches FFmpeg as raw BGRA over a current-user-only named pipe, paced to the output frame rate. FFmpeg overlays it, and a `zmq` command filter on a random loopback port moves and resizes it live. A camera that is off or failing gives a transparent layer, so the stream never stops.
- **"Webcam only" source.** A black canvas with just the camera, for camera-only scenes.
- **Layout editor.** It uses a 1920×1080 logical canvas, so editor coordinates are output pixels. You can drag, resize from the corners with the aspect locked, snap to the grid, edges and centre, and nudge with the arrow keys. The background is a layout reference that refreshes every 2 s. There are also quick placement buttons.
- **Presets.**
  - Built-in: Gaming, Just Chatting, Professional, Low Light, Minimal.
  - Custom: save as new, update, duplicate, rename, delete, reset.
  - Presets store the look and placement, never a device.
- **Persistence.**
  - Versioned `settings.json` with migration, clamping and corrupt-file quarantine, written atomically.
  - Every stream and webcam setting is remembered.
  - The stream key is DPAPI-encrypted for the current Windows user in `secrets.dat`.

## Architecture decisions

- [ADR 0005](../adr/0005-camera-owned-by-linkast.md): Linkast owns the camera and FFmpeg composites. The camera opens once, effects run where AI can later run, and live changes need no restart.
- The latest frame wins at every stage. The capture triple buffer, the pipe writer and the preview drop stale frames instead of queuing them.
- Camera errors are non-fatal for the stream. Audio errors still stop it, as in Milestone 1.

## Files

**Added**
- `src/Linkast.Core/Camera/`: CameraModels, CameraLayerSettings, LayerGeometry, LatestFrameExchange, CameraLayerRenderer, ICameraService, CameraLayers
- `src/Linkast.Core/Settings/`: LinkastSettings, CameraPresets, SettingsSerializer
- `src/Linkast.Platform.Windows/Camera/`: MediaCaptureCameraService, CameraSession, CameraLayerPipeInput
- `src/Linkast.Platform.Windows/Capture/ScreenSnapshot.cs`
- `src/Linkast.Engine.FFmpeg/FfmpegCommandChannel.cs`
- `src/Linkast.App/`: ViewModels/WebcamViewModel, Views/WebcamPanel, Views/SceneEditor, Services/JsonSettingsStore, Services/DpapiSecretStore
- Tests: `tests/Linkast.Core.Tests/CameraTests.cs`, `SettingsTests.cs`, `tests/Linkast.IntegrationTests/CameraIntegrationTests.cs`
- Docs: webcam-architecture, webcam-checklist, ADR 0005, this report

**Changed**
- FfmpegJob, FfmpegArgumentBuilder: layer inputs, overlay chain, zmq, blank canvas
- FfmpegStreamEngine: layer inputs per attempt, `ILiveSceneControl`
- StreamSession: `CameraLayers`; Sources: `BlankCanvasSource`
- MainViewModel, MainWindow: tabs, persistence, encrypted key, live moves
- App: dependency injection, NetMQ cleanup
- Project target frameworks: `net10.0-windows10.0.19041.0` for Platform.Windows, App and the integration tests

## New dependencies

| Package | Why | License |
|---|---|---|
| NetMQ 4.0.4.5 | Talk to FFmpeg's `zmq` filter for live moves | LGPL-3.0 (GPL-compatible) |
| AsyncIO 0.1.69 (from NetMQ) | NetMQ I/O | **To verify.** The package declares no license; upstream repository believed MPL-2.0 |
| NaCl.Net 0.1.13 (from NetMQ) | NetMQ curve security (unused) | MPL-2.0 |
| WinRT `Windows.Media.Capture` | Camera | Part of Windows |
| `ProtectedData` (DPAPI) | Stream key | Part of .NET on Windows |

## Tests

| Suite | Result |
|---|---|
| Release build | 0 warnings, 0 errors |
| Core unit tests | 67 pass |
| Engine unit tests | 34 pass |
| Integration tests (real Iriun camera, real FFmpeg, MediaMTX) | 7 pass, 1 skipped (opt-in soak) |

The two new camera integration tests:

- **Open, share and release.**
  - The camera opens and delivers frames within 2 s.
  - Two consumers share one device session.
  - Zero sessions remain after release, and the camera reopens immediately.
- **Stream with a camera layer.**
  - The layer appears where placed.
  - A live zmq move is confirmed in the received frames.
  - Camera off makes the layer transparent and releases the device while the stream stays Live.
  - Camera on brings the layer back.

## Manual tests through the real UI (UI Automation, screenshots)

| Test | Result |
|---|---|
| Webcam tab, camera on, live preview, "Camera active" badge | Pass |
| Minimal preset gives a mirrored circle with a white border; Brightness 40 visibly lifts the image | Pass |
| Layout editor shows a desktop snapshot with the camera layer and handles | Pass |
| Mouse drag moved the layer (0.835, 0.044) to (0.521, 0.376); corner resize took width 0.14 to 0.232 with aspect kept at 1.000 | Pass |
| Preset save, rename, duplicate, delete | Pass |
| Go live: display capture plus webcam, NVENC 1080p60, then "Top right" pressed while live. Streamed frames show the layer bottom-left, then top-right, with no restart (log: moved live to 1603,48 268x268 ok) | Pass |
| Camera off while live: badge disappears, stream stays Live | Pass |
| Close and reopen: preset, brightness, placement, server URL and camera-on restored; went live using the saved encrypted key | Pass |
| Key absent from settings.json and logs, not plaintext in secrets.dat | Pass |

## Performance (whole machine, 12 threads)

Measured while live with 1080p60 NVENC desktop capture plus a 268×268 webcam layer and 1280×720 at 30 fps capture.

| Configuration | Linkast | FFmpeg |
|---|---|---|
| Camera on, Stream tab | 3.8 % | 9.3 % |
| Camera on, Webcam tab (preview visible) | 3.3 % | 8.7 % |
| Camera off (layer transparent) | 0.2–0.6 % | 8.0–8.9 % |

An isolated real-time A/B of the FFmpeg graph found no measurable cost from the layer: without a layer 10.9 %, with a layer 9.0–11.0 % across overlay formats. Run-to-run noise from desktop activity is larger than the difference. Linkast's own camera work, which is format conversion, rendering and pipe writes, costs about 3–4 % of the machine, roughly half a core.

The webcam processing path is CPU only. The UI shows "processing: CPU". A GPU path is planned for Phase 3.

## Known limitations

- **Untested on real webcams.** Unplug and replug, "in use by another app", hardware controls and explicit format selection need a real USB or integrated webcam; see the Blocked and Implemented rows in the checklist.
- **Privacy denial.** The message for Windows blocking camera access is implemented but was not tested, because that would mean changing the user's privacy setting.
- **Resizing while live.** FFmpeg rescales the layer from its start size. Enlarging it a lot while live looks softer until the next start.
- **Layout preview background.** The editor's background uses GDI, so an exclusive-fullscreen game may look black there even though the stream captures it.
- **Adding the webcam mid-stream.** The webcam layer is part of the stream only if a camera was selected when you went live. Camera on and off works live after that.
- **One webcam layer.** The UI offers a single webcam layer; the engine supports up to four, planned for Phase 4.
- **Corrupt settings.** A settings file with an invalid enum value resets to defaults. The bad file is kept aside, not lost.

## How to reproduce

```powershell
docker compose -f tools/test-ingest/docker-compose.yml up -d
dotnet test Linkast.slnx -c Release
# camera tests only:
dotnet test tests/Linkast.IntegrationTests --filter "FullyQualifiedName~CameraIntegration"
```

## Recommended next phase

**P2: Network adapter selection**, as agreed. The relay already reports the interface actually used, so binding and verification fit into one component.

The maintainer should also run the four Blocked or Implemented camera checks with a real webcam:
- Unplug and replug the camera while live.
- Open it in Teams, Zoom or Discord first, to test "in use".
- Change exposure and focus.
- Turn off camera access in Windows privacy settings.

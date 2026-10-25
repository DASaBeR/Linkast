# Webcam Studio requirement checklist

States: **Not started** · **In progress** · **Implemented** (not yet validated) · **Validated** · **Blocked** (with reason).

"Validated" means tested against real hardware or the real pipeline. A requirement is never marked Validated on the strength of a mock alone. The only camera on the development machine is **Iriun Webcam**, a phone-as-webcam driver; its phone was not connected, so it delivered its own "Please start Iriun Webcam" image. Evidence and measurements are in the phase reports: [1](reports/webcam-phase-1.md), [3](reports/webcam-phase-3.md), [4](reports/webcam-phase-4.md).

| # | Requirement | Phase | State | Evidence / notes |
|---|---|---|---|---|
| 1.1 | Discover cameras, show readable names | P1 | Validated | Iriun listed by name; integration test |
| 1.2 | Select active camera | P1 | Validated | UI and integration test |
| 1.3 | USB and integrated cameras | P1 | Blocked | No USB or integrated camera on the dev machine. Same WinRT path; needs a maintainer test. |
| 1.4 | Disconnect / reconnect handling, hot-plug refresh | P1 | Implemented | DeviceWatcher refresh, `MediaCapture.Failed` and a 4 s stall watchdog, then automatic reopen. Not validated: Iriun can't be unplugged and disabling it needs admin rights. |
| 1.5 | Live preview before adding to scene | P1 | Validated | UI screenshot; rendered off the UI thread |
| 1.6 | Choose resolution, fps, pixel format; auto default | P1 | Implemented | Auto validated (1280×720 YUY2). The explicit format list is shown; selecting one is not yet tested on hardware. |
| 1.7 | Clear "camera in use" / cannot open message | P1 | Implemented | Exclusive open falls back to shared read-only with a message; otherwise `camera.in_use`. Not validated: Iriun allows multiple exclusive opens. |
| 1.8 | Release resources on switch/close; no double open | P1 | Validated | One shared session for two consumers; zero sessions after release; immediate reopen; camera-off releases the device mid-stream |
| 2.1 | Brightness, contrast, saturation (software) | P1 | Validated | Unit tests; visible change in the preview |
| 2.2 | White balance, exposure, focus (hardware, if supported) | P1 | Implemented | Capability-gated controls. Iriun reports none, so the "not offered" path is validated; changing values needs a real camera. |
| 2.3 | Sharpen, noise reduction | P1 | Validated | Unit tests (edge contrast up, flat areas unchanged; flicker damped, motion kept) |
| 2.4 | Low-light enhancement | P3 | Implemented | Adaptive gamma "Low-light boost" (unit test: dark lifted, bright unchanged); used by the Low Light preset. Needs a real dark-room test (field test D4). |
| 2.5 | Unsupported controls marked; reset to default | P1 | Validated | Reset image and Reset to preset; unsupported hardware controls are disabled with a reason |
| 2.6 | Persist per camera; presets survive restart | P1 | Validated | Restart restored preset, brightness and placement; look is stored per device ID |
| 3.1 | Original / blur / remove / static image modes | P3 | Validated | Real model and real portrait through the renderer and the full stream (received frame checked); UI on Iriun |
| 3.2 | Video background | P3 | Not started | Optional in the brief; not implemented |
| 3.3 | Local model, GPU (DirectML) with CPU fallback, active mode reported | P3 | Validated | GPU and CPU both measured; provider shown in the UI and logs; Windows' own DirectML used |
| 3.4 | Edge smoothing, low flicker, natural hair and shoulders | P3 | Implemented | Recurrent model plus edge softness; checked on a still portrait. Motion and hair need a real person (field test D1). |
| 3.5 | Quality and performance modes | P3 | Validated | 3 levels measured on GPU and CPU |
| 3.6 | Inference shared between consumers; sessions and buffers reused | P3 | Validated | One worker per camera; ActiveWorkers = 1 while streaming with preview |
| 3.7 | Model provenance, licensing, versioning, packaging | P3 | Validated | Pinned SHA-256 at fetch and load; GPL-3.0; packaged with license; tampered model refused |
| 3.8 | Missing model or provider gives an actionable error or fallback | P3 | Validated | Unit-tested hash refusal; status messages; GPU-to-CPU fallback path. A missing model in the package is a field test (D5). |
| 3.9 | Inference off the UI thread; disable instantly | P3 | Validated | Dedicated thread; off releases the worker live (integration test) |
| 3.10 | Performance measured before default-on | P3 | Validated | Measurements in the Phase 3 report; effect defaults to Original |
| 4.1 | Rectangle, rounded, circle; aspect, crop | P1 | Validated | Unit tests for masks and crop; circle shown in preview and stream |
| 4.2 | Border | P1 | Validated | Unit test; white border visible in stream frames |
| 4.3 | Glow, animated border | P4 | Validated | Pulse and colour cycle are time-based on one shared clock (unit tests); colour cycle seen changing in two stream frames 1 s apart; glow uses a signed-distance mask (unit test) |
| 4.3b | Drop shadow, image mask | P4 | Not started | Not in Phase 4 scope; glow covers the "soft edge" use |
| 4.4 | Drag, resize handles, snap, crop/zoom separate from placement | P1 | Validated | Real mouse drag and corner resize (aspect stays 1.000 for the circle); snapping; zoom and crop are separate settings |
| 4.5 | Editor geometry equals output geometry; saved with scene | P1 | Validated | Editor uses a 1920×1080 logical canvas; streamed frame showed the layer exactly where placed |
| 5.1 | Auto-framing follows the face with smoothing, dead zone and hysteresis | P4 | Validated | Unit tests (dead zone, zoom hysteresis, jump confirmation, hold then ease out); integration test zooms onto an off-centre real portrait through Windows face detection |
| 5.2 | Face size, headroom, max zoom, speed; status shown | P4 | Validated | UI controls; "Looking for a face…" shown live with a camera that shows no face |
| 5.3 | Framing shared by preview and stream, one detector per camera | P4 | Validated | Worker keyed by camera and layer, shared by preview and stream pipe |
| 5.4 | Framing quality with a real moving person | P4 | Implemented | Needs a real person (field test E1) |
| 6.1 | Built-in presets: Gaming, Just Chatting, Professional, Low Light, Minimal | P1 | Validated | All listed in the UI; Minimal applied to preview and stream |
| 6.2 | Custom presets: duplicate, rename, edit, reset, delete | P1 | Validated | Driven through the UI: save as new, rename, duplicate, delete; "Update preset" and Reset are wired the same way |
| 6.3 | Versioned serialization, migration, missing-device fallback | P1 | Validated | Unit tests: round trip, unversioned migration, newer version, corrupt file, clamping. Presets never store a device. |
| 7.1 | Up to 4 camera layers, each with its own camera, name, look and effects | P4 | Validated | UI: add, rename, select, remove; settings schema 2 with v1 migration (unit test and real settings file) |
| 7.2 | Same camera in two layers opens it once | P4 | Validated | Integration test: two layers of one camera, one session, both visible in the stream |
| 7.3 | Show or hide a camera live; stacking order | P4 | Validated | Hide/show while live (stream frames); hide integration test. Stacking order applies on the next go-live. |
| 7.4 | Several different physical cameras at once | P4 | Blocked | Only one camera on the dev machine (field test E3) |
| 8.1 | Voice level from the existing microphone capture, no second session | P4 | Validated | Audio hub: one capture session per device for stream and meter (integration test) |
| 8.2 | Border pulse, glow ring, speaking indicator; threshold and intensity | P4 | Validated | Unit tests for meter attack/release/hysteresis and effect maths; meter live in the UI |
| 8.3 | Reaction to a real voice looks right | P4 | Implemented | Nobody spoke during validation (field test E2) |
| 9.1 | Landscape and vertical layouts with separate placements | P4 | Validated | UI and settings; vertical stream probed at 1080×1920 with gameplay on top (Fill) and camera below |
| 9.2 | Gameplay fit or fill per layout | P4 | Validated | Filter-graph unit tests; vertical Fill seen in the stream |
| 9.3 | Live switching between same-shape layouts; restart with confirmation otherwise | P4 | Validated | Landscape ↔ vertical while live: confirmation shown, stream restarted at the new size |
| 9.4 | Vertical 1080×1920 at 60 fps keeps real time | P4 | Validated | 59.6/60 fps, 1.00× with NVENC after moving camera blending to YUV (was 0.7× in BGRA) |
| 10.1 | Phone as webcam through phone apps | P6 | Validated | Iriun used as the development camera throughout; every feature works with it |
| 10.2 | Guide for Windows 11 connected camera, DroidCam, Camo | P6 | Implemented | [phone-as-webcam.md](phone-as-webcam.md); only Iriun is verified (field test G4) |
| 11.1 | Virtual camera visible to other apps | P6 | Validated | "Linkast Camera (Windows Virtual Camera)" listed for Media Foundation and DirectShow apps; 1280×720 NV12/RGB32 at 30 fps |
| 11.2 | Shows the chosen camera layer with its effects | P6 | Validated | Frame read through Windows' camera stack (DirectShow) showed the Iriun picture blurred; magenta test frame matched |
| 11.3 | Placeholder when Linkast isn't sending | P6 | Validated | Navy "Linkast is not running" picture read back (integration test) |
| 11.4 | Optional install with one admin prompt; remove; no admin for Linkast itself | P6 | Validated | Installed and removed through the app on the dev PC; registry, device and files checked |
| 11.5 | Costs nothing while no app watches | P6 | Validated | Producer renders only while frames are requested (integration test); CPU measured |
| 11.6 | Works in Zoom, Teams, Discord, Meet, OBS | P6 | Implemented | Standard Windows camera, but each app is a field test (G1) |
| 11.7 | Windows 10 | P6 | Blocked | The Windows virtual camera API needs Windows 11; Linkast explains this on Windows 10 |
| 12.1 | Camera-active indicator, camera-off control | P1 | Validated | "Camera active" badge; camera-off releases the device |
| 12.2 | No frames in logs; local-only processing | P1 | Validated | Frames are never logged or stored; logs checked |
| 12.3 | Windows privacy denial reported clearly | P1 | Implemented | `UnauthorizedAccessException` maps to a message pointing to Settings › Privacy › Camera. Not validated: would require changing the user's privacy setting. |
| 12.4 | Camera failure does not stop streaming other sources | P1 | Validated | Camera off mid-stream: layer transparent, stream stays Live (integration test and UI) |
| S.1 | Camera layer in real stream output | P1 | Validated | Over desktop capture (NVENC 1080p60) and over the blank canvas |
| S.2 | Live move/resize without stream restart | P1 | Validated | zmq command; frames confirm the move; integration test and UI |
| S.3 | Stream key saved encrypted (DPAPI) | P1 | Validated | Restored after restart and used to go live; absent from settings and logs; not plaintext in secrets.dat |

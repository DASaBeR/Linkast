# Phase 4 report: advanced webcam features

**Status:** Implemented and validated on the development PC, through to the received stream. Not yet validated with a real person on a physical webcam, with two different physical cameras, or on a real vertical-video platform; see [field-test-plan.md](../field-test-plan.md#e-advanced-webcam-features-from-phase-4).

**Test machine:** Windows 11 build 26200, i5-12400F (12 threads), RTX 3070, FFmpeg 9.0.1. The only camera is Iriun Webcam; its phone wasn't connected, so it delivered its own "Please start Iriun Webcam" image.

## What was built

- **Auto-framing.**
  - Windows' built-in `FaceDetector` runs on this PC about 10 times a second, on a 480-px grey copy of the frame.
  - `AutoFramer` turns detections into a calm crop. It has:
    - a dead zone (small movements are ignored);
    - zoom hysteresis (no "breathing");
    - confirmation before following a big jump;
    - a 2-second hold, then a slow zoom out, when the face is lost;
    - time-based easing whose speed the user sets.
  - Controls: face size, headroom, maximum zoom and speed, plus a live status ("Following your face", "Looking for a face…" or the problem).
  - One worker per camera and layer, shared by the preview and the stream.
- **Border effects:**
  - pulse and colour-cycle animations on one app-wide clock, so they look the same at any frame rate and identical in the preview and the stream;
  - a soft glow drawn from a signed-distance mask, so it follows circles and rounded corners exactly.
- **Voice-reactive effects:**
  - Styles: border grows with your voice, glow ring lights up, or the border changes colour while you speak.
  - The voice level comes from a new `AudioCaptureHub`. It opens one capture session per device and shares it, so the stream's microphone and the meter never open the microphone twice.
  - The level meter uses attack/release smoothing, a threshold with hysteresis and a hold, so the effect doesn't flicker between words.
- **Up to four cameras.**
  - Each camera layer has its own name, camera, look, background effect, framing and effects.
  - Two layers can use the same camera, for example a close-up and a wide shot, and it is opened once.
  - Add, remove, rename, select, bring forward and send backward.
  - In the editor you click a camera to select it, then drag or resize it.
  - A camera can be shown or hidden per layout. While live, moving, resizing, hiding and showing are instant.
- **Landscape and vertical layouts.**
  - Each layout stores the output orientation, the gameplay area, Fit or Fill for the gameplay, and a position per camera. Positions are kept separately, so arranging one never disturbs the other.
  - Vertical streams the chosen resolution rotated (1080×1920). The default vertical layout puts the game on top (Fill, no bars) and the camera below.
  - Switching while live is instant between layouts of the same shape. Otherwise Linkast explains why and asks before restarting the stream.
- **Settings schema 2.** Layers and layouts are saved; Phase 1–3 files migrate automatically (look, camera and position kept).
- **Accessibility.** After switching cameras, the newly shown settings panel is exposed to screen readers. The default list control kept showing the first panel, so it was replaced.

Design decisions are recorded in [ADR 0008](../adr/0008-layers-layouts-and-shared-inputs.md).

## Problems found and fixed during validation

| Problem | Cause | Fix |
|---|---|---|
| Vertical 1080×1920 at 60 fps ran at 0.7× real time, and the camera dropped out of the stream | The large camera (844×844) was alpha-blended in BGRA: about 2.8 cores for FFmpeg | Blend in YUV 4:2:0: same output at about a third of the CPU, 59.6–60 fps |
| FFmpeg's memory grew from 0.4 to 9.6 GB during a 30-minute stream | Camera pipes were timestamped by frame count, the capture by the wall clock. Whenever FFmpeg read slower than real time the camera lagged, and the overlay kept every gameplay frame waiting for it. Present since Phase 1, but only visible once FFmpeg dipped below 1.00×. | Wall-clock timestamps for camera pipes; repeat soak flat at 412–422 MB |
| Linkast used about 2 cores with two cameras and effects | Layers were rendered and piped at the stream's 60 fps | Layers capped at 30 fps; the overlay repeats the latest frame. About 1.3 cores. |
| A circle drawn as an oval in the vertical layout | The vertical slot wasn't square | Every slot is fitted to the camera's own shape |
| A new camera copied another camera's look | Choosing a device applied that device's remembered look, even when the app chose it automatically | Remembered looks apply only when the user switches cameras |
| Crash at start-up | A property handler ran before the studio existed | Guarded |

## Measurements

**Live, Debug build, 30 minutes:**
- Setup: 1080p60 NVENC desktop capture with system audio, landscape.
- Camera 1: circle with background blur on the GPU.
- Camera 2: rounded, colour-cycle border, glow, voice reaction on the microphone, auto-framing.
- Both previews visible. Sampled every minute.

| | Before the fixes | After the fixes |
|---|---|---|
| Frame rate | 59.5 → 58.2 / 60 | 59.8 → 60 / 60 throughout |
| Encoder speed | 1.00× → 0.97× | 1.00× throughout |
| Dropped frames, reconnects | 0, 0 | 0, 0 |
| FFmpeg memory | 417 MB → **9,601 MB** | 412 → 422 MB |
| FFmpeg CPU (share of one core) | 47–76 % | 53–79 % |
| Linkast memory | 502–519 MB | 484–493 MB |
| Linkast CPU (share of one core) | 173–231 % | 125–159 % |

**Vertical (1080×1920 at 60 fps, NVENC):**
- Before the YUV change: 39 fps, 0.69×.
- After: 59.6/60, 1.00×, 0 dropped frames.
- The stream was probed at 1080×1920.

**Compositing benchmark** (FFmpeg alone, real capture, 10 s, NVENC):

| Case | Before | After |
|---|---|---|
| Vertical, 844×844 overlay | 0.95–0.98×, 27 s CPU | 0.99×, 9 s CPU |
| Landscape, 536×302 overlay | 0.98×, 6 s CPU | 0.98×, 5 s CPU |

## Validation

| Check | Result |
|---|---|
| Unit tests, Core (97): framer dead zone, hysteresis, jump confirmation, hold and ease out; meter attack, release and hysteresis; effect maths; framing override; glow; layouts; fit-inside; settings v2 and v1 migration | Pass |
| Engine tests (36): YUV overlay graph, vertical Fill, gameplay area Fit, wall-clock camera inputs | Pass |
| Face detection on a real portrait; the framer zooms onto an off-centre face (opt-in) | Pass |
| Audio hub: two subscribers share one session; the meter and a stream subscriber share the microphone | Pass |
| Two layers of one camera: one camera session, both visible in the received stream; hide, then show again live | Pass |
| Vertical stream probed at 1080×1920; gameplay fills the top, camera fills the bottom area | Pass |
| UI: add a camera, select, rename, show or hide per layout, landscape and vertical editors, effect cards | Pass |
| Live landscape ↔ vertical: confirmation text, restart at the new size, stream back to LIVE | Pass |
| Live colour cycle: border colour differs between frames 1 s apart; hide and show live | Pass |
| Packaged build: starts with the bundled FFmpeg, migrates old settings, adds a camera, exits with no FFmpeg left | Pass |
| Full suite in Release: 176 default tests, plus 10 opt-in real-model, face and camera tests | Pass, 0 warnings |

## Dependencies and licensing

No new packages. Face detection uses `Windows.Media.FaceAnalysis` (part of Windows). Voice metering reuses NAudio.Wasapi, which was already in use.

## Limitations

- **Stacking order** changes apply on the next go-live; FFmpeg's overlay chain can't be reordered live. Cameras added while live also appear on the next go-live, and the UI says so.
- **Animation rate.** Animations and the camera update 30 times a second in the stream, even at 60 fps output.
- **Preset positions** are designed for landscape. In a vertical layout a preset applies only the look.
- **Voice reaction** uses the microphone chosen on the Stream tab, even when that microphone isn't added to the stream.
- **Vertical needs rotation.** The vertical layout always rotates the configured resolution. A separate vertical resolution is not offered.
- **One composition per stream.** Landscape and vertical at the same time would need two encoders and two uploads, and is left for later.
- **CPU rendering.** Layer rendering is on the CPU. With four large cameras on a slower CPU, the render cost may matter; a GPU layer path is the next optimisation.

## Next phase

P5, installer and beta: an installer with updates through GitHub Releases, a pinned FFmpeg with license compliance, a compatibility matrix and a security review. The field tests in sections A–E remain open for real users.

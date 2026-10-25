# ADR 0008: Camera layers, layouts and shared inputs

**Status:** Accepted (Phase 4)

## Context

Phase 4 adds auto-framing, animated and voice-reactive effects, up to four cameras, and landscape and vertical output. Each of these could easily double work that already runs:

- a second face detector per consumer;
- a second microphone capture;
- a second camera session;
- a second compositor.

It must also stay real-time next to a game at 1080p60.

## Decisions

| Concern | Decision | Why |
|---|---|---|
| Settings | **Schema 2.** A list of camera layers (camera, look, effects) and a list of layouts. Each layout has an orientation, a gameplay area, Fit or Fill, and a placement per layer. Schema 1 files migrate automatically. | A layer's look belongs to the layer, and its position belongs to the layout. Switching layouts never disturbs another layout's arrangement. |
| One composition per stream | A stream uses one layout. Vertical streams the configured resolution rotated (for example 1080×1920). | One encoder and one upload. Simultaneous landscape and vertical outputs would double both and are left for a later assessment. |
| Live changes | Moving, resizing, hiding and showing a camera go to FFmpeg through the existing `zmq` channel; hiding moves it off the canvas. Switching between layouts with the same shape and gameplay area is live. Anything else changes the output, so the user confirms a short restart. | Never restart without asking; never pretend a change applied when it didn't. |
| Layer shape | Every slot is fitted to its camera's own aspect ratio when loaded and when the shape changes. | The preview, editor and stream show the same shape; a circle stays round in any layout. |
| Auto-framing | Windows' `FaceDetector` (on this PC, no download) runs at about 10 Hz on a 480-px grey copy. A pure `AutoFramer` smooths the crop with a dead zone, zoom hysteresis, jump confirmation and hold-then-ease-out. There is one worker per camera and layer, shared by the preview and the stream. | The OS component needs no model or license. The smoothing is unit-tested without a camera. |
| Voice level | An `AudioCaptureHub` opens one WASAPI session per device and fans it out. The stream's microphone input and the voice meter subscribe to the same session. | A second capture would cost CPU and can fail on exclusive-mode devices. |
| Effects timing | Animations use one app-wide clock, so they look the same at any frame rate and identical in the preview and the stream. | |
| Blending | The gameplay is converted to YUV 4:2:0 once. Cameras are scaled, converted to YUVA 4:2:0 and overlaid in YUV. | A BGRA alpha blend of an 844×844 camera in a vertical layout took FFmpeg from 0.98× to 0.7× real time (about 2.8 cores). In YUV it needs about a third of the CPU and holds 60 fps. |

| Camera timestamps | Camera pipes use wall-clock timestamps (`-use_wallclock_as_timestamps 1`), like the live screen capture. | With frame-count timestamps, a camera pipe read even slightly slower than real time drifted behind the capture. The overlay then kept every gameplay frame while waiting for it: FFmpeg grew from 0.4 to 9.6 GB in a 30-minute soak. |
| Layer frame rate | Camera layers are rendered and piped at no more than 30 fps; the overlay repeats the latest camera frame in a 60 fps stream. | Cameras rarely deliver more than 30 fps. Rendering at 60 fps doubled Linkast's CPU (about 2 cores with two cameras and blur) and the pipe bandwidth. Animated effects update 30 times a second. |

## Consequences

- **Pipe bandwidth.** Each camera layer is a separate raw BGRA pipe into FFmpeg at the stream frame rate. Four large cameras at 60 fps are memory-bandwidth heavy. Measured cases are in the Phase 4 report.
- **Stacking order** applies on the next go-live; FFmpeg's overlay chain can't be reordered live.
- **Cameras added while live** appear on the next go-live, and the UI says so.

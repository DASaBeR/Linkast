# Phase 6 report: Linkast Camera, phone webcams, new brand and dark mode

**Status:** Implemented and validated on the development PC. A real Windows 11 install of the Linkast Camera was tested end to end, through both Media Foundation and DirectShow, then removed again. Still open:
- tests with real call apps (Zoom, Teams, Discord, Meet, OBS);
- phone apps other than Iriun.

See [field-test-plan.md](../field-test-plan.md), section G.

**Test machine:** Windows 11 Pro build 26200, i5-12400F, RTX 3070, Iriun Webcam (phone app not connected), Visual Studio 2022 and 18 with Windows SDK 10.0.26100.

## What was built

- **Linkast Camera** ([ADR 0009](../adr/0009-linkast-camera.md)):
  - `native/Linkast.VirtualCamera`: a Media Foundation media source in C++ with WRL and the static CRT. Its structure is adapted from Microsoft's MIT-licensed VirtualCamera sample. It serves 1280×720 at 30 fps in NV12 and RGB32, and shows a branded "Linkast is not running" picture when Linkast isn't sending.
  - `native/LinkastCameraSetup`: an elevated helper. It copies the DLL to `%ProgramFiles%\Linkast\Virtual Camera`, registers it in HKLM, and creates (or removes) a system-wide camera that stays listed when Linkast is closed.
  - A shared-memory protocol between session 0 and the user's session, with a strict security descriptor, double buffering, and heartbeats both ways.
  - The producer in Linkast:
    - renders the chosen camera layer with its background effect and auto-framing, as a plain 16:9 rectangle, unmirrored, with transparency flattened;
    - works only while an app watches;
    - shares the camera session and the effect workers with the preview and the stream;
    - shows a "Camera is off" picture when the layer's camera is off.
  - A Webcam-tab card with:
    - set up, repair or update, and remove (one UAC prompt each);
    - on/off, and which camera layer it shows;
    - a live status ("In use by an app · 1,322 frames sent").
  - Linkast hides its own camera from its camera list, so it can't loop.
- **Phone as webcam:** a guide for Iriun (verified), DroidCam, Camo and Windows 11's connected camera ([phone-as-webcam.md](../phone-as-webcam.md)).
- **New brand.**
  - The "Lk" mark from the brand sheet, rebuilt as vector geometry in `Themes/Brand.xaml`, the single source of the logo.
  - `tools/make-icon.ps1` renders from it: the app icon at every size, the README logos for dark and light backgrounds, and the virtual camera placeholder.
  - A header lockup in the window, and the icon in the title bar, About window and installer.
- **Dark mode by default.** Appearance offers Dark, Light or Match Windows. The choice is saved and applied before the window appears, so it never flashes light. Older settings files open in Dark.

## Validation

| Check | Result |
|---|---|
| Native build, warnings as errors (DLL 206 KB, helper 786 KB) | Pass |
| Install through the helper with UAC | Copied, registered, camera created (`0x00000000`) |
| Listed to apps | "Linkast Camera (Windows Virtual Camera)" in Windows' camera list and in DirectShow |
| Read back through Windows' camera stack (integration test) | 1280×720 NV12 at 30 fps. Navy placeholder when idle (BGR 62/27/23); a magenta frame from Linkast arrived as BGR 255/40/255. |
| Full app as producer, frame grabbed by an ordinary DirectShow client (FFmpeg) | The Iriun picture, blurred: Linkast's processed camera reached the app |
| Producer against a stand-in source (integration test) | Nothing rendered while no client watches; 61 frames in 2 s while watched; stops after the client leaves |
| Protocol unit tests | Layout matches the native header; slot alternation; heartbeat timeout; alpha flattening; virtual camera look |
| Removal through the app's button with UAC | Camera gone from DirectShow and Windows; HKLM entry removed; the DLL still loaded by Frame Server is deleted at the next restart |
| UI | Not installed, set up, in use and removed states; Light and Dark themes; About window; header logo |
| Full suite (Release) | **195 passed**, 0 failed, 0 warnings (opt-in tests skipped) |
| Package | The `vcam\` components and Microsoft's MIT notice are included; the packaged app offers setup |

## Measurements

Linkast's whole-machine CPU while the Linkast Camera is used by an app (1280×720 at 30 fps, Iriun at about 22 fps):

| Background effect | No app watching | An app watching |
|---|---|---|
| Original | 5.4 % | 7.9 % |
| Blur | 5.8–6.9 % | 15.5–15.9 % |

The extra cost with blur comes from compositing the blurred background on the CPU at 720p. A GPU path for camera layers would remove most of it; it's on the roadmap.

## Notes and limitations

- **Windows 11 only.** On Windows 10 Linkast explains why the camera isn't available.
- **Fixed format.** The Linkast Camera is 1280×720 at 30 fps; call apps scale it as they need.
- **Removing the camera leaves files until restart.** A DLL that Windows' camera service still has loaded is deleted at the next restart, and the empty folder goes with it.
- **The scratchpad and `%LOCALAPPDATA%\Linkast` were wiped during this phase,** apparently from outside the app. Nothing in Linkast's code, tests or scripts deletes those folders. Only local test settings were lost; Linkast recreated defaults (dark theme) on the next start.

## Next

- The first public beta release (`v0.2.0-beta.1`, see [releasing.md](../releasing.md)).
- A self-built FFmpeg to close the GPL source gaps.
- Code signing.
- A GPU render path.

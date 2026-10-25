# ADR 0009: Linkast Camera (virtual camera)

**Status:** Accepted (Phase 6)

## Context

Users want their Linkast look (background blur, auto-framing, picture settings) in Zoom, Teams, Discord, Meet and OBS. Windows 11 has a supported virtual camera API: `MFCreateVirtualCamera` with a Media Foundation media source. Windows' camera service (Frame Server) loads that source into its own process.

That has three consequences:

- the source must be registered **machine-wide (HKLM)**, which needs administrator rights;
- the DLL must be readable by the *Local Service* account, so it can't live in a per-user folder;
- the source runs in session 0, so it can't simply share memory with an app in the user's session.

Linkast itself installs per user without admin rights (Phase 5) and must keep doing so.

## Decision

| Concern | Decision |
|---|---|
| Install model | **Optional add-on.** Linkast ships `vcam\Linkast.VirtualCamera.dll` and `vcam\LinkastCameraSetup.exe`. **Set up Linkast Camera** runs the helper with one UAC prompt. The helper copies the DLL to `%ProgramFiles%\Linkast\Virtual Camera`, which is writable only by administrators; that matters because a system service loads it. It then registers the DLL in HKLM and creates the camera. **Remove from this PC** reverses all of it. Linkast never runs elevated. |
| Lifetime | `MFVirtualCameraLifetime_System` and `AllUsers`: "Linkast Camera" stays listed when Linkast is closed and shows a branded "Linkast is not running" picture, so calls never lose their camera device. |
| Media source | Native C++ with WRL and the static CRT. The structure follows Microsoft's MIT-licensed VirtualCamera sample. It offers one stream, 1280×720 at 30 fps, in NV12 and RGB32. Frames are produced in system memory from the shared section, with BGRA to NV12 conversion (BT.601 limited range). |
| Frame transport | A `Global\LinkastVirtualCamera.v1` file mapping. It is created by the media source, because services hold *SeCreateGlobalPrivilege* and normal users don't. Its DACL gives interactive users read/write, and it has a Medium integrity label so the app can write. It holds a 4 KB header and two BGRA slots. The app writes the other slot, then publishes the sequence number; the source copies the newest slot and retries if the sequence moved. |
| Liveness | The source stamps `ConsumerTime` on every frame request. Linkast renders only while that is fresh (an app is watching), and closes the camera feed 3 s and the section 30 s after the last request. Frames older than 1 s make the source show the placeholder. |
| What it shows | One chosen camera layer with its picture, background effect and auto-framing. It is rendered as a plain unmirrored 16:9 rectangle without border, glow or animations, because call apps mirror the self-view themselves, and transparency is flattened onto the brand navy. The camera, segmentation and framing workers are shared with the preview and the stream. |
| Feedback loop | Linkast hides "Linkast Camera …" from its own camera list. |
| Updates | Each install copies the DLL under a new name, because Frame Server may still have the old one loaded; old copies are deleted, or scheduled for deletion at restart. A protocol mismatch is detected and the UI offers **Repair or update**. |

## Consequences

- **Windows 11 only** (build 22000 or later). Windows 10 shows an explanation instead of the button.
- **CPU cost.** It costs something only while an app uses the camera. Measured: +2.5 % of the machine without a background effect and about +10 % with blur (CPU compositing at 720p). A GPU path is the follow-up.
- **The DLL runs inside a Windows service,** so it is deliberately small:
  - it uses no exceptions across COM;
  - it fails closed (a missing or garbled section just shows the placeholder);
  - it is built with warnings as errors.

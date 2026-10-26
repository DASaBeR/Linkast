# Plan: exclusive-fullscreen games

Today the Source tab always warns that exclusive-fullscreen games can capture as black and tells users to switch to borderless ([MainWindow.xaml:191](../src/Linkast.App/MainWindow.xaml)). The goal is to let people keep exclusive fullscreen where Windows allows it. Linkast should notice when the current capture cannot see the game and move to a method that can.

## Ground rules

These apply to every phase. Anything that breaks them is out of scope.

- Capture only through Windows' own APIs: Windows Graphics Capture (WGC) and Desktop Duplication (DDA). Both are used by Windows itself and by every recording tool.
- Never touch the game process. No injection, no hooks, no DLLs loaded into the game, no reading or writing its memory, no overlay drawn inside it. A process handle is opened only with `PROCESS_QUERY_LIMITED_INFORMATION` (the same access Task Manager uses), and only to read the executable path.
- No third-party hook binaries, and no changes to game files or game settings.
- Detection uses read-only OS queries.

With these rules, some games in true exclusive mode will still capture black with both WGC and DDA. For those games, borderless stays the advice, and the app says so only when it applies.

## Background

- Since Windows 10 1803, **fullscreen optimizations** run most DX11 and DX12 "exclusive fullscreen" games as flip-model windows, so WGC usually captures them. True exclusive mode mostly affects DX9, some OpenGL and Vulkan games, and games where the user has turned fullscreen optimizations off.
- DDA reads the image the monitor shows, so it can often see exclusive-fullscreen games that WGC window capture cannot. Its known weak points: it loses access when the display mode changes (`DXGI_ERROR_ACCESS_LOST`), it fails on some hybrid-GPU laptops (`DXGI_ERROR_UNSUPPORTED`), and it captures everything on that monitor, including notifications.
- Linkast already supports DDA for displays (`ddagrab`, [FfmpegArgumentBuilder.cs](../src/Linkast.Engine.FFmpeg/FfmpegArgumentBuilder.cs)). Today any capture error ends the session, because only network errors are retryable ([Telemetry.cs](../src/Linkast.Core/Telemetry.cs), `IsRetryable`). So a game switching display mode while DDA is active currently fails the stream.
- The layout preview uses GDI ([ScreenSnapshot.cs](../src/Linkast.Platform.Windows/Capture/ScreenSnapshot.cs)), so it cannot tell us whether the stream is black.

## Phase 0: measure (deferred)

**Decision:** no testing with games for the first release. Phases 1–4 are built from the Windows documentation and the FFmpeg 9.0.1 source, and tested without games:

- **DDA access lost:** change the display resolution, press Win+L, or open a UAC prompt while live.
- **Fullscreen detection:** use a borderless window that fills the monitor.
- **Black probe:** capture a solid black window.

Later, fill in the game matrix in [capture-compatibility.md](capture-compatibility.md) before changing defaults. Add these rows and columns to it:

- APIs: DX9, DX11, DX12, Vulkan and OpenGL titles in exclusive fullscreen, with fullscreen optimizations on and off.
- Capture methods: WGC window, WGC display and DDA for each title.
- Events: Alt+Tab out and back, the game set to a resolution other than the desktop's, and switching between windowed and fullscreen while live.
- Machines: a hybrid-GPU laptop and an HDR monitor.

**Exit:** we know which combinations go black and whether DDA fixes them. That decides whether Phase 5 and the Phase 3 opt-in button are worth doing.

## Phase 1: detect fullscreen (Platform.Windows)

Add a `FullscreenDetector` next to `VideoSourceCatalog` in `Capture/`:

- **System-wide exclusive mode:** `SHQueryUserNotificationState` returns `QUNS_RUNNING_D3D_FULL_SCREEN`.
- **The selected window fills its monitor:** the window is in the foreground, `GetWindowRect` equals the `rcMonitor` from `MonitorFromWindow`, and it has no caption or border styles. This reports "fullscreen" (borderless or exclusive) and the monitor it is on.
- **Fullscreen optimizations turned off:** get the executable path with `QueryFullProcessImageName`, then read the per-app entry under `Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers` in HKCU and HKLM and look for `DISABLEDXMAXIMIZEDWINDOWEDMODE`.
- Polling runs every 2 seconds, only while the Source tab is open or a stream is live. This matches the existing snapshot timer.
- Result: `FullscreenState(Kind: None | Fullscreen | Exclusive, MonitorIndex, MonitorHandle, OptimizationsDisabled)`.
- Keep the geometry and flag parsing in pure functions so they can be unit-tested without a real game.

## Phase 2: detect a black stream (Engine.FFmpeg)

- Add a small probe branch to the filter graph: split, scale to about 32×18, `blackdetect=d=2:pix_th=0.02`, then `nullsink`. Parse `black_start` and `black_end` from stderr and publish them as a `CaptureLooksBlack` flag in telemetry.
- **Check first:** where in the graph frames are still on the GPU. On the zero-copy NVENC path, scale on the GPU (`scale_d3d11`) before a tiny `hwdownload`. Measure the cost; the target is under 0.5 % CPU at 1080p60.
- Loading screens are often black. Act only when the probe says black for more than 3 seconds **and** Phase 1 reports fullscreen for the captured window.
- **Optional pre-flight:** when the user selects a window that Phase 1 reports as exclusive, run a 2-second test capture with the same source and the probe, and show the result before they go live.

## Phase 3: targeted guidance and a one-click switch (App)

- Replace the always-on warning with a hint that appears only when Phase 1 or Phase 2 says there is a problem with the selected source.
- **Action, "Capture this display instead":** sets the source kind to Display, selects the game's monitor, and turns on Desktop Duplication. While live, this is a confirmed restart, the same pattern used for layout changes that need one. This one-click switch is the only fallback in the first release.
- **Privacy warning (required):** before switching, the user confirms a clear warning that everything on that monitor may appear on stream, including notifications, chat and other windows. The warning stays visible as a hint for as long as display capture is in use after the switch. Without that confirmation, nothing switches.
- **When fullscreen optimizations are off for the game:** only explain how to turn them back on (the game's .exe → Properties → Compatibility → clear "Disable fullscreen optimizations"). **Decision:** Linkast never changes another app's compatibility settings. An explicit opt-in button may be considered in a later release, after its behaviour has been checked.
- **Last resort**, when both methods are known to fail: "This game can't be captured in exclusive fullscreen. Switch it to borderless or windowed."

## Phase 4: make Desktop Duplication survive fullscreen switches (Engine.FFmpeg, Core)

- **Access lost:** add a `capture.dda.accesslost` rule to [ErrorClassifier.cs](../src/Linkast.Engine.FFmpeg/ErrorClassifier.cs), placed before the general `capture.dda` rule. Take the exact needles from the `ddagrab` source in FFmpeg 9.0.1, not from guesses. Make only this code retryable, with its own short backoff (about 250 ms, a few attempts) instead of the network backoff. The FFmpeg restart should leave a gap of 1–2 seconds; check that recording continues the same way it does after a network reconnect.
- **Resolution changes:** when the game sets a mode other than the desktop's, DDA frames change size. Make sure the graph always scales and pads to the output canvas so the encoder size never changes. Verify whether `ddagrab` keeps running through the change or errors out; if it errors, the access-lost path covers it.
- **Hybrid-GPU laptops:** classify `DXGI_ERROR_UNSUPPORTED` from `ddagrab` as its own code. The message points the user to Settings → System → Display → Graphics, where they can set Linkast to the GPU that drives the display, or use WGC instead.
- **HDR:** check which pixel format `ddagrab` outputs on an HDR monitor, and convert it correctly.

## Phase 5: automatic mode (deferred)

**Decision:** not in the first release. Reconsider only after the Phase 0 tests show that WGC and DDA are both reliable.

- Add a "Game (automatic)" source: capture the window with WGC. When Phase 1 reports exclusive mode and Phase 2 reports black, switch to DDA on that monitor; switch back when the game is windowed again.
- Each switch restarts FFmpeg with a gap of 1–2 seconds. Limit switching (for example, at most once every 10 seconds) so Alt+Tab doesn't cause a loop.
- Off by default until field tests confirm it works.

## Phase 6: tests and docs

- Unit tests: detector geometry and flag parsing, the new classifier rules, retry rules for `capture.dda.accesslost`, and the probe branch in the argument builder.
- Integration test: a DDA session that receives an access-lost error recovers and stays live.
- Update [risks.md](risks.md), [compatibility-matrix.md](compatibility-matrix.md), [field-test-plan.md](field-test-plan.md) and the known-issues line in the release notes.

## Order

| Step | Size | Depends on |
|---|---|---|
| Phase 1: detect fullscreen | Small | none |
| Phase 4: DDA recovery | Medium | FFmpeg 9.0.1 `ddagrab` source for the exact error text |
| Phase 3: guidance, warning and switch | Small | Phase 1 |
| Phase 2: black probe | Medium | none, cost measured first |
| Phase 6: tests and docs | Ongoing | each phase |
| Phase 0: measure with games | Small, needs real games | deferred |
| Phase 5: automatic mode | Medium | deferred until Phase 0 |

Phases 1, 3 and 4 alone replace the blanket warning with a targeted hint and a working fallback. Phase 2 can follow in the same release.

## Decisions

1. **Fullscreen optimizations:** explanation only. Linkast does not change another game's compatibility settings. An explicit opt-in button may be considered in a future release, after its behaviour has been checked.
2. **Automatic switching:** deferred. The one-click "Capture this display instead" is enough for the first release, and it must clearly warn that notifications and other windows on that monitor may appear on stream.
3. **Game testing:** none for now. Development and tests use the game-free methods listed under Phase 0.

# Linkast Stability and Architecture Hardening Report

> **Author**: Gemini Assistant & Antigravity  
> **Date**: 2026-11-01  
> **Scope**: Engine Stability, FFmpeg Process Orchestration, Ingest Reconnects, ECC 2.2.3 Customization  
> **Target**: Linkast (.NET 10, WPF, Vortice DirectX 11, FFmpeg)  

---

## Executive Summary

Following analysis of real-world production logs (`linkast-20261029-5.log` and `linkast-20261030.log`), three primary stability and reliability failure modes were identified, resolved, and verified against Linkast's strict automated test suite:

1. **Audio Source Limit Enforcement & Pre-Flight UI Safety**: Prevented engine crashes caused by exceeding audio source limits (`[Audio/audio.count]`), introducing pre-flight validation before engine initialization and dynamic UI warning banners.
2. **FFmpeg Demuxing & Muxing Queue Clamping**: Fixed out-of-memory crashes (`Cannot allocate memory`) caused by unbounded muxing buffer accumulation when game FPS or GPU encoding speed dropped below real time (<1.0x), and classified memory exhaustion errors cleanly.
3. **Rapid Re-Publishing & Ingest "Already Publishing" Grace Period**: Addressed RTMP server socket collisions (`Server error: Already publishing` / `Operation not permitted`) during quick stop-and-restart cycles, ensuring clean classification and backoff.

In addition, the workspace's Everything Claude Code (ECC) installation was audited, targeted, and optimized specifically for Linkast's C#/.NET 10 + C++ stack.

---

## 1. Problem Analysis & Root Cause Investigation

### Incident A: Audio Limit Collision (`linkast-20261029-5.log`)

* **Log Evidence**:
  ```text
  [2026-10-29 17:56:12.441] [ERR] Linkast.Engine.FFmpeg.FfmpegStreamEngine: Stream failed: [Audio/audio.count] At most 4 sounds can go into a stream.
  ```
* **Root Cause**:
  In earlier builds, `SessionValidator.MaxAudioSources` was hardcoded to 4. When a streamer configured Desktop Audio, Microphone, Game Audio, and two web overlays (alert box + chat overlay), the session contained 5 audio inputs. When "Go Live" was clicked:
  1. `MainViewModel.StartSessionAsync` immediately invoked `_engine.StartAsync(session)`.
  2. The engine transitioned from `Idle` to `Preparing`.
  3. `SessionValidator.Validate(session)` failed inside `FfmpegStreamEngine.StartAsync`, immediately transitioning to `StreamState.Failed` and throwing a `StreamFailureException`.
  4. The user experienced a jarring state crash loop on every attempt to go live.
* **Resolution**:
  - `SessionValidator.MaxAudioSources` was expanded to 16 in Core.
  - Implemented pre-flight validation in `MainViewModel.BuildSession(bool, out StreamError?)`. Validation failures now prevent engine startup entirely, cleanly populating `LastError` so the UI presents the error and fix action without a state crash.
  - Added real-time checking in `MainViewModel.Scenes.cs` (`RefreshMixerChannels()`), displaying an informative amber warning banner (`WarningText`) in `MainWindow.xaml` before the user even clicks "Go Live".

---

### Incident B: FFmpeg Memory Exhaustion at Low Encoder Speed (`linkast-20261030.log`)

* **Log Evidence**:
  ```text
  [2026-10-30 19:40:00.159] [INF] Activity: [aist#6:1/aac @ 000001bf3ad31e40] [warning] Resumed reading at pts 444.661 with rate 1.050 after a lag of 226.243s
  [2026-10-30 19:40:50.822] [DBG] ffmpeg: [in#8/rawvideo @ 000001bf3ab2bb40] [error] Error during demuxing: Cannot allocate memory
  [2026-10-30 19:40:51.568] [DBG] ffmpeg: [fc#0 @ 000001bf37cb4300] [error] Error while filtering: Cannot allocate memory
  [2026-10-30 19:40:53.604] [INF] Live, last 60 s: 2058 frames (34.3 fps), encoder speed 0.64x, sending 3514 kbps; game window sent 5 fps
  ```
* **Root Cause**:
  During heavy gameplay, the game window dropped to 5 FPS and the NVENC encoder speed dropped to 0.64x real-time speed.
  Over 7 minutes of streaming, real-time inputs (named pipes for raw video and audio) and looping background video/audio inputs drifted by **246.3 seconds**.
  Because FFmpeg had no upper bound on muxing queue depth (`-max_muxing_queue_size`), filter graphs and muxer queues buffered uncompressed raw frames and audio samples to compensate for the timestamp drift until process virtual memory was completely exhausted.
  Furthermore, `ErrorClassifier` had no entry for `Cannot allocate memory`, degrading the failure into a generic opaque process exit code.
* **Resolution**:
  - In `FfmpegArgumentBuilder.cs`, clamped the muxing queue depth by injecting `-max_muxing_queue_size 1024` into output argument generation. This bounds buffer allocations across single FLV streaming, Matroska recording, and `tee` dual-muxer configurations.
  - In `ErrorClassifier.cs`, added a designated `process.memory` classification rule mapping `Cannot allocate memory` to an actionable error explaining memory exhaustion and suggesting resolution/background reductions.

---

### Incident C: Rapid Re-Publishing Socket Collisions (`linkast-20261030.log`)

* **Log Evidence**:
  ```text
  [2026-10-30 19:24:45.312] [DBG] ffmpeg: [rtmp @ 00000224d441aa80] [error] Server error: Already publishing
  [2026-10-30 19:24:45.313] [DBG] ffmpeg: [flv @ 00000224d441ba00] [error] Error opening output rtmp://... Operation not permitted
  ```
* **Root Cause**:
  When a user stopped a stream and restarted within 2–5 seconds, the remote RTMP ingest server had not yet closed its TCP socket or reaped the stream session.
  FFmpeg exited with `Server error: Already publishing`. In `ErrorClassifier.cs`, `"Server error"` was mapped to `"network.rejected"` ("The server rejected the stream. Check the stream key.").
  Because `FfmpegStreamEngine` specifically keyed its `keyStillHeld` grace period on `ClosedEarlyCode` (`"network.closed.early"`), the engine treated the collision as a permanent key rejection rather than a transient key-holding condition.
* **Resolution**:
  - In `ErrorClassifier.cs`, added a rule before generic `"network.rejected"`:
    ```csharp
    new(ErrorCategory.Network, "network.closed.early", "The server is still closing the previous connection with this stream key. Linkast will retry in a moment.",
        ["Already publishing", "Server error: Already publishing", "Publishing already in progress"])
    ```
  - In `FfmpegStreamEngine.cs`, updated the reconnect heuristic so that `isAlreadyPublishing` triggers `keyStillHeld = true`, guaranteeing a 10-second `KeyStillHeldDelay` backoff window and surfacing:
    > *"The server is still closing the previous connection with this stream key. Linkast tries again in a few seconds."*

---

## 2. Code Changes Summary

| Project | File | Description of Modification |
|---|---|---|
| `Linkast.App` | `src/Linkast.App/ViewModels/MainViewModel.cs` | Pre-flight validation in `BuildSession` via `SessionValidator.Validate(session)` before engine initialization. |
| `Linkast.App` | `src/Linkast.App/ViewModels/MainViewModel.Scenes.cs` | Dynamic warning banner evaluation in `RefreshMixerChannels()` when audio channels exceed `MaxAudioSources`. |
| `Linkast.Engine.FFmpeg` | `src/Linkast.Engine.FFmpeg/FfmpegArgumentBuilder.cs` | Added `-max_muxing_queue_size 1024` to clamp output buffering under low encoder speeds. |
| `Linkast.Engine.FFmpeg` | `src/Linkast.Engine.FFmpeg/ErrorClassifier.cs` | Added `process.memory` for `Cannot allocate memory` and `network.closed.early` for `Already publishing`. |
| `Linkast.Engine.FFmpeg` | `src/Linkast.Engine.FFmpeg/FfmpegStreamEngine.cs` | Bound `isAlreadyPublishing` directly to `keyStillHeld` delay policy. |
| `Linkast.Engine.FFmpeg.Tests` | `tests/Linkast.Engine.FFmpeg.Tests/EngineComponentTests.cs` | Added unit tests verifying `-max_muxing_queue_size 1024`, `Already publishing`, and `Cannot allocate memory` classifications. |

---

## 3. ECC 2.2.3 Optimization Audit

Prior to these fixes, an audit of the ECC v2.2.3 installation in `.agents/` revealed 122 rules (over 80% for unrelated stacks like PHP, Rust, Angular, Kotlin, Ruby) and 68 agents.

Using safe universal lifecycle commands and synchronization:
* **Rules**: Retained only 24 relevant rules (`common-*`, `csharp-*`, `cpp-*`, and 4 Linkast architectural rules: capture policy, FFmpeg lifecycle, security/privacy, and WPF concurrency).
* **Subagents**: Retained 19 specialized desktop/native development agents (C# reviewer, C++ reviewer, architect, build resolver, performance optimizer).
* **Skills**: Retained 16 targeted skills; removed web/cloud-specific frameworks.
* **Workflows**: Deleted dead workflow definitions.
* **Health Check**: Verified clean via `npx ecc-universal doctor --target antigravity` (0 errors, 0 warnings).

---

## 4. Verification & Quality Gates

The changes were subjected to Linkast's strict verification loop:

1. **Compilation (Debug & Release)**:
   - Tool: `dotnet build Linkast.slnx -c Debug` & `dotnet build Linkast.slnx -c Release`
   - Warnings as Errors: `TreatWarningsAsErrors=true`
   - Outcome: **0 Warning(s), 0 Error(s)**
2. **Automated Unit Test Suite**:
   - Tool: `tools/run-tests.ps1 -UnitOnly`
   - Executed against all projects: `Linkast.Core.Tests`, `Linkast.Networking.Tests`, `Linkast.Effects.Tests`, `Linkast.Engine.FFmpeg.Tests`, `Linkast.App.Tests`
   - Outcome: **395 passed, 0 failed, 7 skipped (100% PASS)**
   - Report generated: `docs/reports/test-runs/2026-11-01-0040-5bbe3c7.md`

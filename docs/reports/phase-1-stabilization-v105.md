# Phase 1 Stabilization Report — Linkast v1.0.5

> **Date**: 2026-11-01  
> **Status**: Verified PASS (398 passed, 0 failed, 7 skipped)  
> **Scope**: Capture Selection Decoupling & Debouncing, Minimized/Zero-Frame Window Guard, Bounded Output Latency, and Stream Health Telemetry.

---

## 1. Executive Summary

Phase 1 stabilizes Linkast's existing dual-FFmpeg streaming engine prior to the Direct3D 11 GPU compositor migration. It addresses the four critical failure modes identified during real-world tester logging:

1. **Capture Selection Decoupling & Debouncing**: Decoupled pending capture selection from scene mutation, extended settle debounce to 1000 ms, and eliminated redundant restarts when returning to the active source.
2. **Minimized & Inactive Window Guard**: Introduced Win32 `IsIconic` checks and client rect validation in `VideoSourceCatalog`. Minimized or non-painting windows (such as background Chrome) no longer stall FFmpeg's startup for 20 seconds with `"could not find codec parameters"`. When restored, capture automatically reattaches without user intervention.
3. **Bounded Output Latency**: Clamped `BufferedOutputLease.MaxBufferedBytes` from 64 MiB down to 16 MiB (~20 seconds at 6 Mbps), preventing the runaway 90-to-170 second latency traps observed on bandwidth-constrained connections (e.g. mobile hotspots).
4. **Stream Health Telemetry & Actionable Guidance**: Exposed live `BufferedBytes` via `NetworkSnapshot` and hooked `_engine.DiagnosticLine` to display real-time warning banners in `MainWindow.xaml` with tailored bitrate suggestions.

---

## 2. Code Changes Summary

| Project | File | Method / Component | Modification |
|---|---|---|---|
| `Linkast.Platform.Windows` | [`VideoSourceCatalog.cs`](file:///d:/Programming/Windows-WPF/Linkast/src/Linkast.Platform.Windows/Capture/VideoSourceCatalog.cs) | `IsIconic`, `IsWindowCapturable` | Added P/Invoke `IsIconic(hwnd)` and `IsWindowCapturable` checking existence, minimization state, and positive client dimensions. |
| `Linkast.App` | [`MainViewModel.LiveSource.cs`](file:///d:/Programming/Windows-WPF/Linkast/src/Linkast.App/ViewModels/MainViewModel.LiveSource.cs) | `LiveSourceSettle`, `OnCaptureCardChanged`, `Resolve`, `UpdateWindowWatch` | Decoupled `_pendingCapture` from scene mutation; increased settle window to 1000 ms; fallback to blank canvas on minimized window with `SceneCaptureNote`; auto-restore when window is un-minimized. |
| `Linkast.App` | [`MainViewModel.cs`](file:///d:/Programming/Windows-WPF/Linkast/src/Linkast.App/ViewModels/MainViewModel.cs) | `DiagnosticLine`, `OnTelemetry` | Surfaced upload backlog warnings to `WarningText` with dynamic bitrate recommendations; auto-cleared when backlog drops $< 1$ MB. |
| `Linkast.Core` | [`Telemetry.cs`](file:///d:/Programming/Windows-WPF/Linkast/src/Linkast.Core/Telemetry.cs) | `NetworkSnapshot` | Added `long BufferedBytes = 0` to track socket buffer depth in telemetry snapshots. |
| `Linkast.Engine.FFmpeg` | [`BufferedOutputLease.cs`](file:///d:/Programming/Windows-WPF/Linkast/src/Linkast.Engine.FFmpeg/BufferedOutputLease.cs) | `MaxBufferedBytes`, `_warnAt`, `Snapshot` | Clamped queue ceiling from 64 MiB to 16 MiB; lowered warning threshold to 4 MiB (~5 s); populated `BufferedBytes` in `Snapshot()`. |
| `Linkast.App.Tests` | [`LiveCaptureDebounceTests.cs`](file:///d:/Programming/Windows-WPF/Linkast/tests/Linkast.App.Tests/LiveCaptureDebounceTests.cs) | Unit test suite | Added unit tests validating `IsWindowCapturable`, `IsWindowMinimized`, and invalid handle safety. |

---

## 3. Verification & Quality Gates

* **Compilation**: `dotnet build Linkast.slnx -c Release` $\to$ **0 Warning(s), 0 Error(s)** (`TreatWarningsAsErrors=true`).
* **Unit Tests**: `tools/run-tests.ps1 -UnitOnly` $\to$ **398 passed, 0 failed, 7 skipped (100% PASS)**.
* **Test Report**: [`docs/reports/test-runs/2026-11-01-1229-ec01a4b.md`](file:///d:/Programming/Windows-WPF/Linkast/docs/reports/test-runs/2026-11-01-1229-ec01a4b.md).

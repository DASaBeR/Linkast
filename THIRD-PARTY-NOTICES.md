# Third-party notices

Linkast is licensed under GPL-3.0-or-later. It uses the components below. Versions are those pinned in the repository at the time of writing; the project files are authoritative.

## Bundled or invoked programs

| Component | Version | License | Notes |
|---|---|---|---|
| FFmpeg | 9.0.1 "essentials_build" from gyan.dev, pinned by SHA-256 (`tools/fetch-ffmpeg.ps1`) | GPL-3.0-or-later as built (enables libx264, libx265 and other GPL components) | Run as a separate process. Shipped in `ffmpeg\` with its LICENSE, README (library versions) and BUILD-INFO (configure line, hashes, source). The corresponding source (FFmpeg plus each statically linked library at the built version) is published with each release as `ffmpeg-9.0.1-corresponding-source.zip`; the few libraries whose exact source the build doesn't identify are listed in its `SOURCES.txt`. See [docs/licensing.md](docs/licensing.md). |
| Velopack (installer and updater, including its native `Update.exe`) | 1.2.161 | MIT | https://github.com/velopack/velopack |

## Code adapted from other projects

| Source | License | Where |
|---|---|---|
| Microsoft Windows-Camera, VirtualCamera sample (github.com/microsoft/Windows-Camera, Samples/VirtualCamera), Copyright (C) Microsoft Corporation | MIT | The structure of the Linkast Camera media source in `native/Linkast.VirtualCamera`: media source, stream and activator, stream attributes, sample allocator handling and virtual camera registration. Rewritten for Linkast with WRL instead of C++/WinRT and WIL. |

## AI models

| Model | Version | License | Notes |
|---|---|---|---|
| RobustVideoMatting, MobileNetV3, fp32 (Lin, Ryabtsev, Sengupta, Curless, Seitz, Kemelmacher-Shlizerman, 2021) | v1.0.0 release, SHA-256 `88D4531297118F595BF2FD60F6F566AEC2E559393802D1F436C380F0CBBD2828` | GPL-3.0 | Shipped in `models/` with `RVM-LICENSE.txt`. Source: https://github.com/PeterL1n/RobustVideoMatting |

## NuGet packages

| Package | License |
|---|---|
| NAudio.Wasapi, NAudio.Core | MIT |
| Microsoft.ML.OnnxRuntime.DirectML 1.24.4 (ONNX Runtime) | MIT. The DirectML redistributable it references is deliberately **not** shipped; Linkast uses the DirectML that is part of Windows (ADR 0007). |
| NetMQ | LGPL-3.0 (used unmodified as a library; compatible with GPL-3.0) |
| NaCl.Net (dependency of NetMQ) | MPL-2.0 |
| AsyncIO (dependency of NetMQ) | MPL-2.0 (verified in the upstream repository github.com/somdoron/AsyncIO, LICENSE.md; the package metadata declares none) |
| CommunityToolkit.Mvvm | MIT |
| Velopack | MIT |
| Microsoft.Extensions.Hosting, Microsoft.Extensions.Logging.Abstractions | MIT |
| Serilog.Extensions.Hosting, Serilog.Sinks.File | Apache-2.0 |
| xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk (tests only) | Apache-2.0 / MIT |
| Xunit.SkippableFact (tests only) | MS-PL |

All of the above are confirmed compatible with distribution under GPL-3.0-or-later. Test-only packages are not shipped.

Camera capture uses the Windows `Windows.Media.Capture` APIs and the stream key uses Windows DPAPI; both are part of the operating system and need no redistribution.

## Development tools (not distributed)

| Tool | License |
|---|---|
| MediaMTX (`bluenviron/mediamtx:1.21.2`, used as a local test ingest) | MIT |

<p align="center">
  <img alt="Linkast" src="src/Linkast.App/Assets/linkast-logo.png" width="180">
</p>

# Linkast

**Go live in a few clicks, over the network connection you choose.**

Linkast is a lightweight, open-source live-streaming app for Windows. It captures your screen, a window or a game plus your audio, encodes it on your own PC with FFmpeg, and streams it to any RTMP or RTMPS server: Twitch, YouTube, a restream service, or your own server.

Linkast is deliberately small. It is not a production studio like OBS. There are no scenes, transitions or plugins. It focuses on starting a stream quickly, keeping it healthy, and telling you clearly what is wrong when it isn't.

> **Status: preview.** Streaming, the Webcam Studio (background effects, auto-framing, multiple cameras, vertical layouts) and network selection work end to end. An installer is next on the [roadmap](docs/roadmap.md). Tests that need real users and hardware are listed in [docs/field-test-plan.md](docs/field-test-plan.md).

## Webcam Studio

- Pick a camera and see a live preview. The camera is opened once and shared by the preview and the stream.
- Shape it: rectangle, rounded or circle, with a border, zoom, crop and mirror.
- Improve the picture: brightness, contrast, saturation, sharpening, noise reduction and low-light boost, plus the camera's own exposure, focus and white balance when it has them.
- Blur, remove or replace your background with no green screen. A local AI model runs on your graphics card through Windows' DirectML, or on the CPU, and the app shows which. Your camera picture never leaves your PC for this.
- Auto-framing keeps your face in the picture, smoothly and without jitter, using Windows' own face detection on your PC.
- Animated borders (pulse, colour cycle), a soft glow, and effects that react to your voice through the microphone you already stream with.
- Up to four cameras, each with its own look and effects. Use one camera twice, for example a close-up and a wide shot; it is opened only once.
- Drag and resize them on top of your game in a layout editor that matches the stream pixel for pixel. Move or hide a camera while live; the stream does not restart.
- Landscape (16:9) and vertical (9:16, 1080×1920) layouts, each remembering its own positions, for Twitch and YouTube as well as Shorts, TikTok and Reels.
- **Linkast Camera** (Windows 11): use your Linkast look in Zoom, Teams, Discord, Meet or OBS. It is an optional add-on that Windows asks your permission to install once.
- Use your phone as a webcam with Iriun, DroidCam, Camo or Windows 11's connected camera; see [docs/phone-as-webcam.md](docs/phone-as-webcam.md).
- Dark mode by default, with light mode or matching Windows one click away.
- Start from a preset (Gaming, Just Chatting, Professional, Low Light, Minimal) or save your own.
- "Webcam only" mode for just-chatting streams.
- Camera off is one click and releases the camera immediately. Camera problems never stop your stream.
- Everything is remembered between runs. The stream key is saved encrypted for your Windows account.

## What makes it different

- **You pick the network for your stream.** Play over Ethernet and stream over a tethered phone or 5G modem, or the other way round. Only Linkast's stream is bound to the network you choose; your game keeps Windows' normal route. Linkast verifies the network actually used, never silently switches to another one, and never changes Windows network settings or needs admin rights. **Test connection** shows exactly which network reached your server.
- **Honest health.** "FFmpeg is running" is never treated as "you are live". Linkast is only *Live* when frames are being encoded **and** bytes are reaching the server, measured on the real socket.
- **Clear errors.** Capture, audio, encoding and network problems are reported separately, with a plain-language fix.
- **Bounded reconnect.** Network drops are retried with backoff, a limited number of times.
- **Local only.** No account, no cloud, no telemetry. Your stream goes straight from your PC to the server you choose.
- **Your stream key stays secret.** It is masked in the UI and removed from every log line and diagnostic report.

## Install

Download `LinkastApp-win-Setup.exe` from the [latest release](https://github.com/DASaBeR/Linkast/releases) and run it. It installs for your Windows account only, needs no admin rights, and adds Start-menu and Desktop shortcuts. Beta builds are not code-signed yet, so Windows SmartScreen may warn: choose *More info* › *Run anyway*. A portable zip is also attached to each release.

Linkast looks for a newer version once when it starts and when you press **Check for updates**. A new version is only mentioned next to that button, and installs only when you press **Update now**. Nothing else is ever sent.

## Requirements

- Windows 10 version 1903 or later, or Windows 11, 64-bit.
- An NVIDIA, AMD or Intel GPU for hardware encoding is recommended. A software (x264) fallback is always available.

## Building from source

Prerequisites: the [.NET 10 SDK](https://dotnet.microsoft.com/download) and FFmpeg. Releases bundle a pinned FFmpeg 9.0.1 "essentials" build from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/); `tools/fetch-ffmpeg.ps1` downloads and verifies exactly that build.

```powershell
powershell -ExecutionPolicy Bypass -File tools/fetch-ffmpeg.ps1   # pinned FFmpeg, hash-verified
$env:LINKAST_FFMPEG = "$PWDrtifactsfmpeg\9.0.1fmpeg.exe"
powershell -ExecutionPolicy Bypass -File tools/fetch-models.ps1   # background-effect model, hash-verified
dotnet build Linkast.slnx -c Release
dotnet run --project src/Linkast.App
```

To build the app folder and zip, plus the installer and update packages with `-Installer` (all into `artifacts/`):

```powershell
powershell -ExecutionPolicy Bypass -File tools/publish.ps1 -Installer
```

Releases are built by GitHub Actions from a version tag; see [docs/releasing.md](docs/releasing.md).

Linkast looks for FFmpeg in this order: the `LINKAST_FFMPEG` environment variable, `ffmpeg\ffmpeg.exe` next to `Linkast.exe`, then `PATH`.

## Running the tests

```powershell
dotnet test Linkast.slnx
```

Unit tests always run. Integration tests stream to a real local RTMP server and are skipped unless it is running:

```powershell
docker compose -f tools/test-ingest/docker-compose.yml up -d
dotnet test tests/Linkast.IntegrationTests
```

## Project layout

| Path | Purpose |
|---|---|
| `src/Linkast.Core` | Domain model, engine interface, state machine, validation, redaction. No Windows dependencies. |
| `src/Linkast.Engine.FFmpeg` | FFmpeg location, encoder probing, argument building, process supervision, progress and error parsing. |
| `src/Linkast.Networking` | Local relay that owns the outgoing connection and measures it. |
| `src/Linkast.Platform.Windows` | Display/window enumeration, WASAPI audio capture, WinRT camera capture and the camera layer pipe. |
| `src/Linkast.App` | WPF user interface. |
| `docs/` | Architecture, decisions, risks, licensing, roadmap. |

Start with [docs/architecture.md](docs/architecture.md).

## License

Linkast is free software, licensed under the [GNU General Public License v3.0 or later](LICENSE). It runs FFmpeg as a separate program; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and [docs/licensing.md](docs/licensing.md).

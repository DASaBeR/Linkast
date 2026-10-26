# Capture and streaming compatibility

## Test machine

| Item | Value |
|---|---|
| OS | Windows 11 Pro, build 26200 |
| GPU | NVIDIA GeForce RTX 3070, driver 32.0.16.1088 |
| FFmpeg | 9.0.1 full_build (gyan.dev) |
| Ingest | MediaMTX 1.21.2 in Docker, via 127.0.0.1 |

## Encoder probe

| Encoder | Result |
|---|---|
| h264_nvenc | Works |
| h264_mf | Works (Constrained Baseline profile) |
| libx264 | Works |
| h264_amf | Fails: amfrt64.dll missing (no AMD GPU) |
| h264_qsv | Fails: no MFX session (no Intel GPU) |

## Milestone 1 soak run (2026-10-25)

Display 1 at 1920x1080 through Windows Graphics Capture, system audio by WASAPI loopback, NVENC p5 CBR at 6000 kbps, 1080p60, through the relay to MediaMTX.

| Metric | Result |
|---|---|
| Duration live | 30 min 0 s |
| Time to Live | 2 s |
| Encoded FPS | average 59.93, one-second minimum 47.0 |
| Encoder speed | 1.00× throughout |
| Upload measured on socket | average 6009 kbps, 1.35 GB sent |
| Dropped or duplicated frames | 0 |
| Reconnects | 0 |
| Output time minus uptime | −1.7 s at 1 min and −1.7 s at 30 min. A constant startup offset, with no drift. |
| Stop | Clean (Stopping, then Stopped) |

## Automated integration tests

| Scenario | Result |
|---|---|
| Test pattern and tone, live and then a clean stop under 6 s | Pass |
| Ingest container restarted mid-stream: Reconnecting, then Live again | Pass |
| Refused port: network.refused after bounded retries, then Failed | Pass |
| Stream key never appears in diagnostics | Pass |
| Real system audio and microphone pipes, live through 6 s of silence, output time tracks wall clock | Pass |

## Manual UI run

Driven through UI Automation: display capture, system audio and NVENC to MediaMTX. The stream went live, ffprobe read h264 1920x1080 and aac, Stop ended FFmpeg within 0.6 s, and the log contained zero occurrences of the stream key.

## Resource use (packaged build, Intel Core i5-12400F, 12 threads)

Measured over 20 s while live at 1080p60 and 6 Mbps, capturing a mostly static desktop with system audio. Low motion understates the x264 cost; repeat during real gameplay.

| Encoder | Linkast + FFmpeg CPU (whole machine) | GPU video-encode engine |
|---|---|---|
| NVENC (p5) | 5.3 % | 60 % |
| x264 (veryfast) | 11.0 % | 0 % |

## Crash cleanup

Linkast was force-killed while live. Its FFmpeg child exited immediately because of the job object, and no ffmpeg.exe was left behind.

## Fullscreen capture checks (2026-10-26)

These checks were made without games, as decided in the [plan](fullscreen-capture-plan.md).

| Check | Result |
|---|---|
| Fullscreen detection on a real borderless window that fills the monitor, a normal window and a minimized one | Fullscreen, None, None |
| "Disable fullscreen optimizations" flag read from AppCompatFlags\Layers (set on a test program only, then removed) | Read correctly |
| Black probe graph on FFmpeg 9.0.1: 3 s black, then a picture | Black start at 0 s, end at 3 s (integration test) |
| Window capture (WGC) of a black borderless window, through the engine | Telemetry reports black; a window with a picture does not (integration test) |
| Probe cost at 1080p60 | Within measurement noise (±0.4 s CPU over 20 s) |
| Hint in the app: shows for a fullscreen window with optimizations off, stays after it minimizes | Verified through UI Automation |
| "Capture this display instead": warning dialog, then Entire display with the privacy note | Verified through UI Automation |
| Desktop Duplication restart after access loss | Error text taken from the FFmpeg 9.0.1 source and covered by unit tests. **Not yet triggered for real** (resolution change, Win+L or a UAC prompt while live). |
| Exclusive fullscreen (`QUNS_RUNNING_D3D_FULL_SCREEN`) | **Not yet seen for real**; needs a game or a Direct3D exclusive-mode test app. |

## Still to do (Milestone 1 exit)

- [ ] One real platform session (Twitch or YouTube, RTMPS) for 30 minutes. Needs the maintainer's stream key.
- [ ] Deliberate interruption on a real connection, such as unplugging Ethernet for 10 s.
- [ ] A/V sync clap test over 30 minutes.
- [ ] Game matrix below.

| Title / API | Mode | WGC display | WGC window | Desktop Duplication | Notes |
|---|---|---|---|---|---|
| DX11 title | Borderless | | | | |
| DX11 title | Exclusive fullscreen | | | | |
| DX12 title | Borderless | | | | |
| DX12 title | Exclusive fullscreen | | | | |
| Vulkan title | Borderless | | | | |
| Title with anti-cheat | Any | | | | |

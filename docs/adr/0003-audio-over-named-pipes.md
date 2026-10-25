# ADR 0003: Audio captured by Linkast and served to FFmpeg over named pipes

**Status:** Accepted (Milestone 1)

## Context

Gamers need system audio. FFmpeg on Windows can read microphones through DirectShow, but it has no WASAPI loopback input, so it cannot capture "what you hear".

## Decision

Linkast captures every audio source with NAudio's `WasapiRecorder` (shared mode, event-driven, 20 ms buffers), asking the Windows audio engine for 48 kHz 32-bit float stereo. Each source is served as raw PCM on its own `NamedPipeServerStream` with `PipeOptions.CurrentUserOnly`. FFmpeg reads `\\.\pipe\linkast-<kind>-<guid>` with `-f f32le`.

The writer keeps each pipe at wall-clock rate:

- If no data has arrived for 60 ms, it writes silence up to the expected byte count. Loopback is silent when nothing is playing, and without this FFmpeg would block and stall the whole stream.
- If the stream is more than 200 ms ahead of wall-clock time, it drops data. This covers a device clock running fast.
- Audio captured before FFmpeg connects is discarded, so there is no startup backlog.

Timestamps come from the sample count. Wall-clock timestamps (`-use_wallclock_as_timestamps`) were tried first. FFmpeg did not rebase them, so output time became epoch-sized. A regression test now asserts that output time tracks uptime.

## Consequences

- Microphones and loopback use the same path and the same format, whatever the device's native format.
- Per-process audio capture later is a small change, because NAudio supports process loopback on Windows 10 2004 and later.
- A/V alignment depends on the pipe writer's pacing. It still needs a long clap-test measurement (risks.md).

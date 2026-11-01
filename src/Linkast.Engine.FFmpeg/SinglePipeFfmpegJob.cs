// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core;

namespace Linkast.Engine.FFmpeg;

/// <summary>
/// Execution parameters for the single-pipe FFmpeg encoder engine (Direct3D 11 compositor architecture).
/// Ingests a pre-composed rawvideo NV12 pipe and a pre-mixed s16le PCM audio pipe.
/// </summary>
public sealed record SinglePipeFfmpegJob(
    string VideoPipePath,
    int Width,
    int Height,
    int Fps,
    string AudioPipePath,
    EncoderSettings Encoder,
    string? PublishUrl,
    string? TcUrl = null,
    string? RecordingPath = null);

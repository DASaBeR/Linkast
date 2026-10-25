// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Linkast.Core;

namespace Linkast.Engine.FFmpeg;

/// <summary>Result of probing one candidate encoder.</summary>
public sealed record EncoderProbeResult(EncoderInfo Encoder, bool Works, string? FailureReason);

/// <summary>
/// Determines which H.264 encoders actually work on this machine by encoding a few real frames
/// with each. Being compiled into FFmpeg is not enough: drivers, GPU vendor and session limits decide.
/// </summary>
public sealed class EncoderCatalog(FfmpegInstallation ffmpeg)
{
    /// <summary>Candidates in preference order: hardware first, software fallback last.</summary>
    public static IReadOnlyList<EncoderInfo> Candidates { get; } =
    [
        new("h264_nvenc", "NVIDIA NVENC (H.264)", EncoderKind.Hardware, "NVIDIA"),
        new("h264_amf", "AMD AMF (H.264)", EncoderKind.Hardware, "AMD"),
        new("h264_qsv", "Intel Quick Sync (H.264)", EncoderKind.Hardware, "Intel"),
        new("h264_mf", "Windows Media Foundation (H.264)", EncoderKind.Hardware, "Microsoft"),
        new("libx264", "x264 software (H.264)", EncoderKind.Software, "x264"),
    ];

    public static string SoftwareFallbackId => "libx264";

    public async Task<IReadOnlyList<EncoderProbeResult>> ProbeAsync(CancellationToken cancellationToken = default)
    {
        // Probes are independent short processes; run them in parallel to keep startup fast.
        var tasks = Candidates.Select(c => ProbeOneAsync(c, cancellationToken));
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<EncoderProbeResult> ProbeOneAsync(EncoderInfo encoder, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg.Path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        string[] args =
        [
            "-hide_banner", "-nostats", "-loglevel", "error",
            "-f", "lavfi", "-i", "color=black:s=1280x720:r=30",
            "-frames:v", "5",
            "-pix_fmt", FfmpegArgumentBuilder.PixelFormat(encoder.Id),
            "-c:v", encoder.Id,
            "-f", "null", "-",
        ];
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using var p = Process.Start(psi);
            if (p is null)
            {
                return new EncoderProbeResult(encoder, false, "Process did not start.");
            }

            var stderrTask = p.StandardError.ReadToEndAsync(ct);
            _ = p.StandardOutput.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                p.Kill(entireProcessTree: true);
                return new EncoderProbeResult(encoder, false, "Probe timed out.");
            }

            var stderr = await stderrTask.ConfigureAwait(false);
            if (p.ExitCode == 0)
            {
                return new EncoderProbeResult(encoder, true, null);
            }

            var firstLine = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            return new EncoderProbeResult(encoder, false, firstLine ?? $"Exit code {p.ExitCode}.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new EncoderProbeResult(encoder, false, ex.Message);
        }
    }
}

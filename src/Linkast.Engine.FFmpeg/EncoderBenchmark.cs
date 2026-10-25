// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using Linkast.Core;

namespace Linkast.Engine.FFmpeg;

/// <summary>
/// Measures how many 1080p frames per second an encoder manages on this PC, with the same encoder
/// settings a stream uses. Nothing leaves the PC: the output is discarded.
/// </summary>
public static class EncoderBenchmark
{
    /// <summary>Seconds of encoding measured (after a short warm-up that is not counted).</summary>
    public const int Seconds = 4;

    public static async Task<EncoderSpeed?> RunAsync(FfmpegInstallation ffmpeg, EncoderInfo encoder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        ArgumentNullException.ThrowIfNull(encoder);
        var settings = new EncoderSettings(encoder.Id, 1920, 1080, 60, 6000);
        var psi = new ProcessStartInfo(ffmpeg.Path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // One second of a moving test picture, generated once and then repeated, so making the
        // picture costs almost nothing and the time measured is the encoder's.
        List<string> args =
        [
            "-hide_banner", "-nostats", "-loglevel", "error", "-progress", "pipe:1", "-stats_period", "0.25",
            "-f", "lavfi", "-i", "testsrc2=s=1920x1080:r=60:d=1,loop=loop=-1:size=60",
            "-frames:v", "100000", // stopped after the measured seconds; this is only a safety limit
        ];
        args.AddRange(FfmpegArgumentBuilder.VideoEncoderArgs(settings));
        args.AddRange(["-an", "-f", "null", "-"]);
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using var p = Process.Start(psi);
            if (p is null)
            {
                return null;
            }

            _ = p.StandardError.ReadToEndAsync(cancellationToken);
            var clock = Stopwatch.StartNew();
            (double Time, long Frame)? first = null, last = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Seconds + 20));
            try
            {
                while (await p.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
                {
                    if (!line.StartsWith("frame=", StringComparison.Ordinal)
                        || !long.TryParse(line.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out var frame) || frame == 0)
                    {
                        continue;
                    }

                    var now = clock.Elapsed.TotalSeconds;
                    first ??= (now, frame); // the warm-up (device start, first second of picture) ends at the first report
                    last = (now, frame);
                    if (now - first.Value.Time >= Seconds)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                }
            }

            if (first is not { } a0 || last is not { } a1 || a1.Time - a0.Time < 1)
            {
                return null;
            }

            return new EncoderSpeed(encoder.Id, encoder.Kind == EncoderKind.Hardware, (a1.Frame - a0.Frame) / (a1.Time - a0.Time));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

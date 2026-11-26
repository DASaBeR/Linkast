// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using Linkast.Core;
using Linkast.Engine.FFmpeg;

namespace Linkast.Engine.FFmpeg.Tests;

/// <summary>
/// The drop path on a real encoder's FLV (H.264 with B-frames, AAC): what is left after a drop must decode
/// without errors, and sound and picture must still start together.
/// </summary>
public class FlvBacklogFfmpegTests
{
    [SkippableFact]
    public async Task A_dropped_real_stream_still_decodes_with_sound_and_picture_together()
    {
        FfmpegInstallation? ffmpeg = null;
        try { ffmpeg = await FfmpegLocator.LocateAsync(); } catch (StreamFailureException) { }
        Skip.If(ffmpeg is null, "FFmpeg is not available on this machine.");
        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg!.Path)!, "ffprobe.exe");
        Skip.IfNot(File.Exists(ffprobe), "ffprobe is not next to ffmpeg.");

        var dir = Directory.CreateTempSubdirectory("linkast-flv-");
        try
        {
            var source = Path.Combine(dir.FullName, "source.flv");
            var kept = Path.Combine(dir.FullName, "kept.flv");

            // 20 s like the encoder's: key frame every 2 s, two B-frames, AAC.
            var (code, err) = await RunAsync(ffmpeg.Path,
                "-hide_banner -loglevel error -f lavfi -i testsrc2=size=640x360:rate=30 -f lavfi -i sine=frequency=440:sample_rate=48000 " +
                "-t 20 -c:v libx264 -preset ultrafast -bf 2 -g 60 -keyint_min 60 -c:a aac -b:a 128k -f flv -flvflags no_duration_filesize -y \"" + source + "\"");
            Skip.If(code != 0, "This FFmpeg cannot make the test stream: " + err);

            var backlog = new FlvBacklog();
            backlog.Append(await File.ReadAllBytesAsync(source));
            var dropped = backlog.DropOldest(TimeSpan.FromSeconds(8), 64L << 20, TimeSpan.FromSeconds(4));
            Assert.InRange(dropped.TotalSeconds, 14, 17.5);

            await using (var output = File.Create(kept))
            {
                while (backlog.TryTake() is { } unit)
                {
                    await output.WriteAsync(unit);
                }
            }

            // Decodes cleanly.
            (code, err) = await RunAsync(ffmpeg.Path, "-hide_banner -v error -i \"" + kept + "\" -f null -");
            Assert.Equal(0, code);
            Assert.True(string.IsNullOrWhiteSpace(err), "Decoding what was left reported: " + err);

            // The first picture is a key frame, and sound starts with it (within one audio frame of slack).
            (code, var packets) = await RunAsync(ffprobe,
                "-v error -show_entries packet=stream_index,pts_time,flags -of csv=p=0 \"" + kept + "\"", stdout: true);
            Assert.Equal(0, code);
            var rows = packets.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.Split(','))
                .Where(f => f.Length >= 3 && double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                .Select(f => (Stream: f[0], Pts: double.Parse(f[1], CultureInfo.InvariantCulture), Key: f[2].Contains('K', StringComparison.Ordinal)))
                .ToList();
            var video = rows.Where(r => r.Stream == "0").ToList();
            var audio = rows.Where(r => r.Stream == "1").ToList();
            Assert.True(video.First().Key, "the stream after a drop must begin with a key frame");
            Assert.True(Math.Abs(video.Min(v => v.Pts) - audio.Min(a => a.Pts)) < 0.1,
                $"picture starts at {video.Min(v => v.Pts):0.000} s, sound at {audio.Min(a => a.Pts):0.000} s");
            Assert.InRange(video.Max(v => v.Pts) - video.Min(v => v.Pts), 3, 4.1);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static async Task<(int Code, string Text)> RunAsync(string exe, string arguments, bool stdout = false)
    {
        using var process = Process.Start(new ProcessStartInfo(exe, arguments)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        return (process.ExitCode, stdout ? await output : await error);
    }
}

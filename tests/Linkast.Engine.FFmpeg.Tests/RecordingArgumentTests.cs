// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core;

namespace Linkast.Engine.FFmpeg.Tests;

public class RecordingArgumentTests
{
    private const char B = (char)0x5C;
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "Linkast rec");
    private static readonly string File = Path.Combine(Folder, "Linkast 2026-10-25 21-04-09.mkv");

    private static List<string> Args(string? publishUrl, string? tcUrl, string? recording) =>
        [.. FfmpegArgumentBuilder.Build(new FfmpegJob(new DisplaySource(1), [], EncoderSettings.Default1080p60("libx264"), publishUrl, tcUrl)
        {
            RecordingPath = recording,
        })];

    [Fact]
    public void Streaming_only_is_unchanged_flv()
    {
        var args = Args("rtmp://127.0.0.1:50000/live/k", "rtmp://live.example/app", null);
        Assert.Equal(["-f", "flv", "-flvflags", "no_duration_filesize", "-rtmp_tcurl", "rtmp://live.example/app", "rtmp://127.0.0.1:50000/live/k"], args[^7..]);
        Assert.DoesNotContain("tee", args);
    }

    [Fact]
    public void Recording_only_writes_matroska()
    {
        var args = Args(null, null, File);
        Assert.Equal(["-f", "matroska", File], args[^3..]);
        Assert.DoesNotContain("flv", args);
    }

    [Fact]
    public void Streaming_and_recording_encode_once_through_tee()
    {
        var args = Args("rtmp://127.0.0.1:50000/live/k", "rtmp://live.example:1935/app", File);
        Assert.Equal(["-flags", "+global_header", "-f", "tee"], args[^5..^1]);
        var spec = args[^1];
        var slaves = spec.Split('|');
        Assert.Equal(2, slaves.Length);

        // The tcUrl is escaped twice (slave level and option level), so its ':' survive both parses.
        var colon = $"{B}{B}:";
        Assert.Equal($"[f=flv:flvflags=no_duration_filesize:rtmp_tcurl=rtmp{colon}//live.example{colon}1935/app]rtmp://127.0.0.1:50000/live/k", slaves[0]);

        // The file path is escaped once: backslashes doubled, nothing else changed.
        Assert.Equal("[f=matroska:onfail=ignore]" + File.Replace(B.ToString(), $"{B}{B}", StringComparison.Ordinal), slaves[1]);
    }

    [Fact]
    public void Special_characters_in_paths_are_escaped()
    {
        Assert.Equal($"a{B}|b{B}{B}c{B}'d", FfmpegArgumentBuilder.TeeEscape($"a|b{B}c'd"));
        Assert.Equal($"x{B}:y{B}]z{B}=w", FfmpegArgumentBuilder.TeeOptionEscape("x:y]z=w"));
    }

    [Theory]
    [InlineData("relative.mkv")]
    [InlineData("C:/rec/file.mp4")]
    [InlineData("-C:/rec/file.mkv")]
    public void Invalid_recording_paths_are_rejected(string path)
    {
        var ex = Assert.Throws<StreamFailureException>(() => Args(null, null, path));
        Assert.Equal("recording.path", ex.Error.Code);
    }

    [Fact]
    public void No_output_at_all_is_rejected()
    {
        var ex = Assert.Throws<StreamFailureException>(() => Args(null, null, null));
        Assert.Equal("output.none", ex.Error.Code);
    }

    [Fact]
    public void Recording_file_names_never_overwrite()
    {
        var folder = Directory.CreateTempSubdirectory("linkast-rec").FullName;
        try
        {
            var when = new DateTime(2026, 10, 25, 21, 4, 9);
            var first = RecordingFiles.NewPath(folder, when);
            Assert.Equal("Linkast 2026-10-25 21-04-09.mkv", Path.GetFileName(first));
            System.IO.File.WriteAllBytes(first, [1]);
            Assert.Equal("Linkast 2026-10-25 21-04-09 (2).mkv", Path.GetFileName(RecordingFiles.NewPath(folder, when)));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}

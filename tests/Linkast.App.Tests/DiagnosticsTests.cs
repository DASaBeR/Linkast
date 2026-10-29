// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.App.Services;
using Xunit.Abstractions;

namespace Linkast.App.Tests;

/// <summary>The PC report and the load line that go into a tester's log, read from this machine.</summary>
public class DiagnosticsTests(ITestOutputHelper output)
{
    [Fact]
    public void The_system_report_reads_every_part_on_this_pc()
    {
        var lines = SystemReport.Lines().ToList();
        foreach (var line in lines)
        {
            output.WriteLine(line);
        }

        Assert.Contains(lines, l => l.StartsWith("Windows:", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("CPU:", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("GPU 0:", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("Monitor on GPU", StringComparison.Ordinal) && l.Contains(" Hz", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("could not be read", StringComparison.Ordinal));
    }

    [Fact]
    public void The_gpu_counters_open_and_read()
    {
        using var gpu = GpuCounters.TryOpen();
        Assert.NotNull(gpu);
        gpu.Collect();
        Thread.Sleep(1100);
        var load = gpu.Read(Environment.ProcessId);
        Assert.NotNull(load);
        output.WriteLine($"3D {load.Value.ThreeD:0.0}%, this process {load.Value.FfmpegThreeD:0.0}%, encoder {load.Value.Encode:0.0}%");
        Assert.InRange(load.Value.ThreeD, 0, 100);
    }

    [Fact]
    public void The_load_line_says_what_is_busy() =>
        Assert.Equal(
            "PC while live: CPU 64% (Linkast 9%, FFmpeg 21%), GPU 3D 97% (FFmpeg 4%), video encoder 31%, 5.2 GB memory free",
            PerformanceMonitor.Describe(64.2, 9.1, 21.4, new GpuLoad(97.3, 4.2, 31), 5.24));

    [Fact]
    public void A_reconnect_leaves_ffmpeg_unmeasured_for_that_minute() =>
        Assert.Contains("FFmpeg n/a", PerformanceMonitor.Describe(50, 5, double.NaN, null, 4), StringComparison.Ordinal);
}

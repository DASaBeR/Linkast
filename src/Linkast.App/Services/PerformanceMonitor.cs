// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Linkast.App.Services;

/// <summary>
/// While a stream runs, writes how busy the PC is once a minute: the CPU (all of it, Linkast and FFmpeg),
/// the GPU's 3D and video-encode engines (from Windows' "GPU Engine" counters, the ones Task Manager shows)
/// and free memory. Next to the engine's frame counts, a tester's log then shows whether a stutter came
/// from a busy GPU, a busy CPU or the network.
/// </summary>
public sealed partial class PerformanceMonitor(ILogger<PerformanceMonitor> logger, Func<int> ffmpegProcessId) : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly ILogger<PerformanceMonitor> _logger = logger;
    private Timer? _timer;
    private GpuCounters? _gpu;
    private Sample _last;

    private readonly record struct Sample(long At, ulong Idle, ulong Busy, TimeSpan Linkast, int FfmpegId, TimeSpan Ffmpeg);

    public bool IsRunning => _timer is not null;

    /// <summary>Starts sampling (a stream went live). Does nothing when already running.</summary>
    public void Start()
    {
        if (_timer is not null)
        {
            return;
        }

        _gpu ??= GpuCounters.TryOpen();
        _gpu?.Collect();
        _last = Take();
        _timer = new Timer(_ => Report(), null, Interval, Interval);
    }

    /// <summary>Stops sampling (the stream ended).</summary>
    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose()
    {
        Stop();
        _gpu?.Dispose();
    }

    private void Report()
    {
        try
        {
            var now = Take();
            var gpu = _gpu?.Read(now.FfmpegId);
            var seconds = Stopwatch.GetElapsedTime(_last.At, now.At).TotalSeconds;
            var cores = Environment.ProcessorCount;
            var idle = now.Idle - _last.Idle;
            var busy = now.Busy - _last.Busy;
            var total = busy == 0 ? 0 : 100.0 * (busy - Math.Min(idle, busy)) / busy;
            var linkast = 100.0 * (now.Linkast - _last.Linkast).TotalSeconds / seconds / cores;
            var ffmpeg = now.FfmpegId == _last.FfmpegId && now.FfmpegId != 0
                ? 100.0 * (now.Ffmpeg - _last.Ffmpeg).TotalSeconds / seconds / cores
                : double.NaN;
            _last = now;

            LogLoad(Describe(total, linkast, ffmpeg, gpu, SystemReport.FreeMemoryGb()));
        }
#pragma warning disable CA1031 // Diagnostics must never disturb a stream.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFailed(ex.GetType().Name);
        }
    }

    /// <summary>The load line, for example "PC while live: CPU 64% (Linkast 9%, FFmpeg 21%), GPU 3D 97% (FFmpeg 4%), video encoder 31%, 5.2 GB memory free".</summary>
    public static string Describe(double cpu, double linkast, double ffmpeg, GpuLoad? gpu, double freeGb)
    {
        var inv = CultureInfo.InvariantCulture;
        var ff = double.IsNaN(ffmpeg) ? "n/a" : ffmpeg.ToString("0", inv) + "%";
        var g = gpu is { } l
            ? string.Create(inv, $"GPU 3D {l.ThreeD:0}% (FFmpeg {l.FfmpegThreeD:0}%), video encoder {l.Encode:0}%")
            : "GPU load unavailable";
        return string.Create(inv, $"PC while live: CPU {cpu:0}% (Linkast {linkast:0}%, FFmpeg {ff}), {g}, {freeGb:0.0} GB memory free");
    }

    private Sample Take()
    {
        GetSystemTimes(out var idle, out var kernel, out var user);
        var id = 0;
        var ffmpeg = TimeSpan.Zero;
        try
        {
            using var self = Process.GetCurrentProcess();
            var linkast = self.TotalProcessorTime;
            id = ffmpegProcessId();
            if (id != 0)
            {
                using var p = Process.GetProcessById(id);
                ffmpeg = p.TotalProcessorTime;
            }

            return new Sample(Stopwatch.GetTimestamp(), idle, kernel + user, linkast, id, ffmpeg);
        }
        catch (ArgumentException)
        {
            // FFmpeg exited between the two calls (a reconnect).
            using var self = Process.GetCurrentProcess();
            return new Sample(Stopwatch.GetTimestamp(), idle, kernel + user, self.TotalProcessorTime, 0, TimeSpan.Zero);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Line}")]
    private partial void LogLoad(string line);

    [LoggerMessage(Level = LogLevel.Debug, Message = "PC load could not be read ({Error})")]
    private partial void LogFailed(string error);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}

/// <summary>GPU engine utilisation over the last interval, in percent.</summary>
public readonly record struct GpuLoad(double ThreeD, double FfmpegThreeD, double Encode);

/// <summary>
/// Windows' "GPU Engine" performance counters through PDH: one instance per process and engine, named
/// like "pid_1234_luid_…_engtype_3D". Utilisation is a rate, so each read averages since the last one.
/// </summary>
public sealed class GpuCounters : IDisposable
{
    private const uint FmtDouble = 0x00000200;
    private const uint FmtNoCap100 = 0x00008000;
    private const int MoreData = unchecked((int)0x800007D2);

    private readonly nint _query;
    private readonly nint _threeD;
    private readonly nint _encode;

    private GpuCounters(nint query, nint threeD, nint encode)
    {
        _query = query;
        _threeD = threeD;
        _encode = encode;
    }

    public static GpuCounters? TryOpen()
    {
        if (PdhOpenQueryW(null, 0, out var query) != 0)
        {
            return null;
        }

        if (PdhAddEnglishCounterW(query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", 0, out var threeD) != 0
            || PdhAddEnglishCounterW(query, @"\GPU Engine(*engtype_VideoEncode)\Utilization Percentage", 0, out var encode) != 0)
        {
            _ = PdhCloseQuery(query);
            return null;
        }

        return new GpuCounters(query, threeD, encode);
    }

    public void Collect() => _ = PdhCollectQueryData(_query);

    /// <summary>Collects again and sums each engine type over all processes since the last collect.</summary>
    public GpuLoad? Read(int ffmpegProcessId)
    {
        if (PdhCollectQueryData(_query) != 0)
        {
            return null;
        }

        var ffmpegTag = $"pid_{ffmpegProcessId.ToString(CultureInfo.InvariantCulture)}_";
        var threeD = Sum(_threeD, null);
        var ffmpeg = ffmpegProcessId == 0 ? 0 : Sum(_threeD, ffmpegTag);
        var encode = Sum(_encode, null);
        return threeD is null || encode is null ? null : new GpuLoad(Math.Min(threeD.Value, 100), Math.Min(ffmpeg ?? 0, 100), Math.Min(encode.Value, 100));
    }

    private static double? Sum(nint counter, string? onlyTag)
    {
        uint size = 0;
        var status = PdhGetFormattedCounterArrayW(counter, FmtDouble | FmtNoCap100, ref size, out _, 0);
        if (status != MoreData || size == 0)
        {
            return status == 0 ? 0 : null;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, FmtDouble | FmtNoCap100, ref size, out var count, buffer) != 0)
            {
                return null;
            }

            // PDH_FMT_COUNTERVALUE_ITEM_W: name pointer, then status (4 bytes, padded to 8) and the double.
            var itemSize = IntPtr.Size + 16;
            double sum = 0;
            for (var i = 0; i < count; i++)
            {
                var item = buffer + (i * itemSize);
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? string.Empty;
                if (onlyTag is not null && !name.StartsWith(onlyTag, StringComparison.Ordinal))
                {
                    continue;
                }

                if (Marshal.ReadInt32(item + IntPtr.Size) == 0)
                {
                    sum += BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + IntPtr.Size + 8));
                }
            }

            return sum;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose() => _ = PdhCloseQuery(_query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQueryW(string? dataSource, nint userData, out nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounterW(nint query, string path, nint userData, out nint counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bufferSize, out uint itemCount, nint itemBuffer);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(nint query);
}

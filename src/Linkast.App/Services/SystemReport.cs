// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Vortice.DXGI;

namespace Linkast.App.Services;

/// <summary>
/// Writes what a tester's log needs to explain a problem: the PC (Windows, CPU, memory, power), every GPU
/// and monitor (resolution, refresh rate, scaling, HDR), the Windows settings that change how games and
/// capture behave, and, when a stream starts, the captured window and the monitor it is on. Everything is
/// read from Windows only; nothing is sent anywhere.
/// </summary>
public static partial class SystemReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Logs the PC, its GPUs and monitors, and the relevant Windows settings. Never throws.</summary>
    public static void Log(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        foreach (var line in Lines())
        {
            LogLine(logger, line);
        }
    }

    /// <summary>The report, one line each. A part that can't be read says so instead.</summary>
    public static IEnumerable<string> Lines()
    {
        yield return Safe("Windows", Windows);
        yield return Safe("CPU", Cpu);
        yield return Safe("Memory", Memory);
        yield return Safe("Power", Power);
        foreach (var line in SafeMany("GPUs and monitors", GpusAndMonitors))
        {
            yield return line;
        }

        foreach (var line in SafeMany("GPU drivers", Drivers))
        {
            yield return line;
        }

        yield return Safe("Windows graphics settings", GraphicsSettings);
    }

    /// <summary>The captured window when a stream starts: its program, size and state, and its monitor's mode.</summary>
    public static string DescribeWindow(long handle, string? program, string state)
    {
        try
        {
            var hwnd = (nint)handle;
            var size = GetClientRect(hwnd, out var r) ? $"{r.Right - r.Left}×{r.Bottom - r.Top}" : "unknown size";
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            return $"{program ?? "window"}: {size}, {state}, on {DescribeMonitor(monitor)}";
        }
#pragma warning disable CA1031 // Diagnostics must never break going live.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return $"{program ?? "window"}: could not be read ({ex.GetType().Name})";
        }
    }

    private static string Windows()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var product = key?.GetValue("ProductName") as string ?? "Windows";
        var build = Environment.OSVersion.Version.Build;

        // Windows 11 still reports "Windows 10" as its product name; the build number tells them apart.
        if (build >= 22000)
        {
            product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
        }

        return string.Create(Inv, $"Windows: {product} {key?.GetValue("DisplayVersion")} (build {build}.{key?.GetValue("UBR")}), {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}; Linkast {typeof(SystemReport).Assembly.GetName().Version}");
    }

    private static string Cpu()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        var name = (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "unknown";
        return string.Create(Inv, $"CPU: {name}, {Environment.ProcessorCount} threads, {key?.GetValue("~MHz")} MHz");
    }

    private static string Memory()
    {
        var m = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref m)
            ? string.Create(Inv, $"Memory: {m.TotalPhys / 1073741824.0:0.0} GB, {m.AvailPhys / 1073741824.0:0.0} GB free ({m.MemoryLoad}% in use)")
            : "Memory: unknown";
    }

    /// <summary>Free memory now, for the per-minute load line.</summary>
    public static double FreeMemoryGb()
    {
        var m = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref m) ? m.AvailPhys / 1073741824.0 : double.NaN;
    }

    private static string Power()
    {
        if (!GetSystemPowerStatus(out var p))
        {
            return "Power: unknown";
        }

        // A laptop on battery usually runs its GPU and CPU slower.
        var source = p.AcLineStatus switch { 0 => "on battery", 1 => "plugged in", _ => "unknown source" };
        var battery = p.BatteryFlag == 128 ? "no battery" : $"battery {p.BatteryLifePercent}%";
        return $"Power: {source}, {battery}{(p.SystemStatusFlag == 1 ? ", battery saver on" : string.Empty)}";
    }

    private static IEnumerable<string> GpusAndMonitors()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var lines = new List<string>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                var d = adapter.Description1;
                if ((d.Flags & AdapterFlags.Software) != 0)
                {
                    continue;
                }

                lines.Add(string.Create(Inv, $"GPU {a}: {d.Description.Trim()}, {d.DedicatedVideoMemory / 1073741824.0:0.0} GB video memory, vendor 0x{d.VendorId:X4}"));
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                    {
                        lines.Add($"  Monitor on GPU {a}: {DescribeMonitor(output.Description.Monitor, output)}");
                    }
                }
            }
        }

        return lines;
    }

    /// <summary>"\\.\DISPLAY1 2560×1440 @ 165 Hz, 125 % scaling, HDR on, main".</summary>
    private static string DescribeMonitor(nint monitor, IDXGIOutput? output = null)
    {
        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return "an unknown monitor";
        }

        var mode = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
        var hz = EnumDisplaySettings(info.DeviceName, EnumCurrentSettings, ref mode) ? $" @ {mode.DisplayFrequency} Hz" : string.Empty;
        var scaling = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? string.Create(Inv, $", {dpi * 100 / 96} % scaling") : string.Empty;
        var hdr = string.Empty;
        if (output?.QueryInterfaceOrNull<IDXGIOutput6>() is { } o6)
        {
            using (o6)
            {
                hdr = o6.Description1.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020 ? ", HDR on" : ", HDR off";
            }
        }

        var w = info.Monitor.Right - info.Monitor.Left;
        var h = info.Monitor.Bottom - info.Monitor.Top;
        return string.Create(Inv, $"{info.DeviceName} {w}×{h}{hz}{scaling}{hdr}{((info.Flags & 1) != 0 ? ", main" : string.Empty)}");
    }

    /// <summary>Display drivers from the registry: name, version and date.</summary>
    private static IEnumerable<string> Drivers()
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        var lines = new List<string>();
        foreach (var name in root?.GetSubKeyNames().Where(n => n.All(char.IsDigit)) ?? [])
        {
            using var k = root!.OpenSubKey(name);
            if (k?.GetValue("DriverDesc") is string desc && !desc.Contains("Basic", StringComparison.OrdinalIgnoreCase))
            {
                lines.Add($"Driver: {desc}, version {k.GetValue("DriverVersion")}, dated {k.GetValue("DriverDate")}");
            }
        }

        return lines;
    }

    /// <summary>Windows settings that change how games run and how they can be captured.</summary>
    private static string GraphicsSettings()
    {
        using var gfx = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
        var hags = gfx?.GetValue("HwSchMode") switch { 2 => "on", 1 => "off", _ => "default" };
        using var bar = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\GameBar");
        var gameMode = bar?.GetValue("AutoGameModeEnabled") is 0 ? "off" : "on";
        using var dvr = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\GameDVR");
        var backgroundRecording = dvr?.GetValue("AppCaptureEnabled") is 1 ? "on" : "off";
        using var prefs = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
        var global = prefs?.GetValue("DirectXUserGlobalSettings") as string ?? string.Empty;
        var windowedOptimizations = global.Contains("SwapEffectUpgradeEnable=1", StringComparison.Ordinal) ? "on"
            : global.Contains("SwapEffectUpgradeEnable=0", StringComparison.Ordinal) ? "off" : "default";
        return $"Windows graphics: hardware-accelerated GPU scheduling {hags}, Game Mode {gameMode}, Xbox background recording {backgroundRecording}, optimizations for windowed games {windowedOptimizations}";
    }

#pragma warning disable CA1031 // Each part of the report is optional; a failure is reported, never thrown.
    private static string Safe(string what, Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            return $"{what}: could not be read ({ex.GetType().Name})";
        }
    }

    private static List<string> SafeMany(string what, Func<IEnumerable<string>> read)
    {
        try
        {
            return read().ToList();
        }
        catch (Exception ex)
        {
            return [$"{what}: could not be read ({ex.GetType().Name})"];
        }
    }
#pragma warning restore CA1031

    [LoggerMessage(Level = LogLevel.Information, Message = "{Line}")]
    private static partial void LogLine(ILogger logger, string line);

    private const int MonitorDefaultToNearest = 2;
    private const int EnumCurrentSettings = -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public short SpecVersion, DriverVersion, Size, DriverExtra;
        public int Fields, PositionX, PositionY, DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FormName;
        public short LogPixels;
        public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public int IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string deviceName, int mode, ref DevMode devMode);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);
}

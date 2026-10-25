// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;
using Linkast.Core.Camera;
using Microsoft.Win32;
using Windows.Devices.Enumeration;

namespace Linkast.Platform.Windows.VirtualCamera;

public enum VirtualCameraSupport
{
    /// <summary>Windows 10: the virtual camera API needs Windows 11.</summary>
    NeedsWindows11,

    /// <summary>The components aren't part of this build (development build without native parts).</summary>
    ComponentsMissing,

    NotInstalled,

    Installed,
}

public sealed record VirtualCameraStatus(VirtualCameraSupport Support, string? InstalledPath, bool DeviceListed);

/// <summary>
/// Installs and removes the Linkast Camera. Checking needs no rights; installing or removing runs
/// LinkastCameraSetup.exe, which asks Windows for administrator rights once (UAC).
/// </summary>
public static class VirtualCameraInstaller
{
    /// <summary>Folder with Linkast.VirtualCamera.dll and LinkastCameraSetup.exe, next to Linkast.exe.</summary>
    public static string ComponentsFolder { get; set; } = Path.Combine(AppContext.BaseDirectory, "vcam");

    public static bool IsWindows11 => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    public static async Task<VirtualCameraStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!IsWindows11)
        {
            return new(VirtualCameraSupport.NeedsWindows11, null, false);
        }

        var registered = RegisteredPath();
        var installed = registered is not null && File.Exists(registered);
        var listed = await IsDeviceListedAsync(cancellationToken).ConfigureAwait(false);
        if (installed || listed)
        {
            return new(VirtualCameraSupport.Installed, registered, listed);
        }

        return File.Exists(Path.Combine(ComponentsFolder, "LinkastCameraSetup.exe")) && File.Exists(Path.Combine(ComponentsFolder, "Linkast.VirtualCamera.dll"))
            ? new(VirtualCameraSupport.NotInstalled, null, false)
            : new(VirtualCameraSupport.ComponentsMissing, null, false);
    }

    /// <summary>Path of the registered media source DLL (HKLM, readable without admin rights), or null.</summary>
    public static string? RegisteredPath()
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"Software\Classes\CLSID\{VirtualCameraProtocol.Clsid}\InprocServer32");
        return key?.GetValue(null) as string;
    }

    public static async Task<bool> IsDeviceListedAsync(CancellationToken cancellationToken = default)
    {
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask(cancellationToken).ConfigureAwait(false);
        return devices.Any(d => VirtualCameraProtocol.IsLinkastCamera(d.Name));
    }

    /// <summary>Installs (or updates) the Linkast Camera. Returns null on success, else a message for the user.</summary>
    public static Task<string?> InstallAsync(CancellationToken cancellationToken = default) =>
        RunSetupAsync(["install", ComponentsFolder], cancellationToken);

    /// <summary>Removes the Linkast Camera from this PC. Returns null on success, else a message for the user.</summary>
    public static Task<string?> RemoveAsync(CancellationToken cancellationToken = default) =>
        RunSetupAsync(["uninstall"], cancellationToken);

    private static async Task<string?> RunSetupAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var setup = Path.Combine(ComponentsFolder, "LinkastCameraSetup.exe");
        if (!File.Exists(setup))
        {
            return "The virtual camera components are missing from this copy of Linkast. Reinstall Linkast.";
        }

        // ShellExecute with the "runas" verb shows the UAC prompt; the helper's manifest also requires it.
        var psi = new ProcessStartInfo(setup) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "Windows didn't get permission (the administrator prompt was cancelled). Nothing was changed.";
        }

        if (process is null)
        {
            return "The setup helper didn't start.";
        }

        using (process)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode == 0)
            {
                return null;
            }

            var log = Path.Combine(Path.GetTempPath(), "LinkastCameraSetup.log");
            var detail = File.Exists(log) ? (await File.ReadAllLinesAsync(log, cancellationToken).ConfigureAwait(false)).LastOrDefault(l => !l.StartsWith("Result", StringComparison.Ordinal)) : null;
            return $"Setting up the virtual camera failed (code 0x{process.ExitCode:X8}). {detail}".Trim();
        }
    }
}

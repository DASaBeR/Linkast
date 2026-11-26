// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace Linkast.Platform.Windows.Audio;

/// <summary>One running process: its ID, the process that started it, and its program file ("Spotify.exe").</summary>
public readonly record struct ProcessEntry(uint ProcessId, uint ParentId, string ExeName);

/// <summary>
/// Finds the process whose sound stands for a whole program. Chrome, Discord and Spotify each run many
/// processes of the same name and play their sound from a child; capturing the first process (the parent of
/// the others) with its children takes all of it. Looked up by name, so a program that restarted (a new
/// process ID) is found again.
/// </summary>
public static partial class ProgramProcesses
{
    /// <summary>The first process of <paramref name="exeName"/> (for example "chrome.exe"), or null when it isn't running.</summary>
    public static uint? FindRoot(string exeName) => PickRoot(WithoutExited(Snapshot(), [exeName]), exeName);

    /// <summary>True when <paramref name="processId"/> still runs and is <paramref name="exeName"/>.</summary>
    public static bool IsRunning(uint processId, string exeName) =>
        WithoutExited(Snapshot(), [exeName]).Any(p => p.ProcessId == processId && string.Equals(p.ExeName, exeName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Leaves out processes of <paramref name="exeNames"/> that have ended but are still listed: Windows keeps
    /// an ended process in the list while anything holds a handle to it. Only the named programs are checked.
    /// </summary>
    public static IReadOnlyList<ProcessEntry> WithoutExited(IReadOnlyList<ProcessEntry> processes, IReadOnlyCollection<string> exeNames)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(exeNames);
        return [.. processes.Where(p => !exeNames.Contains(p.ExeName, StringComparer.OrdinalIgnoreCase) || !HasExited(p.ProcessId))];
    }

    private static bool HasExited(uint processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true; // gone
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false; // can't be asked (a protected process): it runs
        }
    }

    /// <summary>
    /// Of the processes named <paramref name="exeName"/>, the one no other of them started. With several (two
    /// copies of the program), the one with the most processes under it: the one that is really in use.
    /// </summary>
    public static uint? PickRoot(IReadOnlyCollection<ProcessEntry> processes, string exeName)
    {
        ArgumentNullException.ThrowIfNull(processes);
        if (string.IsNullOrWhiteSpace(exeName))
        {
            return null;
        }

        var named = processes.Where(p => p.ProcessId != 0 && string.Equals(p.ExeName, exeName, StringComparison.OrdinalIgnoreCase)).ToList();
        var ids = named.Select(p => p.ProcessId).ToHashSet();
        var roots = named.Where(p => !ids.Contains(p.ParentId) || p.ParentId == p.ProcessId).ToList();
        if (roots.Count == 0)
        {
            return null;
        }

        var children = named.ToLookup(p => p.ParentId);
        int Descendants(uint id, int depth) =>
            depth > 32 ? 0 : children[id].Where(c => c.ProcessId != id).Sum(c => 1 + Descendants(c.ProcessId, depth + 1));

        return roots.OrderByDescending(r => Descendants(r.ProcessId, 0)).ThenBy(r => r.ProcessId).First().ProcessId;
    }

    /// <summary>Every running process, from a Windows process snapshot (no rights needed, unlike opening each one).</summary>
    public static unsafe IReadOnlyList<ProcessEntry> Snapshot()
    {
        var result = new List<ProcessEntry>();
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == InvalidHandle)
        {
            return result;
        }

        try
        {
            var entry = new ProcessEntry32W { Size = (uint)sizeof(ProcessEntry32W) };
            for (var ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
            {
                result.Add(new ProcessEntry(entry.ProcessId, entry.ParentProcessId, new string(entry.ExeFile)));
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return result;
    }

    private const uint SnapProcess = 0x2;
    private static readonly nint InvalidHandle = -1;

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ProcessEntry32W
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        public fixed char ExeFile[260];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32FirstW(nint snapshot, ref ProcessEntry32W entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32NextW(nint snapshot, ref ProcessEntry32W entry);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}

// SPDX-License-Identifier: GPL-3.0-or-later
using NAudio.CoreAudioApi;

namespace Linkast.Platform.Windows.Audio;

public enum AudioDeviceKind
{
    /// <summary>A capture endpoint, such as a microphone.</summary>
    Input,

    /// <summary>A render endpoint whose output can be captured as system audio.</summary>
    Output,
}

public sealed record AudioDevice(string Id, string Name, AudioDeviceKind Kind, bool IsDefault)
{
    public override string ToString() => IsDefault ? $"{Name} (default)" : Name;
}

/// <summary>Lists active WASAPI endpoints.</summary>
public static class AudioDeviceCatalog
{
    public static IReadOnlyList<AudioDevice> List(AudioDeviceKind kind)
    {
        var flow = kind == AudioDeviceKind.Input ? DataFlow.Capture : DataFlow.Render;
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        if (enumerator.HasDefaultAudioEndpoint(flow, Role.Console))
        {
            using var d = enumerator.GetDefaultAudioEndpoint(flow, Role.Console);
            defaultId = d.ID;
        }

        var result = new List<AudioDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
            {
                result.Add(new AudioDevice(device.ID, device.FriendlyName, kind, device.ID == defaultId));
            }
        }

        return [.. result.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>
    /// Program files that have a sound session on an output device now (what Windows' volume mixer lists):
    /// a music player, a browser, a voice chat. Chrome plays from a child process; it is listed by its
    /// program file all the same. Windows' own sounds and Linkast are left out.
    /// </summary>
    public static IReadOnlySet<string> ProgramsWithSound()
    {
        var pids = new HashSet<uint>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (var i = 0; i < sessions.Count; i++)
                    {
                        using var session = sessions[i];
                        if (!session.IsSystemSoundsSession && session.GetProcessID is > 0 and var pid)
                        {
                            pids.Add(pid);
                        }
                    }
                }
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // No sound devices, or the audio service is restarting: no programs then.
        }

        var self = (uint)Environment.ProcessId;
        return ProgramProcesses.Snapshot()
            .Where(p => pids.Contains(p.ProcessId) && p.ProcessId != self)
            .Select(p => p.ExeName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}

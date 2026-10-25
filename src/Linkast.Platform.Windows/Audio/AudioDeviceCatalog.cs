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
}

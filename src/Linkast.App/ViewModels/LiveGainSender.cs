// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.App.ViewModels;

/// <summary>
/// Which live gains to send to a running stream. Requests are coalesced per channel (only the newest is
/// sent), and a gain the stream already has is not sent again: each fader step went out twice (the fader
/// and the setting it moves both sent it), and dragging a muted fader sent "0" dozens of times (tester
/// log, 1.0.4). Not thread-safe: used on the UI thread.
/// </summary>
public sealed class LiveGainSender
{
    private readonly Dictionary<string, double> _sent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _pending = new(StringComparer.Ordinal);

    /// <summary>True when something is waiting to be sent.</summary>
    public bool HasPending => _pending.Count > 0;

    /// <summary>Asks for <paramref name="gain"/> on <paramref name="channel"/>. False when nothing needs sending.</summary>
    public bool Request(string channel, double gain)
    {
        if (!_pending.ContainsKey(channel) && _sent.TryGetValue(channel, out var sent) && sent == gain)
        {
            return false;
        }

        _pending[channel] = gain;
        return true;
    }

    /// <summary>The gains to send now (the newest per channel, only those that changed); they count as sent.</summary>
    /// <param name="skip">A channel not to send now (its test sound holds it); its request is dropped.</param>
    public IReadOnlyList<(string Channel, double Gain)> TakeChanges(string? skip = null)
    {
        var changes = new List<(string, double)>();
        foreach (var (channel, gain) in _pending)
        {
            if (channel == skip || (_sent.TryGetValue(channel, out var sent) && sent == gain))
            {
                continue;
            }

            _sent[channel] = gain;
            changes.Add((channel, gain));
        }

        _pending.Clear();
        return changes;
    }

    /// <summary>A gain the stream was given another way (a test sound's 100 %), so the next request is compared with it.</summary>
    public void Note(string channel, double gain) => _sent[channel] = gain;

    /// <summary>A new stream: it starts from its session's gains, so nothing sent before counts.</summary>
    public void Reset()
    {
        _sent.Clear();
        _pending.Clear();
    }
}

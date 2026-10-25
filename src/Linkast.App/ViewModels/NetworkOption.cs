// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core;
using Linkast.Networking;

namespace Linkast.App.ViewModels;

/// <summary>One entry in the "Network for your stream" list.</summary>
public sealed record NetworkOption(string? Id, string Name, NetworkAdapterInfo? Adapter)
{
    public static NetworkOption Automatic { get; } = new(null, "Automatic (same network as Windows uses)", null);

    public bool IsAutomatic => Id is null;

    /// <summary>A remembered adapter that is not connected right now. Kept selected: no silent fallback.</summary>
    public bool IsMissing => Id is not null && Adapter is null;

    public string Label => this switch
    {
        { IsAutomatic: true } => Name,
        { IsMissing: true } => $"{Name} (not connected)",
        _ => Adapter!.ToString(),
    };

    public string Details => this switch
    {
        { IsAutomatic: true } => "Linkast uses whatever connection Windows picks, like every other app.",
        { IsMissing: true } => "This network is not connected. Linkast will not switch to another network on its own; plug it back in or choose another.",
        _ => $"{Adapter!.KindLabel} · {(Adapter.IsUp ? "connected" : "disconnected")} · {Adapter.AddressSummary}" +
             $"{(Adapter.HasIPv4Gateway || Adapter.HasIPv6Gateway ? string.Empty : " · no gateway")} · Windows says: {Adapter.AccessLabel}",
    };

    public NetworkSelection ToSelection() => Id is null ? NetworkSelection.SystemDefault : new NetworkSelection(Id, Name);

    public override string ToString() => Label;
}

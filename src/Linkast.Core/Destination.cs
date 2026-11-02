// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core;

/// <summary>
/// Wraps a sensitive string so it is never printed by accident.
/// <see cref="ToString"/> always returns a mask; call <see cref="Reveal"/> only where the value is consumed.
/// </summary>
public sealed class Secret : IEquatable<Secret>
{
    public const string Mask = "********";

    private readonly string _value;

    public Secret(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value = value;
    }

    public bool IsEmpty => _value.Length == 0;

    public string Reveal() => _value;

    public override string ToString() => Mask;

    public bool Equals(Secret? other) => other is not null && string.Equals(_value, other._value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Secret s && Equals(s);

    public override int GetHashCode() => _value.GetHashCode(StringComparison.Ordinal);
}

/// <summary>Where the stream is sent.</summary>
/// <param name="ServerUrl">RTMP or RTMPS ingest URL without the stream key, for example <c>rtmp://live.example.com/app</c>.</param>
/// <param name="Kind">Which delivery model this destination represents. Shown in the UI so models are never conflated.</param>
public sealed record Destination(string Name, Uri ServerUrl, Secret StreamKey, DestinationKind Kind = DestinationKind.Direct);

/// <summary>Delivery models from PRD 4.5. They are deliberately distinct.</summary>
public enum DestinationKind
{
    /// <summary>A streaming platform's own ingest (Twitch, YouTube, custom server).</summary>
    Direct,

    /// <summary>A third-party restream service that fans out to several platforms.</summary>
    ThirdPartyRestream,
}

/// <summary>Which network interface the outgoing stream must use.</summary>
/// <param name="InterfaceId">Platform interface identifier, or null to use the operating system's default route.</param>
public sealed record NetworkSelection(string? InterfaceId, string? DisplayName)
{
    public static NetworkSelection SystemDefault { get; } = new(null, null);

    public bool IsSystemDefault => InterfaceId is null;

    /// <summary>A proxy the stream goes through (for example a local v2rayN), or null to connect directly.</summary>
    public ProxySettings? Proxy { get; init; }
}

public enum ProxyKind
{
    None,
    Socks5,
    Http,
}

/// <summary>A SOCKS5 or HTTP (CONNECT) proxy for the outgoing stream. Only the stream uses it.</summary>
public sealed record ProxySettings(ProxyKind Kind, string Host, int Port, string? Username = null, Secret? Password = null)
{
    public bool IsEnabled => Kind != ProxyKind.None && !string.IsNullOrWhiteSpace(Host) && Port is > 0 and <= 65535;

    public bool HasCredentials => !string.IsNullOrEmpty(Username);

    public string Label => $"{(Kind == ProxyKind.Socks5 ? "SOCKS5" : "HTTP")} proxy {Host}:{Port}";
}

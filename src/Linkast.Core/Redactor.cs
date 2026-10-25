// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace Linkast.Core;

/// <summary>
/// Removes secrets from any text before it is logged or shown.
/// Two layers: exact registered secrets, and a structural rule that masks the path segment after
/// the RTMP application name, which is where stream keys live.
/// </summary>
public sealed partial class Redactor
{
    private readonly Lock _gate = new();
    private string[] _secrets = [];

    public static Redactor Shared { get; } = new();

    public void Register(Secret secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var value = secret.Reveal();
        if (value.Length < 4)
        {
            return;
        }

        lock (_gate)
        {
            if (!_secrets.Contains(value, StringComparer.Ordinal))
            {
                // Longest first so overlapping secrets are fully masked.
                _secrets = [.. _secrets.Append(value).OrderByDescending(s => s.Length)];
            }
        }
    }

    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = text;
        foreach (var s in Volatile.Read(ref _secrets))
        {
            result = result.Replace(s, Secret.Mask, StringComparison.Ordinal);
            var encoded = Uri.EscapeDataString(s);
            if (!string.Equals(encoded, s, StringComparison.Ordinal))
            {
                result = result.Replace(encoded, Secret.Mask, StringComparison.Ordinal);
            }
        }

        return RtmpKeyPattern().Replace(result, m => m.Groups["prefix"].Value + Secret.Mask);
    }

    /// <summary>Matches rtmp[s|t|e]://host[:port]/app/KEY... and masks everything after the app segment.</summary>
    [GeneratedRegex(@"(?<prefix>rtmp[a-z]{0,2}://[^/\s'""]+/[^/\s'""]+/)[^\s'""]+", RegexOptions.IgnoreCase)]
    private static partial Regex RtmpKeyPattern();
}

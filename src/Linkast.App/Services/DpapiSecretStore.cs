// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Linkast.Core;

namespace Linkast.App.Services;

/// <summary>
/// Stores secrets (the stream key) encrypted with Windows DPAPI for the current user, in
/// <c>secrets.dat</c>, separate from settings. Another Windows user, or a copy of the file on
/// another PC, cannot decrypt them. Secrets are never exported in plain text.
/// </summary>
public sealed class DpapiSecretStore
{
    public const string StreamKeyName = "destination.streamKey";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Linkast.secrets.v1");
    private readonly Lock _gate = new();

    public DpapiSecretStore(string? path = null)
    {
        FilePath = path ?? Path.Combine(App.DataDirectory, "secrets.dat");
    }

    public string FilePath { get; }

    public Secret? Get(string name)
    {
        lock (_gate)
        {
            var all = ReadAll();
            if (!all.TryGetValue(name, out var b64))
            {
                return null;
            }

            try
            {
                var plain = ProtectedData.Unprotect(Convert.FromBase64String(b64), Entropy, DataProtectionScope.CurrentUser);
                return new Secret(Encoding.UTF8.GetString(plain));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                return null; // encrypted for another user or machine, or damaged
            }
        }
    }

    /// <summary>Stores a secret; null or empty removes it.</summary>
    public void Set(string name, Secret? secret)
    {
        lock (_gate)
        {
            var all = ReadAll();
            if (secret is null || secret.IsEmpty)
            {
                all.Remove(name);
            }
            else
            {
                var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret.Reveal()), Entropy, DataProtectionScope.CurrentUser);
                all[name] = Convert.ToBase64String(cipher);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(all));
            File.Move(temp, FilePath, overwrite: true);
        }
    }

    private Dictionary<string, string> ReadAll()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return [];
        }
    }
}

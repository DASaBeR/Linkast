// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Linkast.Effects;

/// <summary>A model file Linkast knows and trusts, identified by its exact size and SHA-256.</summary>
public sealed record ModelArtifact(string FileName, long Size, string Sha256, string Source, string License);

/// <summary>Why a model could not be used.</summary>
public sealed class ModelUnavailableException(string message) : Exception(message)
{
    public ModelUnavailableException()
        : this("The model is unavailable.")
    {
    }

    public ModelUnavailableException(string message, Exception inner)
        : this(message)
    {
        _ = inner;
    }
}

/// <summary>
/// Finds and verifies model files. Models are shipped with Linkast (fetched at build time by
/// tools/fetch-models.ps1); nothing is ever downloaded at runtime, and a file whose size or hash does
/// not match the pinned values is refused.
/// </summary>
public static class ModelCatalog
{
    public const string EnvironmentVariable = "LINKAST_MODELS";

    /// <summary>RobustVideoMatting, MobileNetV3, fp32. GPL-3.0 (same as Linkast).</summary>
    public static ModelArtifact Rvm { get; } = new(
        "rvm_mobilenetv3_fp32.onnx",
        14_975_696,
        "88D4531297118F595BF2FD60F6F566AEC2E559393802D1F436C380F0CBBD2828",
        "https://github.com/PeterL1n/RobustVideoMatting/releases/download/v1.0.0/rvm_mobilenetv3_fp32.onnx",
        "GPL-3.0");

    private static readonly ConcurrentDictionary<string, (DateTime Modified, bool Ok)> Verified = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folders searched, in order: LINKAST_MODELS, &lt;app&gt;\models, then a repository "models" folder (development).</summary>
    public static IEnumerable<string> SearchFolders()
    {
        var env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env))
        {
            yield return env;
        }

        yield return Path.Combine(AppContext.BaseDirectory, "models");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Linkast.slnx")))
            {
                yield return Path.Combine(dir.FullName, "models");
                yield break;
            }
        }
    }

    /// <summary>Returns the verified path of <paramref name="model"/> or throws with a user-presentable message.</summary>
    public static string Locate(ModelArtifact model)
    {
        ArgumentNullException.ThrowIfNull(model);
        string? damaged = null;
        foreach (var folder in SearchFolders())
        {
            var path = Path.Combine(folder, model.FileName);
            if (!File.Exists(path))
            {
                continue;
            }

            if (IsAuthentic(path, model))
            {
                return path;
            }

            damaged = path;
        }

        throw new ModelUnavailableException(damaged is not null
            ? $"The background model file is damaged or not the expected version ({damaged}). Reinstall Linkast."
            : $"The background model '{model.FileName}' is missing. Reinstall Linkast, or run tools/fetch-models.ps1 when building from source.");
    }

    /// <summary>Checks size and SHA-256. The result is cached per file and modification time.</summary>
    public static bool IsAuthentic(string path, ModelArtifact model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != model.Size)
        {
            return false;
        }

        if (Verified.TryGetValue(path, out var cached) && cached.Modified == info.LastWriteTimeUtc)
        {
            return cached.Ok;
        }

        using var stream = File.OpenRead(path);
        var ok = string.Equals(Convert.ToHexString(SHA256.HashData(stream)), model.Sha256, StringComparison.OrdinalIgnoreCase);
        Verified[path] = (info.LastWriteTimeUtc, ok);
        return ok;
    }
}

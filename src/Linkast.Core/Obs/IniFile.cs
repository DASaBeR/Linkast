// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Linkast.Core.Obs;

/// <summary>A minimal reader for the INI files OBS Studio writes (sections, key=value, no escapes).</summary>
public sealed class IniFile
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections = new(StringComparer.OrdinalIgnoreCase);

    public static IniFile Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var ini = new IniFile();
        Dictionary<string, string>? section = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart((char)0xFEFF);
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                var name = line[1..^1].Trim();
                if (!ini._sections.TryGetValue(name, out section))
                {
                    section = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    ini._sections[name] = section;
                }

                continue;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (section is not null && eq > 0)
            {
                section[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }

        return ini;
    }

    /// <summary>Reads <paramref name="path"/>, or null when it does not exist or can't be read.</summary>
    public static IniFile? Load(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public string? Get(string section, string key) =>
        _sections.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    public int? GetInt(string section, string key) =>
        int.TryParse(Get(section, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    public double? GetDouble(string section, string key) =>
        double.TryParse(Get(section, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}

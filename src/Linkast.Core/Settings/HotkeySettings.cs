// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace Linkast.Core.Settings;

/// <summary>What a global hotkey does. Works while Linkast is in the background, for example during a game.</summary>
public enum HotkeyAction
{
    /// <summary>Go live, or end the stream.</summary>
    GoLive,

    MuteMicrophone,

    /// <summary>Turns every camera off, or back on.</summary>
    ToggleCameras,

    BeRightBack,

    /// <summary>Starts or stops a recording (without going live).</summary>
    Record,

    /// <summary>The next scene in the scene strip (wraps around).</summary>
    NextScene,

    PreviousScene,

    /// <summary>Scene 1 to 9 of the scene strip.</summary>
    Scene1,
    Scene2,
    Scene3,
    Scene4,
    Scene5,
    Scene6,
    Scene7,
    Scene8,
    Scene9,
}

/// <summary>Modifier keys. The values are those of Win32 <c>RegisterHotKey</c>.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

/// <summary>A key with modifiers, for example Ctrl+Alt+L. <see cref="Key"/> is a WPF key name ("L", "D5", "F9").</summary>
public sealed partial record Hotkey(HotkeyModifiers Modifiers, string Key)
{
    /// <summary>
    /// A hotkey needs Ctrl, Alt or Windows: with Shift alone or no modifier it would take a key away
    /// from every game and chat box.
    /// </summary>
    public bool IsValid =>
        (Modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Windows)) != 0
        && (Modifiers & ~(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Windows)) == 0
        && KeyPattern().IsMatch(Key);

    /// <summary>For people: "Ctrl+Alt+L", "Ctrl+Shift+5", "Alt+F9".</summary>
    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(Key.Length == 2 && Key[0] == 'D' && char.IsAsciiDigit(Key[1]) ? Key[1..] : Key);
        return string.Join('+', parts);
    }

    /// <summary>Reads what <see cref="ToString"/> writes. Null when it is not a valid hotkey.</summary>
    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var mods = HotkeyModifiers.None;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        foreach (var p in parts[..^1])
        {
            mods |= p.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => HotkeyModifiers.Control,
                "ALT" => HotkeyModifiers.Alt,
                "SHIFT" => HotkeyModifiers.Shift,
                "WIN" or "WINDOWS" => HotkeyModifiers.Windows,
                _ => (HotkeyModifiers)0x100, // unknown: makes the hotkey invalid
            };
        }

        var key = parts[^1];
        if (key.Length == 1 && char.IsAsciiDigit(key[0]))
        {
            key = "D" + key;
        }
        else if (key.Length == 1)
        {
            key = key.ToUpperInvariant();
        }

        var hotkey = new Hotkey(mods, key);
        return hotkey.IsValid ? hotkey : null;
    }

    [GeneratedRegex("^([A-Z]|D[0-9]|F([1-9]|1[0-9]|2[0-4])|NumPad[0-9]|Home|End|Insert|Delete|PageUp|PageDown|Pause|Scroll|Space|Up|Down|Left|Right)$")]
    private static partial Regex KeyPattern();
}

/// <summary>Global hotkeys, stored as text such as "Ctrl+Alt+L". An action without an entry has no hotkey.</summary>
public sealed record HotkeySettings
{
    public bool Enabled { get; init; } = true;

    public IReadOnlyDictionary<HotkeyAction, string> Bindings { get; init; } = Defaults;

    public static IReadOnlyDictionary<HotkeyAction, string> Defaults { get; } = new Dictionary<HotkeyAction, string>
    {
        [HotkeyAction.GoLive] = "Ctrl+Alt+L",
        [HotkeyAction.MuteMicrophone] = "Ctrl+Alt+M",
        [HotkeyAction.ToggleCameras] = "Ctrl+Alt+C",
        [HotkeyAction.BeRightBack] = "Ctrl+Alt+B",
        [HotkeyAction.Record] = "Ctrl+Alt+R",
        [HotkeyAction.NextScene] = "Ctrl+Alt+Right",
        [HotkeyAction.PreviousScene] = "Ctrl+Alt+Left",
        [HotkeyAction.Scene1] = "Ctrl+Alt+1",
        [HotkeyAction.Scene2] = "Ctrl+Alt+2",
        [HotkeyAction.Scene3] = "Ctrl+Alt+3",
        [HotkeyAction.Scene4] = "Ctrl+Alt+4",
        [HotkeyAction.Scene5] = "Ctrl+Alt+5",
        [HotkeyAction.Scene6] = "Ctrl+Alt+6",
        [HotkeyAction.Scene7] = "Ctrl+Alt+7",
        [HotkeyAction.Scene8] = "Ctrl+Alt+8",
        [HotkeyAction.Scene9] = "Ctrl+Alt+9",
    };

    /// <summary>The scene number (1–9) of a scene hotkey, or 0.</summary>
    public static int SceneNumber(HotkeyAction action) =>
        action is >= HotkeyAction.Scene1 and <= HotkeyAction.Scene9 ? action - HotkeyAction.Scene1 + 1 : 0;

    /// <summary>Adds the default key of every action that has none, unless that key is already taken.</summary>
    public HotkeySettings WithNewDefaults()
    {
        var merged = new Dictionary<HotkeyAction, string>(Bindings ?? Defaults);
        var used = merged.Values.Select(Hotkey.Parse).OfType<Hotkey>().ToHashSet();
        foreach (var (action, text) in Defaults)
        {
            if (!merged.ContainsKey(action) && Hotkey.Parse(text) is { } key && used.Add(key))
            {
                merged[action] = text;
            }
        }

        return this with { Bindings = merged };
    }

    /// <summary>The valid hotkey of each action (null if none).</summary>
    public Hotkey? Get(HotkeyAction action) => Bindings.TryGetValue(action, out var text) ? Hotkey.Parse(text) : null;

    /// <summary>Drops unknown actions, invalid keys and a key used twice (the first action keeps it).</summary>
    public HotkeySettings Sanitized()
    {
        var used = new HashSet<Hotkey>();
        var clean = new Dictionary<HotkeyAction, string>();
        foreach (var (action, text) in (Bindings ?? Defaults).OrderBy(b => b.Key))
        {
            if (Enum.IsDefined(action) && Hotkey.Parse(text) is { } key && used.Add(key))
            {
                clean[action] = key.ToString();
            }
        }

        return this with { Bindings = clean };
    }
}

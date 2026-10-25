// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core.Settings;

namespace Linkast.Core.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+L", HotkeyModifiers.Control | HotkeyModifiers.Alt, "L")]
    [InlineData("ctrl + shift + 5", HotkeyModifiers.Control | HotkeyModifiers.Shift, "D5")]
    [InlineData("Alt+F9", HotkeyModifiers.Alt, "F9")]
    [InlineData("Win+Alt+PageUp", HotkeyModifiers.Windows | HotkeyModifiers.Alt, "PageUp")]
    public void Parses_hotkeys(string text, HotkeyModifiers mods, string key) =>
        Assert.Equal(new Hotkey(mods, key), Hotkey.Parse(text));

    [Theory]
    [InlineData("L")]
    [InlineData("Shift+L")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+Hyper+L")]
    [InlineData("Ctrl+Escape")]
    [InlineData("")]
    public void Rejects_hotkeys_that_would_steal_typing_or_are_unknown(string text) => Assert.Null(Hotkey.Parse(text));

    [Fact]
    public void Text_round_trips_and_digits_read_naturally()
    {
        var key = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, "D5");
        Assert.Equal("Ctrl+Shift+5", key.ToString());
        Assert.Equal(key, Hotkey.Parse(key.ToString()));
    }

    [Fact]
    public void Defaults_are_all_valid_and_different()
    {
        var s = new HotkeySettings();
        var keys = Enum.GetValues<HotkeyAction>().Select(s.Get).ToList();
        Assert.All(keys, Assert.NotNull);
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Equal("Ctrl+Alt+L", s.Get(HotkeyAction.GoLive)!.ToString());
    }

    [Fact]
    public void Sanitizing_drops_invalid_and_duplicate_keys()
    {
        var s = new HotkeySettings
        {
            Bindings = new Dictionary<HotkeyAction, string>
            {
                [HotkeyAction.GoLive] = "ctrl+alt+l",
                [HotkeyAction.MuteMicrophone] = "Ctrl+Alt+L",
                [HotkeyAction.Record] = "R",
                [(HotkeyAction)99] = "Ctrl+Alt+X",
            },
        }.Sanitized();

        Assert.Equal(new Dictionary<HotkeyAction, string> { [HotkeyAction.GoLive] = "Ctrl+Alt+L" }, s.Bindings);
    }

    [Fact]
    public void Hotkeys_survive_saving_and_a_cleared_one_stays_cleared()
    {
        var settings = new LinkastSettings
        {
            Hotkeys = new HotkeySettings
            {
                Enabled = false,
                Bindings = new Dictionary<HotkeyAction, string> { [HotkeyAction.Record] = "Ctrl+Shift+F9" },
            },
        };

        var (loaded, _) = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings));
        Assert.False(loaded.Hotkeys.Enabled);
        Assert.Equal("Ctrl+Shift+F9", loaded.Hotkeys.Get(HotkeyAction.Record)!.ToString());
        Assert.Null(loaded.Hotkeys.Get(HotkeyAction.GoLive));
    }
}

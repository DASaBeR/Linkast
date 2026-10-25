// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Controls;
using System.Windows.Input;
using Linkast.App.ViewModels;
using Linkast.Core.Settings;

namespace Linkast.App.Views;

/// <summary>Appearance, updates, hotkeys, diagnostics and privacy. Bound to the main view model.</summary>
public partial class SettingsPage : UserControl
{
    public SettingsPage() => InitializeComponent();

    private void OnHotkeyBoxFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm && sender is TextBox box)
        {
            vm.PauseHotkeys(box.IsKeyboardFocused);
        }
    }

    /// <summary>A hotkey box records the next key combination pressed in it.</summary>
    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: HotkeyRowViewModel row } || DataContext is not MainViewModel vm)
        {
            return;
        }

        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };
        var mods = Keyboard.Modifiers;

        // Tab still moves on; modifiers alone wait for the actual key.
        if (key is Key.Tab && mods is ModifierKeys.None or ModifierKeys.Shift)
        {
            return;
        }

        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        if (mods == ModifierKeys.None && key is Key.Back or Key.Delete)
        {
            vm.Hotkeys.Assign(row, null);
            return;
        }

        if (mods == ModifierKeys.None && key == Key.Escape)
        {
            Keyboard.ClearFocus();
            return;
        }

        var modifiers = HotkeyModifiers.None;
        if (mods.HasFlag(ModifierKeys.Control))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (mods.HasFlag(ModifierKeys.Alt))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (mods.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (mods.HasFlag(ModifierKeys.Windows))
        {
            modifiers |= HotkeyModifiers.Windows;
        }

        vm.Hotkeys.Assign(row, new Hotkey(modifiers, key.ToString()));
    }
}

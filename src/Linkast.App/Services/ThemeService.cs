// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using Linkast.Core.Settings;
using Microsoft.Win32;

namespace Linkast.App.Services;

/// <summary>
/// Applies the appearance: WPF's Fluent theme for the stock controls plus the matching Linkast colour tokens
/// (Themes/Tokens.Dark.xaml or Tokens.Light.xaml). "Match Windows" follows the Windows app theme live.
/// </summary>
public static class ThemeService
{
    private static readonly Uri DarkTokens = new("pack://application:,,,/Themes/Tokens.Dark.xaml");
    private static readonly Uri LightTokens = new("pack://application:,,,/Themes/Tokens.Light.xaml");

    private static AppTheme _choice = AppTheme.Dark;
    private static bool _watchingWindows;

    public static void Apply(AppTheme choice)
    {
        _choice = choice;
        if (choice == AppTheme.System && !_watchingWindows)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _watchingWindows = true;
        }

        ApplyResolved(choice switch
        {
            AppTheme.Light => true,
            AppTheme.System => WindowsUsesLightTheme(),
            _ => false,
        });
    }

    private static void ApplyResolved(bool light)
    {
        var app = Application.Current;

#pragma warning disable WPF0001 // Fluent ThemeMode is marked experimental.
        app.ThemeMode = light ? ThemeMode.Light : ThemeMode.Dark;
#pragma warning restore WPF0001

        // Our tokens go last so they win over the Fluent dictionary that ThemeMode just merged in.
        var merged = app.Resources.MergedDictionaries;
        var old = merged.FirstOrDefault(d => d.Source == DarkTokens || d.Source == LightTokens);
        if (old is not null)
        {
            merged.Remove(old);
        }

        merged.Add(new ResourceDictionary { Source = light ? LightTokens : DarkTokens });
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General && _choice == AppTheme.System)
        {
            Application.Current.Dispatcher.BeginInvoke(() => ApplyResolved(WindowsUsesLightTheme()));
        }
    }

    private static bool WindowsUsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
    }
}

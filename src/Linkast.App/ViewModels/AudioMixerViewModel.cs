// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Linkast.App.ViewModels;

/// <summary>What a mixer channel is, for its icon and its words.</summary>
public enum MixerChannelKind
{
    Desktop,
    Microphone,
    Game,
    WebOverlay,
    SceneVideo,
}

/// <summary>
/// One fader of the mixer: a volume (0–200 %), a mute switch and a level meter, like a channel strip in
/// OBS. A channel can also be silent because the active scene mutes it.
/// </summary>
public sealed partial class MixerChannelViewModel(string key, MixerChannelKind kind, string name) : ObservableObject
{
    /// <summary>The channel name used in the stream (see <see cref="Core.AudioChannels"/>).</summary>
    public string Key { get; } = key;

    public MixerChannelKind Kind { get; } = kind;

    [ObservableProperty]
    public partial string Name { get; set; } = name;

    /// <summary>Fader position, 0 to 200 % (100 % = unchanged).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeText), nameof(IsSilent))]
    public partial double Volume { get; set; } = 100;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSilent))]
    public partial bool Muted { get; set; }

    /// <summary>The active scene mutes this channel (shown on the strip; changed in the scene's settings).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSilent))]
    public partial bool SceneMuted { get; set; }

    /// <summary>Level 0 to 100 for the meter; scene videos have no meter (FFmpeg plays them).</summary>
    [ObservableProperty]
    public partial double Level { get; set; }

    /// <summary>False for a scene video's channel while another scene is on: the Audio tab shows only the active scene's sounds.</summary>
    [ObservableProperty]
    public partial bool InActiveScene { get; set; } = true;

    /// <summary>A web overlay can play a test sound, so its level can be set before an alert comes in.</summary>
    public bool CanTest => Kind == MixerChannelKind.WebOverlay;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    public partial bool IsTesting { get; set; }

    /// <summary>How loud the last test sound was in the stream, for example "Test: −12 dB in the stream".</summary>
    [ObservableProperty]
    public partial string? TestResult { get; set; }

    /// <summary>A short note under the strip, for example why it has no meter.</summary>
    [ObservableProperty]
    public partial string? Note { get; set; }

    public bool HasMeter => Kind != MixerChannelKind.SceneVideo;

    public bool IsSilent => Muted || SceneMuted || Volume <= 0;

    public string VolumeText => Volume <= 0
        ? "-∞ dB"
        : string.Create(CultureInfo.InvariantCulture, $"{20 * Math.Log10(Volume / 100):+0.0;-0.0;0.0} dB");

    /// <summary>The gain the stream uses: 0 while muted (here or by the scene).</summary>
    public double Gain => Muted || SceneMuted ? 0 : Volume / 100;

    /// <summary>
    /// The meter position (0 to 1, for -60 to 0 dBFS) of a sound at <paramref name="levelDb"/> after a
    /// gain of <paramref name="gain"/>: what the stream gets. A meter before the fader kept moving when a
    /// channel was muted or turned down, and testers took the mixer for broken.
    /// </summary>
    public static double StreamLevel(double levelDb, double gain)
    {
        if (gain <= 0)
        {
            return 0;
        }

        var db = levelDb + (20 * Math.Log10(gain));
        return Math.Clamp((db + 60) / 60, 0, 1);
    }

    public string Glyph => Kind switch
    {
        MixerChannelKind.Desktop => "", // speaker
        MixerChannelKind.Microphone => "", // microphone
        MixerChannelKind.Game => "", // game
        MixerChannelKind.WebOverlay => "", // globe
        _ => "", // video
    };

    /// <summary>Raised when the user moves the fader or mutes (not for scene mutes or meter updates).</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when the user asks for a test sound on this channel.</summary>
    public event EventHandler? TestRequested;

    [RelayCommand(CanExecute = nameof(CanRunTest))]
    private void Test() => TestRequested?.Invoke(this, EventArgs.Empty);

    private bool CanRunTest() => CanTest && !IsTesting;

    [RelayCommand]
    private void ToggleMute() => Muted = !Muted;

    [RelayCommand]
    private void ResetVolume() => Volume = 100;

    partial void OnVolumeChanged(double value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnMutedChanged(bool value) => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// The audio mixer: one channel per sound that goes into the stream, in a fixed order (desktop, microphone,
/// game, web overlays, scene videos). The main view model decides which channels exist and keeps the
/// stream's gains in step with them.
/// </summary>
public sealed class AudioMixerViewModel
{
    public ObservableCollection<MixerChannelViewModel> Channels { get; } = [];

    /// <summary>Raised when the user changes a channel's volume or mute.</summary>
    public event EventHandler<MixerChannelViewModel>? ChannelChanged;

    /// <summary>Raised when the user asks for a channel's test sound.</summary>
    public event EventHandler<MixerChannelViewModel>? TestRequested;

    public MixerChannelViewModel? Get(string key) => Channels.FirstOrDefault(c => c.Key == key);

    /// <summary>
    /// Makes the channels exactly <paramref name="wanted"/>, in that order. Existing channels are kept (with
    /// their fader); new ones start at the volume and mute <paramref name="restore"/> gives.
    /// </summary>
    public void Sync(IReadOnlyList<(string Key, MixerChannelKind Kind, string Name)> wanted, Func<string, (double Volume, bool Muted)> restore)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(restore);
        foreach (var gone in Channels.Where(c => wanted.All(w => w.Key != c.Key)).ToList())
        {
            gone.Changed -= OnChannelChanged;
            gone.TestRequested -= OnTestRequested;
            Channels.Remove(gone);
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            var (key, kind, name) = wanted[i];
            var channel = Get(key);
            if (channel is null)
            {
                var (volume, muted) = restore(key);
                channel = new MixerChannelViewModel(key, kind, name) { Volume = volume, Muted = muted };
                channel.Changed += OnChannelChanged;
                channel.TestRequested += OnTestRequested;
                Channels.Insert(Math.Min(i, Channels.Count), channel);
                continue;
            }

            channel.Name = name;
            var at = Channels.IndexOf(channel);
            if (at != i)
            {
                Channels.Move(at, Math.Min(i, Channels.Count - 1));
            }
        }
    }

    private void OnTestRequested(object? sender, EventArgs e)
    {
        if (sender is MixerChannelViewModel channel)
        {
            TestRequested?.Invoke(this, channel);
        }
    }

    private void OnChannelChanged(object? sender, EventArgs e)
    {
        if (sender is MixerChannelViewModel channel)
        {
            ChannelChanged?.Invoke(this, channel);
        }
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linkast.App.Services;
using Linkast.Core;
using Linkast.Core.Audio;
using Linkast.Core.Camera;
using Linkast.Platform.Windows.Audio;

namespace Linkast.App.ViewModels;

/// <summary>What covers a scene's canvas, as picked in the scene settings.</summary>
public enum SceneBackgroundChoice
{
    None,
    Card,
    Picture,
    Video,
}

/// <summary>A scene in a picker that also offers "none".</summary>
public sealed record SceneChoice(Guid? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Scenes and the mixer. Every source is set up once; a scene only says which of them show, where and
/// how loud. Every scene of the stream's orientation is part of the running stream (hidden ones off the
/// canvas, silent), so switching scenes is instant: cameras, overlays, scene pictures and the game move
/// or hide through the command channel and the gains change live.
/// </summary>
public sealed partial class MainViewModel
{
    private const string SceneMediaPrefix = "scene-";

    private readonly Dictionary<string, (AudioLevelBus Bus, AudioLevelMonitor Monitor)> _channelMeters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _channelVolumes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _mutedChannels = new(StringComparer.Ordinal);
    private readonly HashSet<string> _liveCameraIds = new(StringComparer.Ordinal);
    private bool _liveHasScenesMedia;
    private bool _applyingSceneMutes;
    private Guid? _sceneBeforeBreak;
    private DispatcherTimer? _endingTimer;

    // The saved start and end scenes, until the scenes themselves are loaded.
    private (Guid? Start, Guid? End) _pendingSceneChoices;

    /// <summary>The audio mixer: a fader, a mute switch and a meter for every sound in the stream.</summary>
    public AudioMixerViewModel Mixer { get; } = new();

    // ---- The active scene's settings ----------------------------------------------------------

    public IReadOnlyList<SceneBackgroundChoice> SceneBackgrounds { get; } = Enum.GetValues<SceneBackgroundChoice>();

    /// <summary>The scene's name, as shown in the scene strip.</summary>
    public string SceneName
    {
        get => Webcam.ActiveLayout.Name;
        set => Webcam.UpdateActiveScene(s => s with { Name = value });
    }

    public SceneBackgroundChoice SceneBackground
    {
        get => Webcam.ActiveLayout.Media?.Kind switch
        {
            SceneMediaKind.Card => SceneBackgroundChoice.Card,
            SceneMediaKind.Picture => SceneBackgroundChoice.Picture,
            SceneMediaKind.Video => SceneBackgroundChoice.Video,
            _ => SceneBackgroundChoice.None,
        };
        set
        {
            if (value == SceneBackground)
            {
                return;
            }

            // A picture or video asks for its file; without one the choice is not taken.
            var media = Webcam.ActiveLayout.Media ?? new SceneMedia();
            string? path = media.Path;
            if (value is SceneBackgroundChoice.Picture or SceneBackgroundChoice.Video)
            {
                path = value == SceneBackgroundChoice.Picture ? OverlaysViewModel.PickImage() : OverlaysViewModel.PickVideo();
                if (path is null)
                {
                    OnPropertyChanged();
                    return;
                }
            }

            Webcam.UpdateActiveScene(s => s with
            {
                Media = value switch
                {
                    SceneBackgroundChoice.None => null,
                    SceneBackgroundChoice.Card => media with { Kind = SceneMediaKind.Card },
                    SceneBackgroundChoice.Picture => media with { Kind = SceneMediaKind.Picture, Path = path },
                    _ => media with { Kind = SceneMediaKind.Video, Path = path },
                },
            });
        }
    }

    /// <summary>The card's big line.</summary>
    public string SceneTitle
    {
        get => Webcam.ActiveLayout.Media?.Title ?? string.Empty;
        set => Webcam.UpdateActiveScene(s => s with { Media = (s.Media ?? new SceneMedia()) with { Title = value ?? string.Empty } });
    }

    public string? SceneSubtitle
    {
        get => Webcam.ActiveLayout.Media?.Subtitle;
        set => Webcam.UpdateActiveScene(s => s with { Media = (s.Media ?? new SceneMedia()) with { Subtitle = value } });
    }

    public bool SceneHasCard => SceneBackground == SceneBackgroundChoice.Card;

    public bool SceneHasFile => SceneBackground is SceneBackgroundChoice.Picture or SceneBackgroundChoice.Video;

    /// <summary>The picture or video's file name, or a warning when the file is gone.</summary>
    public string? SceneMediaName => Webcam.ActiveLayout.Media?.Path is { } path
        ? File.Exists(path) ? Path.GetFileName(path) : $"{Path.GetFileName(path)} (missing)"
        : null;

    /// <summary>The scene's background in the preview (a card or picture), or null.</summary>
    [ObservableProperty]
    public partial ImageSource? ScenePreviewImage { get; set; }

    /// <summary>The scene's video, which the preview plays muted on a loop, or null.</summary>
    [ObservableProperty]
    public partial string? ScenePreviewVideo { get; set; }

    [RelayCommand]
    private void ChooseSceneFile()
    {
        var path = SceneBackground == SceneBackgroundChoice.Video ? OverlaysViewModel.PickVideo() : OverlaysViewModel.PickImage();
        if (path is not null)
        {
            Webcam.UpdateActiveScene(s => s with { Media = (s.Media ?? new SceneMedia()) with { Path = path } });
        }
    }

    /// <summary>"None" and every scene, for the automation pickers.</summary>
    public ObservableCollection<SceneChoice> SceneChoices { get; } = [];

    [ObservableProperty]
    public partial SceneChoice? StartSceneChoice { get; set; }

    [ObservableProperty]
    public partial SceneChoice? EndSceneChoice { get; set; }

    /// <summary>How long the end scene is on before the stream ends by itself.</summary>
    [ObservableProperty]
    public partial int EndSceneSeconds { get; set; } = 10;

    partial void OnStartSceneChoiceChanged(SceneChoice? value) => ScheduleSave();

    partial void OnEndSceneChoiceChanged(SceneChoice? value) => ScheduleSave();

    partial void OnEndSceneSecondsChanged(int value) => ScheduleSave();

    private void RebuildSceneChoices(Guid? start, Guid? end)
    {
        var was = _loading;
        _loading = true;
        try
        {
            SceneChoices.Clear();
            SceneChoices.Add(new SceneChoice(null, "(none)"));
            foreach (var option in Webcam.LayoutOptions)
            {
                SceneChoices.Add(new SceneChoice(option.Id, option.Name));
            }

            StartSceneChoice = SceneChoices.FirstOrDefault(c => c.Id == start) ?? SceneChoices[0];
            EndSceneChoice = SceneChoices.FirstOrDefault(c => c.Id == end) ?? SceneChoices[0];
        }
        finally
        {
            _loading = was;
        }
    }

    // ---- Be right back, start and end ---------------------------------------------------------

    /// <summary>The Be right back scene is on.</summary>
    public bool IsOnBreak => Webcam.ActiveLayout.Kind == SceneKind.BeRightBack;

    /// <summary>Switches to the Be right back scene, or back to the scene that was on before it.</summary>
    [RelayCommand]
    private void ToggleBeRightBack()
    {
        var active = Webcam.ActiveLayout;
        if (IsOnBreak)
        {
            var back = _sceneBeforeBreak is { } id && Webcam.FindScene(id) is not null
                ? id
                : Webcam.Layouts.FirstOrDefault(l => l.Kind == SceneKind.Gaming && l.Orientation == active.Orientation)?.Id;
            if (back is { } target)
            {
                Webcam.SwitchTo(target);
            }

            return;
        }

        var brb = Webcam.Layouts.FirstOrDefault(l => l.Kind == SceneKind.BeRightBack && l.Orientation == active.Orientation)
            ?? Webcam.Layouts.FirstOrDefault(l => l.Kind == SceneKind.BeRightBack);
        if (brb is null)
        {
            AppendLog("There is no Be right back scene. Add one with + in the scene strip.");
            return;
        }

        _sceneBeforeBreak = active.Id;
        Webcam.SwitchTo(brb.Id);
    }

    /// <summary>True while the end scene is on and the stream is about to end by itself.</summary>
    [ObservableProperty]
    public partial bool IsEnding { get; set; }

    [ObservableProperty]
    public partial int EndingSecondsLeft { get; set; }

    /// <summary>
    /// End stream: shows the end scene first when one is chosen (and ends by itself after its time), or
    /// ends at once. Pressing it again while the end scene is on ends at once.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task EndStreamAsync()
    {
        if (IsEnding || !IsBroadcasting || EndSceneChoice?.Id is not { } endId || Webcam.FindScene(endId) is not { } end
            || !end.CanSwitchLiveFrom(Webcam.ActiveLayout) || State != StreamState.Live)
        {
            StopEnding();
            await StopAsync();
            return;
        }

        Webcam.SwitchTo(endId);
        IsEnding = true;
        EndingSecondsLeft = EndSceneSeconds;
        AppendLog(string.Create(CultureInfo.InvariantCulture, $"Showing '{end.Name}'; the stream ends in {EndSceneSeconds} s. Press End stream again to end now."));
        _endingTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => _ = OnEndingTickAsync(), _ui);
        _endingTimer.Start();
    }

    private async Task OnEndingTickAsync()
    {
        if (!IsEnding)
        {
            return;
        }

        EndingSecondsLeft--;
        if (EndingSecondsLeft <= 0)
        {
            StopEnding();
            await StopAsync();
        }
    }

    private void StopEnding()
    {
        _endingTimer?.Stop();
        IsEnding = false;
        EndingSecondsLeft = 0;
    }

    /// <summary>Before going live: the start scene, if one is chosen (offline, so it never needs to ask).</summary>
    private void SwitchToStartScene()
    {
        if (StartSceneChoice?.Id is { } id && id != Webcam.ActiveLayout.Id)
        {
            Webcam.SwitchTo(id);
        }
    }

    // ---- Scene switches -----------------------------------------------------------------------

    /// <summary>
    /// The active scene changed (a switch, or its background, muted sounds or hidden overlays). The
    /// overlays and the mixer follow it, and while live the stream does at once: the game moves or hides,
    /// the scene pictures swap, and the gains change. Cameras were moved by <see cref="OnLayoutSwitchingAsync"/>.
    /// </summary>
    private void OnActiveSceneChanged(SceneLayout scene)
    {
        Overlays.ApplyScene(scene);
        ApplySceneMutes(scene);
        RefreshScenePreview();
        foreach (var name in new[]
        {
            nameof(SceneName), nameof(SceneBackground), nameof(SceneTitle), nameof(SceneSubtitle), nameof(SceneHasCard),
            nameof(SceneHasFile), nameof(SceneMediaName), nameof(IsOnBreak),
        })
        {
            OnPropertyChanged(name);
        }

        RefreshMixerChannels();
        if (!IsActive || _engine is null)
        {
            return;
        }

        _baseDirty = true;
        _ = SendBaseAsync();
        foreach (var id in _liveLayerIds.Where(id => id.StartsWith(SceneMediaPrefix, StringComparison.Ordinal)))
        {
            _pendingMoves[id] = id == SceneMediaLayerId(scene.Id) ? new NormalizedRect(0, 0, 1, 1) : null;
        }

        _ = SendPendingMovesAsync();
        if (scene.Media is not null && _liveHasScenesMedia && !_liveLayerIds.Contains(SceneMediaLayerId(scene.Id)))
        {
            Webcam.Notice = "This scene's new background shows the next time you go live.";
        }
    }

    /// <summary>The game was moved, resized, trimmed, refitted, shown or hidden in the active scene.</summary>
    private void OnBaseChanged()
    {
        if (IsActive && _engine is not null)
        {
            _baseDirty = true;
            _ = SendBaseAsync();
        }
    }

    private bool _baseDirty;
    private bool _baseInFlight;

    /// <summary>Sends the game's newest placement; the positions of a drag in between are skipped (each change is 8 commands).</summary>
    private async Task SendBaseAsync()
    {
        if (_baseInFlight || _engine is null)
        {
            return;
        }

        _baseInFlight = true;
        try
        {
            while (_baseDirty)
            {
                _baseDirty = false;
                var scene = Webcam.ActiveLayout;
                await _engine.SetBaseAsync(scene.ShowCapture ? scene.BasePlacement : null, scene.BaseCrop, scene.BaseFit);
            }
        }
        finally
        {
            _baseInFlight = false;
        }
    }

    /// <summary>The user showed or hid an overlay: that is part of the active scene.</summary>
    private void OnOverlayVisibilityChanged(OverlayItemViewModel item) =>
        Webcam.UpdateActiveScene(s => s with
        {
            HiddenOverlays = item.Visible
                ? [.. s.HiddenOverlays.Where(id => id != item.LayerId)]
                : [.. s.HiddenOverlays, item.LayerId],
        });

    private void RefreshScenePreview()
    {
        var media = Webcam.ActiveLayout.Media;
        ScenePreviewImage = SceneMediaRenderer.Preview(media, Webcam.CanvasWidth, Webcam.CanvasHeight);
        ScenePreviewVideo = SceneMediaRenderer.PreviewVideo(media);
    }

    private static string SceneMediaLayerId(Guid sceneId) => SceneMediaPrefix + sceneId.ToString("N");

    // ---- Mixer ----------------------------------------------------------------------------------

    /// <summary>The captured game's own sound has its own fader (it is also part of desktop audio).</summary>
    [ObservableProperty]
    public partial bool SeparateGameAudio { get; set; }

    partial void OnSeparateGameAudioChanged(bool value)
    {
        RefreshMixerChannels();
        ScheduleSave();
    }

    /// <summary>
    /// Desktop audio leaves out Linkast's own sound (its web overlays have their own faders), so alerts are
    /// not heard twice. That needs Windows 10 2004 or later and the default output device.
    /// </summary>
    private bool DesktopExcludesLinkast =>
        Environment.OSVersion.Version.Build >= 19041 && (SelectedOutputDevice is null || SelectedOutputDevice.IsDefault);

    /// <summary>Makes the mixer's channels match what goes into the stream, in a fixed order.</summary>
    private void RefreshMixerChannels()
    {
        if (Webcam is null)
        {
            return; // the constructor sets defaults before the studio exists
        }

        var wanted = new List<(string, MixerChannelKind, string)>();
        if (SystemAudioEnabled)
        {
            wanted.Add((AudioChannels.System, MixerChannelKind.Desktop, "Desktop audio"));
        }

        if (MicrophoneEnabled)
        {
            wanted.Add((AudioChannels.Microphone, MixerChannelKind.Microphone, "Microphone"));
        }

        if (SeparateGameAudio && SelectedSourceKind.Kind == VideoSourceKind.Window && SelectedWindow is { } window)
        {
            wanted.Add((AudioChannels.Game, MixerChannelKind.Game, window.ProcessName is { } exe ? $"Game ({Path.GetFileNameWithoutExtension(exe)})" : "Game"));
        }

        foreach (var web in Overlays.Items.Where(o => o.IsWeb && o.HasUrl))
        {
            wanted.Add((AudioChannels.ForOverlay(web.LayerId), MixerChannelKind.WebOverlay, web.Name));
        }

        foreach (var scene in Webcam.Layouts.Where(l => l.Media is { Kind: SceneMediaKind.Video }))
        {
            wanted.Add((AudioChannels.ForSceneMedia(scene.Id), MixerChannelKind.SceneVideo, $"{scene.Name} video"));
        }

        Mixer.Sync(wanted, key => key switch
        {
            AudioChannels.System => (SystemAudioVolume, _mutedChannels.Contains(key)),
            AudioChannels.Microphone => (MicrophoneVolume, MicrophoneMuted),
            _ => (_channelVolumes.TryGetValue(key, out var v) ? v : 100, _mutedChannels.Contains(key)),
        });

        foreach (var channel in Mixer.Channels)
        {
            channel.Note = channel.Kind == MixerChannelKind.SceneVideo ? "Plays while its scene is on" : null;
        }

        ApplySceneMutes(Webcam.ActiveLayout);
        UpdateStreamMeters();
    }

    private void ApplySceneMutes(SceneLayout scene)
    {
        _applyingSceneMutes = true;
        try
        {
            foreach (var channel in Mixer.Channels)
            {
                channel.SceneMuted = scene.Mutes(channel.Key);
            }
        }
        finally
        {
            _applyingSceneMutes = false;
        }

        ApplyAllLiveGains();
    }

    /// <summary>A channel's scene mute was ticked in the scene settings: it belongs to the active scene.</summary>
    private void OnChannelSceneMuteToggled(MixerChannelViewModel channel) =>
        Webcam.UpdateActiveScene(s => s with
        {
            MutedChannels = channel.SceneMuted ? [.. s.MutedChannels, channel.Key] : [.. s.MutedChannels.Where(c => c != channel.Key)],
        });

    private void OnMixerChannelChanged(MixerChannelViewModel channel)
    {
        switch (channel.Key)
        {
            case AudioChannels.System:
                SystemAudioVolume = channel.Volume;
                SetMuted(channel.Key, channel.Muted);
                break;
            case AudioChannels.Microphone:
                MicrophoneVolume = channel.Volume;
                MicrophoneMuted = channel.Muted;
                break;
            default:
                _channelVolumes[channel.Key] = channel.Volume;
                SetMuted(channel.Key, channel.Muted);
                break;
        }

        ApplyLiveGain(channel.Key, GainOf(channel.Key));
        ScheduleSave();

        void SetMuted(string key, bool muted)
        {
            if (muted)
            {
                _mutedChannels.Add(key);
            }
            else
            {
                _mutedChannels.Remove(key);
            }
        }
    }

    /// <summary>
    /// The gain of a channel in the stream now: its fader and mute, the active scene's mute, and for a
    /// scene's video, whether that scene is on.
    /// </summary>
    private double GainOf(string key)
    {
        if (key.StartsWith("scene_", StringComparison.Ordinal) && AudioChannels.ForSceneMedia(Webcam.ActiveLayout.Id) != key)
        {
            return 0;
        }

        if (Mixer.Get(key) is { } channel)
        {
            return channel.Gain;
        }

        return key switch
        {
            AudioChannels.System => SystemAudioVolume / 100.0,
            AudioChannels.Microphone => MicrophoneMuted ? 0 : MicrophoneVolume / 100.0,
            _ => 1,
        };
    }

    private void ApplyAllLiveGains()
    {
        foreach (var channel in Mixer.Channels)
        {
            ApplyLiveGain(channel.Key, GainOf(channel.Key));
        }
    }

    /// <summary>Meters every channel that has a meter while the Stream page is on screen.</summary>
    private void UpdateStreamMeters()
    {
        var wanted = IsStreamPageVisible ? Mixer.Channels.Where(c => c.HasMeter).ToList() : [];
        foreach (var key in _channelMeters.Keys.Where(k => wanted.All(c => c.Key != k)).ToList())
        {
            _channelMeters[key].Monitor.Stop();
            _channelMeters.Remove(key);
        }

        foreach (var channel in wanted)
        {
            if (!_channelMeters.TryGetValue(channel.Key, out var meter))
            {
                var bus = new AudioLevelBus();
                meter = (bus, new AudioLevelMonitor(_audioHub, bus, loopback: channel.Kind == MixerChannelKind.Desktop));
                _channelMeters[channel.Key] = meter;
            }

            switch (channel.Kind)
            {
                case MixerChannelKind.Desktop when DesktopExcludesLinkast:
                    meter.Monitor.StartProcess((uint)Environment.ProcessId, include: false);
                    break;
                case MixerChannelKind.Desktop:
                    meter.Monitor.Start(SelectedOutputDevice?.Id);
                    break;
                case MixerChannelKind.Microphone:
                    meter.Monitor.Start(SelectedInputDevice?.Id);
                    break;
                case MixerChannelKind.Game when SelectedWindow is { ProcessId: > 0 } game:
                    meter.Monitor.StartProcess(game.ProcessId, include: true);
                    break;
                case MixerChannelKind.WebOverlay when OverlayProcessId(channel.Key) is { } pid:
                    meter.Monitor.StartProcess(pid, include: true);
                    break;
            }

            channel.Note ??= meter.Monitor.Problem;
        }

        _meterTimer.IsEnabled = wanted.Count > 0;
    }

    private void ReadStreamMeters()
    {
        foreach (var channel in Mixer.Channels)
        {
            channel.Level = _channelMeters.TryGetValue(channel.Key, out var m) && m.Bus.Active ? m.Bus.Meter.Level * 100 : 0;
        }
    }

    /// <summary>The browser process of the web overlay behind a mixer channel, once its page is running.</summary>
    private uint? OverlayProcessId(string channelKey) =>
        Overlays.Items.FirstOrDefault(o => o.IsWeb && AudioChannels.ForOverlay(o.LayerId) == channelKey) is { } web
        && _webOverlays.Get(web.LayerId)?.ProcessId is > 0 and var pid ? pid : null;

    // ---- What a new stream contains -------------------------------------------------------------

    /// <summary>The sounds of a new stream, one per mixer channel, each at its gain now.</summary>
    private List<AudioSourceSpec> BuildAudioSources()
    {
        var audio = new List<AudioSourceSpec>();
        if (SystemAudioEnabled)
        {
            audio.Add(new SystemAudioSource(SelectedOutputDevice?.Id, SelectedOutputDevice?.Name ?? "Default output")
            {
                Gain = GainOf(AudioChannels.System),
                ExcludeProcessId = DesktopExcludesLinkast ? (uint)Environment.ProcessId : null,
            });
        }

        if (MicrophoneEnabled)
        {
            audio.Add(new MicrophoneSource(SelectedInputDevice?.Id, SelectedInputDevice?.Name ?? "Default microphone")
            {
                Gain = GainOf(AudioChannels.Microphone),
                NoiseSuppression = MicrophoneNoiseSuppression,
            });
        }

        if (SeparateGameAudio && SelectedSourceKind.Kind == VideoSourceKind.Window && SelectedWindow is { ProcessId: > 0 } game)
        {
            audio.Add(new ProcessAudioSource(game.ProcessId, game.ProcessName ?? "Game") { Channel = AudioChannels.Game, Gain = GainOf(AudioChannels.Game) });
        }

        foreach (var web in Overlays.Items.Where(o => o.IsWeb && o.HasUrl))
        {
            var channel = AudioChannels.ForOverlay(web.LayerId);
            if (OverlayProcessId(channel) is { } pid)
            {
                audio.Add(new ProcessAudioSource(pid, web.Name) { Channel = channel, Gain = GainOf(channel) });
            }
        }

        return audio;
    }

    /// <summary>
    /// Every camera any scene of this orientation shows: in its place in the active scene, or hidden (in its
    /// place in the first scene that shows it), so a scene switch can bring it in without a restart.
    /// </summary>
    private List<CameraLayerBinding> BuildCameraBindings(IReadOnlyList<SceneLayout> scenes, SceneLayout active)
    {
        var cameras = new List<CameraLayerBinding>();
        foreach (var layer in Webcam.Layers.Where(l => l.HasCamera))
        {
            if (active.CameraPlacements.TryGetValue(layer.LayerId, out var here))
            {
                cameras.Add(new CameraLayerBinding(layer.Layer, here));
            }
            else if (scenes.Select(s => s.CameraPlacements.TryGetValue(layer.LayerId, out var p) ? p : (NormalizedRect?)null).FirstOrDefault(p => p is not null) is { } elsewhere)
            {
                cameras.Add(new CameraLayerBinding(layer.Layer, elsewhere, StartHidden: true));
            }
        }

        return cameras;
    }

    /// <summary>The backgrounds of every scene of this orientation; only the active scene's shows, and only its video is heard.</summary>
    private List<MediaLayerBinding> BuildSceneMedia(IReadOnlyList<SceneLayout> scenes, SceneLayout active, int canvasWidth, int canvasHeight)
    {
        var media = new List<MediaLayerBinding>();
        foreach (var scene in scenes)
        {
            if (SceneMediaRenderer.StreamFile(scene, canvasWidth, canvasHeight) is not { } file)
            {
                continue;
            }

            var channel = file.IsVideo ? AudioChannels.ForSceneMedia(scene.Id) : null;
            media.Add(new MediaLayerBinding(
                SceneMediaLayerId(scene.Id),
                file.Path,
                file.IsVideo,
                StartHidden: scene.Id != active.Id,
                channel,
                channel is null || scene.Id != active.Id ? 0 : GainOf(channel)));
        }

        return media;
    }

    /// <summary>Starts the pages of the web overlays before a stream, so their sound can be captured from their browsers.</summary>
    private async Task EnsureWebOverlaysAsync()
    {
        foreach (var web in Overlays.Items.Where(o => o.IsWeb && o.HasUrl))
        {
            await _webOverlays.EnsureAsync(web.LayerId, web.Url!, web.PageWidth, web.PageHeight);
        }
    }

    /// <summary>After scenes and overlays were loaded (start-up, OBS import): the overlays, pickers and mixer follow the active scene.</summary>
    private void ScenesLoaded()
    {
        RebuildSceneChoices(_pendingSceneChoices.Start, _pendingSceneChoices.End);
        OnActiveSceneChanged(Webcam.ActiveLayout);
    }

    /// <summary>Wires the scene and mixer events. Called once from the constructor.</summary>
    private void AttachScenesAndMixer()
    {
        Webcam.ActiveSceneChanged += (_, scene) => OnActiveSceneChanged(scene);
        Webcam.LayoutOptions.CollectionChanged += (_, _) => RebuildSceneChoices(StartSceneChoice?.Id, EndSceneChoice?.Id);
        Overlays.VisibilityChanged += (_, item) => OnOverlayVisibilityChanged(item);
        Overlays.ItemsChanged += (_, _) => RefreshMixerChannels();
        Mixer.ChannelChanged += (_, channel) => OnMixerChannelChanged(channel);
        Mixer.Channels.CollectionChanged += (_, e) =>
        {
            foreach (var channel in e.NewItems?.OfType<MixerChannelViewModel>() ?? [])
            {
                channel.PropertyChanged += (_, p) =>
                {
                    if (p.PropertyName == nameof(MixerChannelViewModel.SceneMuted) && !_applyingSceneMutes)
                    {
                        OnChannelSceneMuteToggled(channel);
                        ApplyLiveGain(channel.Key, GainOf(channel.Key));
                    }
                };
            }
        };
    }
}

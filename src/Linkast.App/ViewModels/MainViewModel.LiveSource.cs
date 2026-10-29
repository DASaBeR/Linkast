// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows.Threading;
using Linkast.Core;
using Linkast.Core.Camera;
using Linkast.Platform.Windows.Capture;

namespace Linkast.App.ViewModels;

/// <summary>
/// Each scene's own game, window or display, and each scene's own overlays. The Capture card shows and
/// edits the active scene's capture. While live, every distinct capture of the stream's scenes runs as its
/// own FFmpeg input, so a scene switch only shows one and hides the others; a capture that is not in the
/// stream yet (a game started later, or a new choice on the card) restarts the pipeline with a short pause.
/// Each captured window's sound has its own fader, heard only while its scene is on.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Lets a combo box settle (and a refreshed window list re-select) before restarting.</summary>
    private static readonly TimeSpan LiveSourceSettle = TimeSpan.FromMilliseconds(600);

    private DispatcherTimer? _liveSourceTimer;
    private DispatcherTimer? _windowWatch;
    private IReadOnlyList<CaptureBinding> _liveCaptures = [];
    private IReadOnlyList<AudioSourceSpec> _liveAudio = [];

    /// <summary>What a scene without its own capture uses: the last capture chosen on the card.</summary>
    private SceneCapture _defaultCapture = SceneCapture.Nothing;

    /// <summary>True while the Capture card is being filled from the active scene (not a user's choice).</summary>
    private bool _applyingSceneCapture;

    /// <summary>Why the active scene's capture can't be shown, for example its game isn't running.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial string? SceneCaptureNote { get; set; }

    /// <summary>A capture found on this PC right now.</summary>
    private sealed record ResolvedCapture(string Key, VideoSourceSpec Source, WindowInfo? Window);

    // ---- The Capture card is the active scene's capture ------------------------------------------

    private SceneCapture CaptureOf(SceneLayout scene) =>
        scene.Capture ?? (scene.ShowCapture ? _defaultCapture : SceneCapture.Nothing);

    /// <summary>What the Capture card shows now, as a scene's capture.</summary>
    private SceneCapture CardCapture() => new()
    {
        Kind = SelectedSourceKind.Kind switch
        {
            VideoSourceKind.Display => SceneCaptureKind.Display,
            VideoSourceKind.Window => SceneCaptureKind.Window,
            VideoSourceKind.TestPattern => SceneCaptureKind.TestPattern,
            _ => SceneCaptureKind.None,
        },
        MonitorDeviceName = SelectedMonitor?.DeviceName,
        WindowTitle = SelectedWindow?.Title,
        WindowProcess = SelectedWindow?.ProcessName,
        DesktopDuplication = UseDesktopDuplication,
        CaptureCursor = CaptureCursor,
    };

    /// <summary>Fills the Capture card from <paramref name="scene"/>. A game that isn't running is named in <see cref="SceneCaptureNote"/>.</summary>
    private void LoadCaptureCard(SceneLayout scene)
    {
        var c = CaptureOf(scene);
        _applyingSceneCapture = true;
        try
        {
            var kind = c.Kind switch
            {
                SceneCaptureKind.Display => VideoSourceKind.Display,
                SceneCaptureKind.Window => VideoSourceKind.Window,
                SceneCaptureKind.TestPattern => VideoSourceKind.TestPattern,
                _ => VideoSourceKind.CameraOnly,
            };
            SelectedSourceKind = SourceKinds.First(k => k.Kind == kind);
            UseDesktopDuplication = c.DesktopDuplication;
            CaptureCursor = c.CaptureCursor;
            if (c.Kind == SceneCaptureKind.Display)
            {
                SelectedMonitor = FindMonitor(c) ?? SelectedMonitor;
            }

            SceneCaptureNote = null;
            if (c.Kind == SceneCaptureKind.Window)
            {
                var window = FindWindow(c);
                if (window is null)
                {
                    RefreshSources(); // the game may have started since the list was made
                    window = FindWindow(c);
                }

                SelectedWindow = window;
                if (window is null)
                {
                    SceneCaptureNote = $"{c.WindowProcess ?? c.WindowTitle ?? "This scene's game"} isn't running. Start it, then press Refresh, or pick another window.";
                }
            }
        }
        finally
        {
            _applyingSceneCapture = false;
        }
    }

    /// <summary>The user picked another capture on the card: it belongs to the active scene, and a running stream follows.</summary>
    private void OnCaptureCardChanged()
    {
        if (_applyingSceneCapture || _loading || Webcam is null)
        {
            return;
        }

        var capture = CardCapture();
        _defaultCapture = capture;
        SceneCaptureNote = null;
        Webcam.UpdateActiveSceneQuietly(s => s with { Capture = capture });
        RefreshMixerChannels();
        ScheduleLiveSourceSwitch();
    }

    private WindowInfo? FindWindow(SceneCapture c) =>
        Windows.FirstOrDefault(w => w.Title == c.WindowTitle && w.ProcessName == c.WindowProcess)
        ?? Windows.FirstOrDefault(w => w.ProcessName is not null && w.ProcessName == c.WindowProcess);

    private MonitorInfo? FindMonitor(SceneCapture c) =>
        Monitors.FirstOrDefault(m => m.DeviceName == c.MonitorDeviceName) ?? Monitors.FirstOrDefault(m => m.IsPrimary) ?? Monitors.FirstOrDefault();

    /// <summary>The capture a scene asks for, if it can run now (its window is open). Null for no capture.</summary>
    private ResolvedCapture? Resolve(SceneCapture c)
    {
        switch (c.Kind)
        {
            case SceneCaptureKind.Window when FindWindow(c) is { } w && VideoSourceCatalog.WindowExists(w.Handle):
                return new ResolvedCapture("w" + w.Handle.ToString(System.Globalization.CultureInfo.InvariantCulture), w.ToSource() with { CaptureCursor = c.CaptureCursor }, w);
            case SceneCaptureKind.Display when FindMonitor(c) is { } m:
                var api = c.DesktopDuplication ? CaptureApi.DesktopDuplication : CaptureApi.WindowsGraphicsCapture;
                return new ResolvedCapture($"d{m.DeviceName}{(c.DesktopDuplication ? "-dd" : string.Empty)}", m.ToSource(api) with { CaptureCursor = c.CaptureCursor }, null);
            case SceneCaptureKind.TestPattern:
                return new ResolvedCapture("test", new TestPatternSource(), null);
            default:
                return null;
        }
    }

    /// <summary>The scenes that go into a stream started from <paramref name="active"/>: its orientation, the active one first.</summary>
    private IEnumerable<SceneLayout> StreamScenes(SceneLayout active) =>
        Webcam.Layouts.Where(s => s.Id == active.Id).Concat(Webcam.Layouts.Where(s => s.Id != active.Id && s.Orientation == active.Orientation));

    /// <summary>
    /// Every distinct capture of the stream's scenes, the active scene's first (shown; the others start
    /// hidden). A scene whose game isn't running is left out, as are captures beyond the limit.
    /// </summary>
    private List<CaptureBinding> BuildCaptures(SceneLayout active)
    {
        var list = new List<CaptureBinding>();
        foreach (var scene in StreamScenes(active))
        {
            if (Resolve(CaptureOf(scene)) is not { } r || list.Any(c => c.Key == r.Key))
            {
                continue;
            }

            if (list.Count >= Linkast.Engine.FFmpeg.FfmpegArgumentBuilder.MaxCaptures)
            {
                AppendLog($"'{scene.Name}' has its own capture, but a stream can have at most {Linkast.Engine.FFmpeg.FfmpegArgumentBuilder.MaxCaptures}. Let it share another scene's game.");
                continue;
            }

            var shown = scene.Id == active.Id && active.ShowCapture;
            list.Add(new CaptureBinding(r.Key, r.Source, scene.BasePlacement, scene.BaseCrop, scene.BaseFit, StartHidden: !shown));
        }

        return list;
    }

    /// <summary>The game windows of the stream's scenes, one per window, for their own faders.</summary>
    private IEnumerable<WindowInfo> GameWindows()
    {
        var seen = new HashSet<long>();
        foreach (var scene in StreamScenes(Webcam.ActiveLayout))
        {
            if (Resolve(CaptureOf(scene)) is { Window: { ProcessId: > 0 } w } && seen.Add(w.Handle))
            {
                yield return w;
            }
        }
    }

    private static string GameChannel(WindowInfo w) => AudioChannels.ForGame(Path.GetFileNameWithoutExtension(w.ProcessName) ?? w.Handle.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The fader of the active scene's game, or null when the scene captures no window.</summary>
    private string? ActiveGameChannel =>
        Resolve(CaptureOf(Webcam.ActiveLayout)) is { Window: { } w } ? GameChannel(w) : null;

    // ---- While live ---------------------------------------------------------------------------

    /// <summary>Remembers what the running session captures, so only a real change restarts it.</summary>
    private void RememberLiveSources(StreamSession session)
    {
        _liveCaptures = session.Captures;
        _liveAudio = session.Audio;
        _liveStack = session.Stack;
    }

    private void ScheduleLiveSourceSwitch()
    {
        if (!IsActive || State == StreamState.Stopping)
        {
            return;
        }

        _liveSourceTimer ??= new DispatcherTimer(LiveSourceSettle, DispatcherPriority.Background, (_, _) => ApplyLiveSourceSwitch(), _ui);
        _liveSourceTimer.Stop();
        _liveSourceTimer.Start();
    }

    /// <summary>
    /// Makes the running stream capture what its scenes ask for. The same captures and sounds only need
    /// the active scene's capture shown; anything new or gone restarts the pipeline (a short pause).
    /// </summary>
    private void ApplyLiveSourceSwitch()
    {
        _liveSourceTimer?.Stop();
        if (_engine is null || !IsActive || State == StreamState.Stopping)
        {
            return;
        }

        var captures = BuildCaptures(Webcam.ActiveLayout);
        var audio = BuildAudioSources();
        var sameSources = captures.Select(c => (c.Key, c.Source)).SequenceEqual(_liveCaptures.Select(c => (c.Key, c.Source))) && SameSounds(audio, _liveAudio);
        if (sameSources && LiveStackFits(Webcam.ActiveLayout))
        {
            _baseDirty = true;
            _ = SendBaseAsync();
            return;
        }

        var stack = BuildStack(Webcam.ActiveLayout);
        if (_engine.ReplaceSources(captures, audio, stack))
        {
            _liveCaptures = captures;
            _liveAudio = audio;
            _liveStack = stack;
            AppendLog(sameSources
                ? $"'{Webcam.ActiveLayout.Name}' stacks its sources in a new order. Viewers see a short pause."
                : $"The stream now captures {(captures.Count == 0 ? "nothing (cameras and overlays only)" : string.Join(", ", captures.Select(c => Describe(c.Source))))}. Viewers see a short pause.");
        }
    }

    /// <summary>The same sounds, whatever their volumes: gains change live (a scene switch silences one game and brings in another).</summary>
    private static bool SameSounds(IReadOnlyList<AudioSourceSpec> a, IReadOnlyList<AudioSourceSpec> b) =>
        a.Select(s => s with { Gain = 1 }).SequenceEqual(b.Select(s => s with { Gain = 1 }));

    private static string Describe(VideoSourceSpec source) => source switch
    {
        WindowSource w => w.ProcessName is { } p ? Path.GetFileNameWithoutExtension(p) : w.Title,
        DisplaySource d => $"display {d.MonitorIndex + 1}",
        _ => "the test pattern",
    };

    /// <summary>
    /// Shows the active scene's capture where the scene wants it and hides every other one. A capture the
    /// stream doesn't have yet needs a restart, which <see cref="ApplyLiveSourceSwitch"/> does.
    /// </summary>
    private async Task SendCapturesAsync()
    {
        var scene = Webcam.ActiveLayout;
        var active = Resolve(CaptureOf(scene));
        if (active is not null && _liveCaptures.All(c => c.Key != active.Key))
        {
            ScheduleLiveSourceSwitch(); // not in the stream yet (judged by what was asked for, not a reconnect in between)
        }

        foreach (var c in _liveCaptures)
        {
            if (active?.Key == c.Key && scene.ShowCapture)
            {
                await _engine!.SetCaptureAsync(c.Key, scene.BasePlacement, scene.BaseCrop, scene.BaseFit);
            }
            else
            {
                await _engine!.SetCaptureAsync(c.Key, null, c.Crop, c.Fit);
            }
        }
    }

    /// <summary>While live, a captured game that closes is dropped from the stream (FFmpeg can't keep capturing it).</summary>
    private void UpdateWindowWatch()
    {
        _windowWatch ??= new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) =>
        {
            if (_liveCaptures.Any(c => c.Source is WindowSource w && !VideoSourceCatalog.WindowExists(w.WindowHandle)))
            {
                AppendLog("A captured game or window closed; the stream continues without it.");
                ApplyLiveSourceSwitch();
            }
        }, _ui);
        _windowWatch.IsEnabled = IsActive;
    }

    // ---- Each scene's overlays ------------------------------------------------------------------

    /// <summary>The user added an overlay to the active scene or took one out (it stays set up, ready to add again).</summary>
    private void OnOverlayVisibilityChanged(OverlayItemViewModel item) =>
        Webcam.UpdateActiveScene(s => item.Visible ? s.WithOverlay(item.LayerId, item.ToSceneOverlay()) : s.WithoutOverlay(item.LayerId));

    /// <summary>Remove on an overlay's card: out of this scene, and deleted (with its link) when no other scene has it.</summary>
    private void OnOverlayRemoveRequested(OverlayItemViewModel item)
    {
        Webcam.UpdateActiveScene(s => s.WithoutOverlay(item.LayerId));
        if (!Webcam.Layouts.Any(s => s.Shows(item.LayerId)))
        {
            Overlays.Delete(item);
            AppendLog($"'{item.Name}' was in no other scene, so it was deleted.");
        }
    }

    /// <summary>An overlay was moved, resized or trimmed: that is its setup in the active scene.</summary>
    private void OnOverlaySceneSetupChanged(OverlayItemViewModel item) =>
        Webcam.UpdateActiveSceneQuietly(s => s.WithOverlay(item.LayerId, item.ToSceneOverlay()));

    /// <summary>Copies another scene's overlay into the active scene with that scene's place, size and trim.</summary>
    private void OnOverlayCopyRequested(OverlayFromScene choice)
    {
        Webcam.UpdateActiveScene(s => s.WithOverlay(choice.Item.LayerId, choice.Setup));
        AppendLog(choice.Scene is { } from
            ? $"'{choice.Item.Name}' added to '{Webcam.ActiveLayout.Name}' as it is in '{from.Name}'."
            : $"'{choice.Item.Name}' added to '{Webcam.ActiveLayout.Name}'.");
    }

    /// <summary>An overlay that no scene shows (for example one imported from OBS) goes into the active scene.</summary>
    private void AdoptOrphanOverlays()
    {
        foreach (var item in Overlays.Items.Where(o => !Webcam.Layouts.Any(s => s.Shows(o.LayerId))).ToList())
        {
            Webcam.UpdateActiveSceneQuietly(s => s.WithOverlay(item.LayerId, item.ToSceneOverlay()));
        }
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using Linkast.Core.Camera;

namespace Linkast.App.ViewModels;

/// <summary>The scene's game or screen as a row of the sources list (its settings live on the main view model).</summary>
public sealed class CaptureSourceItem
{
    public static CaptureSourceItem Instance { get; } = new();

    private CaptureSourceItem()
    {
    }
}

/// <summary>
/// The order sources are stacked in, per scene: the sources list shows the active scene's order, first =
/// bottom, and dragging a row restacks it. The stream has one running stack for all its scenes; a live
/// switch to a scene that doesn't fit it, or a reorder while live, restacks the stream (a short pause).
/// </summary>
public sealed partial class MainViewModel
{
    private IReadOnlyList<string> _liveStack = [];

    /// <summary>The active scene's sources, bottom of the stack first: the game, cameras and overlays.</summary>
    public ObservableCollection<object> SceneSources { get; } = [];

    /// <summary>The active scene's stacking order for the preview, by ID (<see cref="SceneStacking.CaptureId"/> for the game).</summary>
    public IReadOnlyList<string> PreviewStack { get; private set; } = [];

    /// <summary>Every source in its default place: the game, then cameras, then overlays.</summary>
    private List<string> DefaultSourceOrder() =>
        [SceneStacking.CaptureId, .. Webcam.Layers.Select(l => l.LayerId), .. Overlays.Items.Select(o => o.LayerId)];

    private IReadOnlyList<string> SceneOrder(SceneLayout scene) => SceneStacking.Resolve(scene.StackOrder, DefaultSourceOrder());

    /// <summary>What <paramref name="scene"/> shows, in its order, as the stream names it (the game by its capture key).</summary>
    private List<string> ShownOrder(SceneLayout scene)
    {
        var captureKey = scene.ShowCapture ? Resolve(CaptureOf(scene))?.Key : null;
        var shown = new List<string>();
        foreach (var id in SceneOrder(scene))
        {
            if (id == SceneStacking.CaptureId)
            {
                if (captureKey is not null)
                {
                    shown.Add(captureKey);
                }
            }
            else if (scene.CameraPlacements.ContainsKey(id) || scene.Shows(id))
            {
                shown.Add(id);
            }
        }

        return shown;
    }

    /// <summary>The stream's one stack: the starting scene's order, then every other scene's where it fits.</summary>
    private IReadOnlyList<string> BuildStack(SceneLayout active) =>
        SceneStacking.Merge([.. StreamScenes(active).Select(ShownOrder)]);

    /// <summary>Makes the sources list and the preview follow the active scene's order.</summary>
    private void RefreshSceneSources()
    {
        if (Webcam is null)
        {
            return;
        }

        var order = SceneOrder(Webcam.ActiveLayout);
        var items = new List<object>();
        foreach (var id in order)
        {
            object? item = id == SceneStacking.CaptureId
                ? CaptureSourceItem.Instance
                : (object?)Webcam.Layers.FirstOrDefault(l => l.LayerId == id) ?? Overlays.Items.FirstOrDefault(o => o.LayerId == id);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        // Moves rather than a reset, so the rows keep their state (and a drag in progress its target).
        for (var i = SceneSources.Count - 1; i >= 0; i--)
        {
            if (!items.Contains(SceneSources[i]))
            {
                SceneSources.RemoveAt(i);
            }
        }

        for (var i = 0; i < items.Count; i++)
        {
            var at = SceneSources.IndexOf(items[i]);
            if (at < 0)
            {
                SceneSources.Insert(i, items[i]);
            }
            else if (at != i)
            {
                SceneSources.Move(at, i);
            }
        }

        PreviewStack = order;
        OnPropertyChanged(nameof(PreviewStack));
    }

    private static string? SourceId(object item) => item switch
    {
        CaptureSourceItem => SceneStacking.CaptureId,
        CameraLayerViewModel c => c.LayerId,
        OverlayItemViewModel o => o.LayerId,
        _ => null,
    };

    /// <summary>Moves a source to <paramref name="index"/> in the active scene's stack (0 = bottom). While live the stream restacks.</summary>
    public void MoveSource(object item, int index)
    {
        if (SourceId(item) is not { } id)
        {
            return;
        }

        var order = SceneOrder(Webcam.ActiveLayout);
        var moved = SceneStacking.Move(order, id, index);
        if (moved.SequenceEqual(order))
        {
            return;
        }

        Webcam.UpdateActiveSceneQuietly(s => s with { StackOrder = moved });
        RefreshSceneSources();
        ScheduleLiveSourceSwitch();
    }

    /// <summary>One step towards the front (+1) or the back (-1) of the active scene's stack.</summary>
    public void StepSource(object item, int delta)
    {
        if (SourceId(item) is { } id && SceneOrder(Webcam.ActiveLayout).ToList().IndexOf(id) is >= 0 and var at)
        {
            MoveSource(item, at + delta);
        }
    }

    /// <summary>True when the running stream's stack draws <paramref name="scene"/> in its order (a switch to it stays instant).</summary>
    private bool LiveStackFits(SceneLayout scene) => SceneStacking.Fits(_liveStack, ShownOrder(scene));
}

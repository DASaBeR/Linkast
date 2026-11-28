// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Windows.Media;
using Linkast.App.ViewModels;
using Linkast.Core.Camera;

namespace Linkast.App.Tests;

public class VerticalSceneItemTests
{
    private sealed class FakeSceneItem(string layerId, string name) : ISceneItem
    {
        public string Name => name;
        public string LayerId => layerId;
        public NormalizedRect Placement { get; set; } = NormalizedRect.Full;
        public NormalizedRect Crop { get; set; } = NormalizedRect.Full;
        public bool IsShown { get; set; } = true;
        public bool IsSelected { get; set; }
        public ImageSource? Preview => null;
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }

    [Fact]
    public void Camera_vertical_item_reads_and_writes_vertical_scene_without_touching_active_camera_item()
    {
        var activeSource = new FakeSceneItem("camera-1", "Face cam")
        {
            Placement = new NormalizedRect(0.695, 0.68, 0.28, 0.28),
            Crop = new NormalizedRect(0.1, 0.1, 0.8, 0.8),
            IsSelected = false,
        };
        var verticalScene = SceneLayout.DefaultVertical("camera-1");

        using var item = new VerticalSceneItem(
            activeSource,
            isOverlay: false,
            () => verticalScene,
            update => verticalScene = update(verticalScene),
            () => 1080,
            () => 1920);

        Assert.True(item.IsShown);
        Assert.Equal(SceneLayout.VerticalGameTopCamera, item.Placement);
        Assert.Equal(activeSource.Crop, item.Crop);

        var moved = new NormalizedRect(0.1, 0.2, 0.5, 0.3);
        item.Placement = moved;

        Assert.Equal(moved, verticalScene.CameraPlacements["camera-1"]);
        Assert.Equal(moved, item.Placement);
        Assert.Equal(new NormalizedRect(0.695, 0.68, 0.28, 0.28), activeSource.Placement);

        item.IsSelected = true;
        Assert.False(activeSource.IsSelected);

        var newCrop = new NormalizedRect(0.05, 0.05, 0.9, 0.9);
        item.Crop = newCrop;
        Assert.Equal(newCrop, activeSource.Crop);
    }

    [Fact]
    public void Overlay_vertical_item_stores_custom_placement_and_crop_per_vertical_scene()
    {
        var overlaySource = new FakeSceneItem("overlay-1", "Alerts");
        var verticalScene = SceneLayout.DefaultVertical("camera-1")
            .WithOverlay("overlay-1", new SceneOverlay { Position = OverlayPosition.Custom, CustomPlacement = new NormalizedRect(0.1, 0.1, 0.4, 0.2) });

        using var item = new VerticalSceneItem(
            overlaySource,
            isOverlay: true,
            () => verticalScene,
            update => verticalScene = update(verticalScene),
            () => 1080,
            () => 1920);

        Assert.True(item.IsShown);
        Assert.Equal(new NormalizedRect(0.1, 0.1, 0.4, 0.2), item.Placement);

        var moved = new NormalizedRect(0.2, 0.3, 0.5, 0.25);
        item.Placement = moved;
        item.Crop = new NormalizedRect(0.1, 0, 0.8, 1);

        Assert.Equal(moved, verticalScene.Overlays["overlay-1"].CustomPlacement);
        Assert.Equal(OverlayPosition.Custom, verticalScene.Overlays["overlay-1"].Position);
        Assert.Equal(new NormalizedRect(0.1, 0, 0.8, 1), verticalScene.Overlays["overlay-1"].Crop);
    }
}

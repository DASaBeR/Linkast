// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Linkast.Core.Camera;

namespace Linkast.App.ViewModels;

/// <summary>
/// Wraps a camera layer or overlay for an inactive (vertical) scene so <see cref="Views.SceneEditor"/>
/// reads and writes that scene's placement, trim and selection independently from the active scene.
/// </summary>
public sealed partial class VerticalSceneItem : ObservableObject, ISceneItem, IDisposable
{
    private readonly ISceneItem _source;
    private readonly bool _isOverlay;
    private readonly Func<SceneLayout?> _getScene;
    private readonly Action<Func<SceneLayout, SceneLayout>> _updateScene;
    private readonly Func<double> _canvasWidth;
    private readonly Func<double> _canvasHeight;

    public VerticalSceneItem(
        ISceneItem source,
        bool isOverlay,
        Func<SceneLayout?> getScene,
        Action<Func<SceneLayout, SceneLayout>> updateScene,
        Func<double> canvasWidth,
        Func<double> canvasHeight)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _isOverlay = isOverlay;
        _getScene = getScene ?? throw new ArgumentNullException(nameof(getScene));
        _updateScene = updateScene ?? throw new ArgumentNullException(nameof(updateScene));
        _canvasWidth = canvasWidth ?? throw new ArgumentNullException(nameof(canvasWidth));
        _canvasHeight = canvasHeight ?? throw new ArgumentNullException(nameof(canvasHeight));
        _source.PropertyChanged += OnSourcePropertyChanged;
    }

    public string Name => _source.Name;

    public string LayerId => _source.LayerId;

    public bool IsOverlay => _isOverlay;

    public ImageSource? Preview => _source.Preview;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public bool IsShown
    {
        get
        {
            var scene = _getScene();
            if (scene is null)
            {
                return false;
            }

            return _isOverlay ? scene.Shows(LayerId) : scene.CameraPlacements.ContainsKey(LayerId);
        }
    }

    public NormalizedRect Placement
    {
        get
        {
            var scene = _getScene();
            if (scene is null)
            {
                return NormalizedRect.Full;
            }

            if (_isOverlay)
            {
                if (!scene.Overlays.TryGetValue(LayerId, out var setup))
                {
                    return NormalizedRect.Full;
                }

                if (setup.Position == OverlayPosition.Custom && setup.CustomPlacement is { } custom)
                {
                    return custom.Clamped();
                }

                var aspect = _source is OverlayItemViewModel ov ? ov.Aspect : 16.0 / 9;
                return OverlayGeometry.Place(setup.Position, setup.Size, aspect, _canvasWidth(), _canvasHeight());
            }

            return scene.CameraPlacements.TryGetValue(LayerId, out var rect) ? rect.Clamped() : NormalizedRect.Full;
        }
        set
        {
            var rect = value.Clamped();
            if (rect == Placement && IsShown)
            {
                return;
            }

            if (_isOverlay)
            {
                _updateScene(s =>
                {
                    var setup = (s.Overlays.GetValueOrDefault(LayerId) ?? new SceneOverlay()).Clamped();
                    return s.WithOverlay(LayerId, setup with { Position = OverlayPosition.Custom, CustomPlacement = rect });
                });
            }
            else
            {
                _updateScene(s => s.WithPlacement(LayerId, rect));
            }

            OnPropertyChanged(nameof(Placement));
            OnPropertyChanged(nameof(IsShown));
        }
    }

    public NormalizedRect Crop
    {
        get
        {
            if (!_isOverlay)
            {
                return _source.Crop;
            }

            var scene = _getScene();
            return scene is not null && scene.Overlays.TryGetValue(LayerId, out var setup)
                ? setup.Crop.Clamped(SceneLayout.MinCrop)
                : NormalizedRect.Full;
        }
        set
        {
            var crop = value.Clamped(SceneLayout.MinCrop);
            if (!_isOverlay)
            {
                _source.Crop = crop;
                OnPropertyChanged(nameof(Crop));
                return;
            }

            if (crop == Crop)
            {
                return;
            }

            _updateScene(s =>
            {
                var setup = (s.Overlays.GetValueOrDefault(LayerId) ?? new SceneOverlay()).Clamped();
                return s.WithOverlay(LayerId, setup with { Crop = crop });
            });
            OnPropertyChanged(nameof(Crop));
        }
    }

    public void NotifySceneUpdated()
    {
        OnPropertyChanged(nameof(Placement));
        OnPropertyChanged(nameof(Crop));
        OnPropertyChanged(nameof(IsShown));
    }

    private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ISceneItem.Preview):
                OnPropertyChanged(nameof(Preview));
                break;
            case nameof(ISceneItem.Name):
                OnPropertyChanged(nameof(Name));
                break;
            case nameof(ISceneItem.Crop) when !_isOverlay:
                OnPropertyChanged(nameof(Crop));
                break;
        }
    }

    public void Dispose() => _source.PropertyChanged -= OnSourcePropertyChanged;
}

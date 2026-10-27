// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Windows.Media;
using Linkast.Core.Camera;

namespace Linkast.App.ViewModels;

/// <summary>
/// Something the scene editor can select, drag and resize: a camera layer or an overlay. The editor
/// listens for changes of these properties by name.
/// </summary>
public interface ISceneItem : INotifyPropertyChanged
{
    string Name { get; }

    /// <summary>Where it is on the output canvas, normalized. The editor sets it when the user drags.</summary>
    NormalizedRect Placement { get; set; }

    /// <summary>False when the item is not on the canvas at all (hidden camera, overlay switched off).</summary>
    bool IsShown { get; }

    bool IsSelected { get; set; }

    /// <summary>The latest picture, or null when there is none yet (the editor shows a placeholder).</summary>
    ImageSource? Preview { get; }
}

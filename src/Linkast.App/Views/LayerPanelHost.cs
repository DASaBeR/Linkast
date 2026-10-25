// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Linkast.App.Views;

/// <summary>
/// Hosts one settings panel per camera layer, showing only the selected one. The default
/// ItemsControl automation peer keeps the item tree it saw first, so after switching cameras
/// screen readers could not reach the newly shown panel. This peer exposes the live visual
/// children instead, and is refreshed whenever the selection changes.
/// </summary>
public sealed class LayerPanelHost : ItemsControl
{
    private FrameworkElementAutomationPeer? _peer;

    protected override AutomationPeer OnCreateAutomationPeer() => _peer = new FrameworkElementAutomationPeer(this);

    /// <summary>Tells automation clients that a different panel is now visible.</summary>
    public void RefreshAutomation()
    {
        _peer?.ResetChildrenCache();
        _peer?.RaiseAutomationEvent(AutomationEvents.StructureChanged);
    }
}

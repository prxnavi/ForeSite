using UnityEngine.XR.Interaction.Toolkit.UI;

namespace Cascade
{
    /// Panels always win over 3D colliders. With the default (equal) priority the EventSystem picks
    /// the nearest hit, so a wall, equipment body or issue pin closer than the panel stole the click
    /// whenever you stood near one in walk-in mode.
    public class PanelRaycaster : TrackedDeviceGraphicRaycaster
    {
        public override int sortOrderPriority => 100;
    }
}

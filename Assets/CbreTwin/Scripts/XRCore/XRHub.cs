using UnityEngine;

namespace Keystone.XR
{
    public enum XRButton { A, B, X, Y, LeftTrigger, RightTrigger, LeftGrip, RightGrip, Menu }

    /// <summary>A head-mounted display rig: head, two tracked controllers and their inputs.</summary>
    public interface IXRRig
    {
        string Name { get; }
        /// <summary>True when a headset is connected and rendering.</summary>
        bool HeadsetActive { get; }
        /// <summary>Creates the camera rig in the scene. Call once when entering VR.</summary>
        void Spawn();
        Transform Root { get; }
        Transform Head { get; }
        Transform LeftHand { get; }
        Transform RightHand { get; }
        Camera EyeCamera { get; }
        Vector2 LeftStick { get; }
        Vector2 RightStick { get; }
        bool Down(XRButton b);
        bool Held(XRButton b);
        bool Up(XRButton b);
        void Haptic(bool right, float amplitude);
    }

    /// <summary>
    /// The app depends only on this hub; the Meta XR implementation (compiled only when
    /// com.meta.xr.sdk.core is installed) registers itself before the first scene loads.
    /// </summary>
    public static class XRHub
    {
        public static IXRRig Rig { get; private set; }
        public static void Register(IXRRig rig) => Rig = rig;
        public static bool HeadsetActive => Rig != null && Rig.HeadsetActive;
    }
}

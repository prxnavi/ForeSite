using UnityEngine;

namespace Keystone.XR
{
    /// <summary>
    /// Meta Quest rig built from the Meta XR Core SDK: OVRManager + OVRCameraRig
    /// (head and hand anchors are created by the rig itself) and OVRInput for Touch controllers.
    /// </summary>
    public class MetaXRRig : MonoBehaviour, IXRRig
    {
        OVRCameraRig rig;
        float hapticUntil;

        public string Name => "Meta XR Core SDK";
        public bool HeadsetActive => UnityEngine.XR.XRSettings.isDeviceActive || OVRManager.isHmdPresent;
        public Transform Root => rig != null ? rig.transform : null;
        public Transform Head => rig != null ? rig.centerEyeAnchor : null;
        public Transform LeftHand => rig != null ? rig.leftHandAnchor : null;
        public Transform RightHand => rig != null ? rig.rightHandAnchor : null;
        public Camera EyeCamera => rig != null ? rig.centerEyeAnchor.GetComponent<Camera>() : null;
        public Vector2 LeftStick => OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
        public Vector2 RightStick => OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install()
        {
            var go = new GameObject("Meta XR");
            DontDestroyOnLoad(go);
            XRHub.Register(go.AddComponent<MetaXRRig>());
        }

        public void Spawn()
        {
            if (rig != null) return;
            var go = new GameObject("OVR Camera Rig");
            go.SetActive(false);
            DontDestroyOnLoad(go);
            var manager = go.AddComponent<OVRManager>();
            manager.trackingOriginType = OVRManager.TrackingOrigin.FloorLevel;
            rig = go.AddComponent<OVRCameraRig>();
            go.SetActive(true);
            rig.EnsureGameObjectIntegrity();
            var cam = EyeCamera;
            if (cam != null)
            {
                cam.tag = "MainCamera";
                cam.nearClipPlane = 0.03f;
                cam.farClipPlane = 300f;
            }
        }

        static (OVRInput.Button button, OVRInput.Controller hand) Map(XRButton b) => b switch
        {
            XRButton.A => (OVRInput.Button.One, OVRInput.Controller.RTouch),
            XRButton.B => (OVRInput.Button.Two, OVRInput.Controller.RTouch),
            XRButton.X => (OVRInput.Button.One, OVRInput.Controller.LTouch),
            XRButton.Y => (OVRInput.Button.Two, OVRInput.Controller.LTouch),
            XRButton.LeftTrigger => (OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.LTouch),
            XRButton.RightTrigger => (OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch),
            XRButton.LeftGrip => (OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.LTouch),
            XRButton.RightGrip => (OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch),
            _ => (OVRInput.Button.Start, OVRInput.Controller.LTouch),
        };

        public bool Down(XRButton b) { var (k, c) = Map(b); return OVRInput.GetDown(k, c); }
        public bool Held(XRButton b) { var (k, c) = Map(b); return OVRInput.Get(k, c); }
        public bool Up(XRButton b) { var (k, c) = Map(b); return OVRInput.GetUp(k, c); }

        public void Haptic(bool right, float amplitude)
        {
            OVRInput.SetControllerVibration(0.5f, Mathf.Clamp01(amplitude), right ? OVRInput.Controller.RTouch : OVRInput.Controller.LTouch);
            hapticUntil = Time.unscaledTime + 0.06f;
        }

        void Update()
        {
            if (hapticUntil > 0 && Time.unscaledTime > hapticUntil)
            {
                hapticUntil = 0;
                OVRInput.SetControllerVibration(0, 0, OVRInput.Controller.RTouch);
                OVRInput.SetControllerVibration(0, 0, OVRInput.Controller.LTouch);
            }
        }
    }
}

using UnityEngine;

namespace Atlas
{
    /// Editor/desktop stand-in for a headset: right-drag to orbit, scroll to zoom.
    /// Disables itself when an XR device is active.
    public class DesktopOrbit : MonoBehaviour
    {
        public Vector3 target = new(0f, 0.08f, 0.05f);
        public float yaw = 0f, pitch = 30f, distance = 1.15f;

        void Start()
        {
            if (UnityEngine.XR.XRSettings.isDeviceActive) enabled = false;
        }

        void LateUpdate()
        {
            if (Input.GetMouseButton(1))
            {
                yaw += Input.GetAxis("Mouse X") * 3f;
                pitch = Mathf.Clamp(pitch - Input.GetAxis("Mouse Y") * 3f, 8f, 80f);
            }
            distance = Mathf.Clamp(distance - Input.mouseScrollDelta.y * 0.06f, 0.5f, 3f);
            var rot = Quaternion.Euler(pitch, yaw, 0f);
            transform.SetPositionAndRotation(target + rot * new Vector3(0, 0, -distance), rot);
        }
    }
}

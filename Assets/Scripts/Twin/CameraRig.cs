using UnityEngine;
using UnityEngine.InputSystem;

namespace CbreTwin
{
    public enum ViewMode { Orbit, Walk, Plan }

    /// <summary>Orbit / first-person walkthrough / top-down plan camera (new Input System).</summary>
    public class CameraRig : MonoBehaviour
    {
        public Camera Cam;
        public ViewMode Mode = ViewMode.Orbit;
        public System.Func<Vector2, bool> IsOverUI = _ => false;
        public System.Action<Vector2> OnClick;

        Vector3 target, targetGoal;
        float yaw = 35f, pitch = 38f, dist = 28f, distGoal = 28f;
        Vector3 walkPos;
        float walkYaw, walkPitch;
        float walkFloorY;
        Vector2 pressPos;
        bool dragging;

        public Vector3 Position => Cam.transform.position;
        public float Yaw => Mode == ViewMode.Walk ? walkYaw : yaw;

        public void Frame(Bounds b)
        {
            targetGoal = b.center;
            distGoal = Mathf.Clamp(b.extents.magnitude * 2.1f, 10f, 70f);
            if (Mode == ViewMode.Plan) ApplyPlan(b);
        }

        public void FocusOn(Vector3 p, float d = 9f)
        {
            if (Mode == ViewMode.Walk)
            {
                walkPos = new Vector3(p.x, walkFloorY + 5.4f * BlueprintBuilder.FT, p.z);
                return;
            }
            targetGoal = p;
            distGoal = d;
        }

        public void SetMode(ViewMode m, Bounds b, float floorBaseY)
        {
            Mode = m;
            Cam.orthographic = m == ViewMode.Plan;
            walkFloorY = floorBaseY;
            if (m == ViewMode.Walk)
            {
                var c = b.center;
                walkPos = new Vector3(c.x, floorBaseY + 5.4f * BlueprintBuilder.FT, c.z - b.extents.z * 0.3f);
                walkYaw = 0;
                walkPitch = 0;
                Cam.fieldOfView = 70f;
            }
            else if (m == ViewMode.Plan)
            {
                ApplyPlan(b);
            }
            else
            {
                Cam.fieldOfView = 45f;
                Frame(b);
            }
        }

        public void SetWalkFloor(float floorBaseY)
        {
            walkFloorY = floorBaseY;
            walkPos.y = floorBaseY + 5.4f * BlueprintBuilder.FT;
        }

        void ApplyPlan(Bounds b)
        {
            Cam.orthographicSize = Mathf.Max(b.extents.x * 0.75f, b.extents.z * 1.15f) + 2f;
            targetGoal = b.center;
        }

        void LateUpdate()
        {
            if (Cam == null) return;
            var mouse = Mouse.current;
            var kb = Keyboard.current;
            float dt = Time.unscaledDeltaTime;
            Vector2 mp = mouse != null ? mouse.position.ReadValue() : Vector2.zero;
            bool overUI = IsOverUI(mp);

            // Click vs drag detection for selection
            if (mouse != null)
            {
                if (mouse.leftButton.wasPressedThisFrame && !overUI) { pressPos = mp; dragging = true; }
                if (mouse.leftButton.wasReleasedThisFrame && dragging)
                {
                    dragging = false;
                    if ((mp - pressPos).sqrMagnitude < 25f) OnClick?.Invoke(mp);
                }
            }

            bool typing = TwinUI.IsTyping;
            if (Mode == ViewMode.Walk) UpdateWalk(mouse, kb, dt, overUI, typing);
            else UpdateOrbit(mouse, kb, dt, overUI, typing);
        }

        void UpdateOrbit(Mouse mouse, Keyboard kb, float dt, bool overUI, bool typing)
        {
            if (mouse != null && !overUI)
            {
                var delta = mouse.delta.ReadValue();
                bool rotate = mouse.rightButton.isPressed || (mouse.leftButton.isPressed && dragging && Mode == ViewMode.Orbit);
                if (rotate && Mode == ViewMode.Orbit)
                {
                    yaw += delta.x * 0.25f;
                    pitch = Mathf.Clamp(pitch - delta.y * 0.2f, 8f, 85f);
                }
                if (mouse.middleButton.isPressed || (Mode == ViewMode.Plan && mouse.leftButton.isPressed && dragging))
                {
                    var right = Cam.transform.right; var fwd = Vector3.Cross(right, Vector3.up);
                    float k = (Mode == ViewMode.Plan ? Cam.orthographicSize * 2f / Screen.height : dist * 0.0018f);
                    targetGoal -= (right * delta.x + fwd * delta.y) * k;
                }
                float scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f)
                {
                    float s = Mathf.Sign(scroll) * Mathf.Min(1f, Mathf.Abs(scroll) / 120f + 0.1f);
                    if (Mode == ViewMode.Plan) Cam.orthographicSize = Mathf.Clamp(Cam.orthographicSize * (1f - s * 0.12f), 3f, 60f);
                    else distGoal = Mathf.Clamp(distGoal * (1f - s * 0.12f), 3f, 90f);
                }
            }
            if (kb != null && !typing && Mode == ViewMode.Orbit)
            {
                if (kb.qKey.isPressed) yaw -= 60f * dt;
                if (kb.eKey.isPressed) yaw += 60f * dt;
            }

            target = Vector3.Lerp(target, targetGoal, 1f - Mathf.Exp(-8f * dt));
            dist = Mathf.Lerp(dist, distGoal, 1f - Mathf.Exp(-8f * dt));
            if (Mode == ViewMode.Plan)
            {
                Cam.transform.position = target + Vector3.up * 60f;
                Cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            }
            else
            {
                var rot = Quaternion.Euler(pitch, yaw, 0);
                Cam.transform.position = target + rot * new Vector3(0, 0, -dist);
                Cam.transform.rotation = rot;
            }
        }

        void UpdateWalk(Mouse mouse, Keyboard kb, float dt, bool overUI, bool typing)
        {
            if (mouse != null && (mouse.rightButton.isPressed || (mouse.leftButton.isPressed && dragging)) && !overUI)
            {
                var d = mouse.delta.ReadValue();
                walkYaw += d.x * 0.18f;
                walkPitch = Mathf.Clamp(walkPitch - d.y * 0.15f, -70f, 70f);
            }
            if (kb != null && !typing)
            {
                var move = Vector3.zero;
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) move.z += 1;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) move.z -= 1;
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) move.x -= 1;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) move.x += 1;
                float speed = (kb.leftShiftKey.isPressed ? 4.5f : 2.2f);
                var rot = Quaternion.Euler(0, walkYaw, 0);
                walkPos += rot * move.normalized * speed * dt;
            }
            Cam.transform.position = Vector3.Lerp(Cam.transform.position, walkPos, 1f - Mathf.Exp(-14f * dt));
            Cam.transform.rotation = Quaternion.Euler(walkPitch, walkYaw, 0);
        }
    }
}

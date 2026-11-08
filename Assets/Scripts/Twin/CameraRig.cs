using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CbreTwin
{
    public enum ViewMode { Orbit, Plan, Tour }

    /// <summary>
    /// Orbit / plan / immersive first-person tour.
    /// Tour mode is built around VR comfort conventions: eye-height camera, a
    /// collision capsule that respects walls and doorways, snap turning,
    /// fade-teleport, a centre reticle for gaze selection and a guided route.
    /// </summary>
    public class CameraRig : MonoBehaviour
    {
        public Camera Cam;
        public ViewMode Mode = ViewMode.Orbit;
        public Func<Vector2, bool> IsOverUI = _ => false;
        public Action<Vector2> OnClick;
        public Action OnGuidedArrive;
        public Action<Vector3> OnTeleportRequest;   // UI fades, then calls Teleport()
        public Action OnReticleSelect;
        public bool AutoRotate;

        public const float EyeHeight = 1.62f;
        public float WalkSpeed = 1.5f;

        // Orbit state
        Vector3 target, targetGoal;
        float yaw = 32f, pitch = 34f, dist = 28f, distGoal = 28f;
        Vector2 pressPos;
        bool dragging;

        // Tour state
        Transform visitor;
        CharacterController cc;
        Light headLamp;
        float tourYaw, tourPitch, floorY;
        readonly Queue<Vector3> guided = new();
        float guidedLookYaw;
        bool guidedActive;

        public float CurrentSpeed { get; private set; }
        public bool Guided => guidedActive;
        public bool PointerLocked => Cursor.lockState == CursorLockMode.Locked;
        public Vector3 Position => Cam.transform.position;
        public float Yaw => Mode == ViewMode.Tour ? tourYaw : yaw;
        public Vector3 VisitorFeet => visitor != null ? visitor.position : Vector3.zero;

        // ───────────── Orbit / plan ─────────────

        public void Frame(Bounds b)
        {
            targetGoal = b.center;
            distGoal = Mathf.Clamp(b.extents.magnitude * 2.0f, 10f, 70f);
            if (Mode == ViewMode.Plan) ApplyPlan(b);
        }

        public void FocusOn(Vector3 p, float d = 9f)
        {
            if (Mode == ViewMode.Tour) return;
            targetGoal = p;
            distGoal = d;
        }

        public void SetMode(ViewMode m, Bounds b)
        {
            if (Mode == ViewMode.Tour && m != ViewMode.Tour) ExitTour();
            Mode = m;
            Cam.orthographic = m == ViewMode.Plan;
            if (m == ViewMode.Plan) ApplyPlan(b);
            else if (m == ViewMode.Orbit)
            {
                Cam.fieldOfView = 40f;
                Frame(b);
            }
        }

        void ApplyPlan(Bounds b)
        {
            Cam.orthographicSize = Mathf.Max(b.extents.x * 0.72f, b.extents.z * 1.1f) + 2f;
            targetGoal = b.center;
        }

        // ───────────── Tour ─────────────

        public void EnterTour(Vector3 feetPos, float facingYaw)
        {
            Mode = ViewMode.Tour;
            Cam.orthographic = false;
            Cam.fieldOfView = 74f;
            if (visitor == null)
            {
                visitor = new GameObject("Visitor").transform;
                visitor.gameObject.layer = BlueprintBuilder.WallLayer;
                cc = visitor.gameObject.AddComponent<CharacterController>();
                cc.radius = 0.22f;
                cc.height = 1.55f;
                cc.center = new Vector3(0, 0.1f + 1.55f / 2f, 0); // float just above the slab
                cc.stepOffset = 0.05f;
                cc.skinWidth = 0.02f;
                cc.minMoveDistance = 0f;
                headLamp = new GameObject("Head lamp").AddComponent<Light>();
                headLamp.type = LightType.Point;
                headLamp.range = 7f;
                headLamp.intensity = 0.9f;
                headLamp.color = Palette.Hex("#FFF3E2");
                headLamp.shadows = LightShadows.None;
                headLamp.transform.SetParent(visitor, false);
                headLamp.transform.localPosition = new Vector3(0, 2.1f, 0);
            }
            visitor.gameObject.SetActive(true);
            Teleport(feetPos);
            tourYaw = facingYaw;
            tourPitch = 0;
            Cam.transform.position = visitor.position + Vector3.up * EyeHeight;
        }

        public void ExitTour()
        {
            guided.Clear();
            guidedActive = false;
            if (visitor != null) visitor.gameObject.SetActive(false);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        public void Teleport(Vector3 feetPos)
        {
            if (visitor == null) return;
            guided.Clear();
            guidedActive = false;
            cc.enabled = false;
            visitor.position = feetPos;
            floorY = feetPos.y;
            cc.enabled = true;
        }

        public void FaceToward(Vector3 p)
        {
            var d = p - VisitorFeet;
            d.y = 0;
            if (d.sqrMagnitude > 0.01f) tourYaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        }

        /// <summary>Walk the visitor through a series of waypoints (door to door).</summary>
        public void Guide(IEnumerable<Vector3> feetPoints, Vector3? lookAt = null)
        {
            guided.Clear();
            foreach (var p in feetPoints) guided.Enqueue(p);
            guidedActive = guided.Count > 0;
            if (lookAt.HasValue)
            {
                var d = lookAt.Value - (guided.Count > 0 ? ToArray(guided)[guided.Count - 1] : VisitorFeet);
                guidedLookYaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            }
            else guidedLookYaw = float.NaN;
        }

        static Vector3[] ToArray(Queue<Vector3> q) => q.ToArray();

        public void ReleasePointer()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        // ───────────── Update ─────────────

        void LateUpdate()
        {
            if (Cam == null) return;
            var mouse = Mouse.current;
            var kb = Keyboard.current;
            float dt = Time.unscaledDeltaTime;
            Vector2 mp = mouse != null ? mouse.position.ReadValue() : Vector2.zero;
            bool overUI = !PointerLocked && IsOverUI(mp);

            if (mouse != null)
            {
                if (mouse.leftButton.wasPressedThisFrame && !overUI) { pressPos = mp; dragging = true; }
                if (mouse.leftButton.wasReleasedThisFrame && dragging)
                {
                    dragging = false;
                    if ((mp - pressPos).sqrMagnitude < 25f)
                    {
                        if (Mode == ViewMode.Tour && PointerLocked) OnReticleSelect?.Invoke();
                        else OnClick?.Invoke(mp);
                    }
                }
            }

            bool typing = TwinUI.IsTyping;
            if (Mode == ViewMode.Tour) UpdateTour(mouse, kb, dt, overUI, typing);
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
                    yaw += delta.x * 0.22f;
                    pitch = Mathf.Clamp(pitch - delta.y * 0.18f, 6f, 85f);
                }
                if (mouse.middleButton.isPressed || (Mode == ViewMode.Plan && mouse.leftButton.isPressed && dragging))
                {
                    var right = Cam.transform.right;
                    var fwd = Vector3.Cross(right, Vector3.up);
                    float k = Mode == ViewMode.Plan ? Cam.orthographicSize * 2f / Screen.height : dist * 0.0016f;
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
                if (kb.qKey.isPressed) yaw -= 50f * dt;
                if (kb.eKey.isPressed) yaw += 50f * dt;
            }
            if (AutoRotate && !dragging) yaw += 4f * dt;

            float k2 = 1f - Mathf.Exp(-7f * dt);
            target = Vector3.Lerp(target, targetGoal, k2);
            dist = Mathf.Lerp(dist, distGoal, k2);
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

        void UpdateTour(Mouse mouse, Keyboard kb, float dt, bool overUI, bool typing)
        {
            if (visitor == null) return;

            // Look: pointer-lock or drag
            if (mouse != null)
            {
                var d = mouse.delta.ReadValue();
                bool look = PointerLocked || ((mouse.rightButton.isPressed || (mouse.leftButton.isPressed && dragging)) && !overUI);
                if (look)
                {
                    tourYaw += d.x * 0.14f;
                    tourPitch = Mathf.Clamp(tourPitch - d.y * 0.12f, -60f, 60f);
                }
                // Right-click teleports to the floor point under the reticle (VR-style)
                if (PointerLocked && mouse.rightButton.wasPressedThisFrame) RequestTeleportAtReticle();
            }

            Vector3 move = Vector3.zero;
            if (kb != null && !typing)
            {
                if (kb.escapeKey.wasPressedThisFrame) ReleasePointer();
                if (kb.qKey.wasPressedThisFrame) tourYaw -= 30f; // snap turn
                if (kb.eKey.wasPressedThisFrame) tourYaw += 30f;
                if (kb.tKey.wasPressedThisFrame) RequestTeleportAtReticle();
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) move.z += 1;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) move.z -= 1;
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) move.x -= 1;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) move.x += 1;
            }

            var before = visitor.position;
            if (move.sqrMagnitude > 0.01f && guidedActive) { guided.Clear(); guidedActive = false; }

            if (guidedActive)
            {
                var next = guided.Peek();
                var to = next - visitor.position;
                to.y = 0;
                float step = WalkSpeed * 1.1f * dt;
                if (to.magnitude <= step)
                {
                    cc.enabled = false;
                    visitor.position = new Vector3(next.x, next.y, next.z);
                    floorY = next.y;
                    cc.enabled = true;
                    guided.Dequeue();
                    if (guided.Count == 0)
                    {
                        guidedActive = false;
                        OnGuidedArrive?.Invoke();
                    }
                }
                else
                {
                    cc.enabled = false;
                    visitor.position += to.normalized * step;
                    cc.enabled = true;
                    float want = guided.Count == 1 && !float.IsNaN(guidedLookYaw) && to.magnitude < 1.2f ? guidedLookYaw : Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
                    tourYaw = Mathf.LerpAngle(tourYaw, want, 1f - Mathf.Exp(-3.5f * dt));
                    tourPitch = Mathf.Lerp(tourPitch, 0f, 1f - Mathf.Exp(-3f * dt));
                }
            }
            else if (move.sqrMagnitude > 0.01f)
            {
                float speed = WalkSpeed * (kb != null && kb.leftShiftKey.isPressed ? 1.8f : 1f);
                var dir = Quaternion.Euler(0, tourYaw, 0) * move.normalized;
                var delta = dir * speed * dt;
                delta.y = floorY - visitor.position.y;
                cc.Move(delta);
            }

            var moved = visitor.position - before;
            moved.y = 0;
            CurrentSpeed = Mathf.Lerp(CurrentSpeed, moved.magnitude / Mathf.Max(0.0001f, dt), 1f - Mathf.Exp(-10f * dt));

            var eye = visitor.position + Vector3.up * EyeHeight;
            Cam.transform.position = Vector3.Lerp(Cam.transform.position, eye, 1f - Mathf.Exp(-18f * dt));
            Cam.transform.rotation = Quaternion.Euler(tourPitch, tourYaw, 0);
        }

        void RequestTeleportAtReticle()
        {
            var ray = Cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0));
            if (Physics.Raycast(ray, out var hit, 25f, ~0, QueryTriggerInteraction.Ignore) && hit.collider.GetComponent<RoomTag>() != null && hit.normal.y > 0.7f)
                OnTeleportRequest?.Invoke(hit.point);
        }
    }
}

using System;
using System.Collections.Generic;
using Atlas;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace Cascade
{
    /// <summary>
    /// Walk-in navigation without walking. Point a hand or controller at the floor and a target
    /// ring appears; pinch / pull the trigger and you glide there. Left stick glides, right stick
    /// snap-turns. We move the building around you, never the rig, so you can stay seated or
    /// standing in one spot.
    /// </summary>
    public class ZoomNav : MonoBehaviour
    {
        TwinView twin; Transform viewer; Func<float> floorY;
        public event Action Moved;

        readonly List<NearFarInteractor> rays = new();
        readonly Dictionary<NearFarInteractor, float> uiAt = new();
        readonly Dictionary<NearFarInteractor, bool> armed = new();
        Transform reticle; Renderer ring, dot; TMPro.TextMeshPro hint;
        Vector3 zoomFrom, zoomTo; float zoomT = -1; const float ZoomSec = 0.45f;
        bool turnHeld; float moveIdleAt;

        const float GlideSpeed = 2.5f, SnapDeg = 30f, MaxReach = 25f;

        public void Bind(TwinView t, Transform v, Func<float> floor)
        {
            twin = t; viewer = v; floorY = floor;
            var origin = FindFirstObjectByType<Unity.XR.CoreUtils.XROrigin>();
            if (origin)
            {
                rays.AddRange(origin.GetComponentsInChildren<NearFarInteractor>(true));
                // the rig's own move/turn/teleport would fight this (and move the rig, not the building)
                foreach (var p in origin.GetComponentsInChildren<UnityEngine.XR.Interaction.Toolkit.Locomotion.LocomotionProvider>(true))
                    p.enabled = false;
            }
            BuildReticle();
        }

        void BuildReticle()
        {
            reticle = new GameObject("ZoomReticle").transform;
            var r = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(r.GetComponent<Collider>());
            r.transform.SetParent(reticle, false);
            r.transform.localScale = new Vector3(0.55f, 0.005f, 0.55f);
            ring = r.GetComponent<Renderer>(); ring.sharedMaterial = CascadeStyle.Glow;
            var d = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(d.GetComponent<Collider>());
            d.transform.SetParent(reticle, false);
            d.transform.localPosition = new Vector3(0, 0.004f, 0);
            d.transform.localScale = new Vector3(0.14f, 0.006f, 0.14f);
            dot = d.GetComponent<Renderer>(); dot.sharedMaterial = CascadeStyle.Glow;
            hint = TwinView.MakeLabel(reticle, "Pinch to go", 1.1f, CascadeStyle.Ink, new Vector3(0, 0.35f, 0), new Vector2(3, 0.6f), false, true);
            hint.outlineWidth = 0.3f; hint.outlineColor = new Color32(255, 255, 255, 230);
            reticle.gameObject.SetActive(false);
        }

        void Update()
        {
            if (!twin || !viewer) return;
            if (!twin.LifeSize) { reticle.gameObject.SetActive(false); zoomT = -1; return; }

            // glide in progress
            if (zoomT >= 0)
            {
                zoomT += Time.deltaTime / ZoomSec;
                float k = Mathf.SmoothStep(0, 1, Mathf.Clamp01(zoomT));
                twin.transform.position = Vector3.Lerp(zoomFrom, zoomTo, k);
                if (zoomT >= 1) { zoomT = -1; Moved?.Invoke(); }
                reticle.gameObject.SetActive(false);
                return;
            }

            Sticks();
            PointAndPinch();
        }

        void PointAndPinch()
        {
            float fy = floorY();
            bool shown = false;
            foreach (var ray in rays)
            {
                if (!ray || !ray.isActiveAndEnabled) continue;
                // Panels come first: any UI contact (or a pinch that began on UI) blocks zooming,
                // so a pinch that slips off a button edge never sends you flying.
                bool onUi = ray.TryGetCurrentUIRaycastResult(out var ui) && ui.gameObject;
                if (onUi) uiAt[ray] = Time.time;
                bool pressed = ray.selectInput.ReadIsPerformed();
                if (ray.selectInput.ReadWasPerformedThisFrame())
                    armed[ray] = !onUi && !ray.hasSelection && Time.time - uiAt.GetValueOrDefault(ray, -9f) > 0.5f;
                if (onUi || ray.hasSelection) { if (pressed) armed[ray] = false; continue; }
                var o = ray.curveOrigin; if (!o) continue;
                var dir = o.forward;
                if (dir.y > -0.05f) { armed[ray] = false; continue; } // only when pointing down at the floor
                float dist = (o.position.y - fy) / -dir.y;
                if (dist <= 0 || dist > MaxReach) continue;
                var hit = o.position + dir * dist;
                var plan = twin.PlanPoint(hit);
                var size = twin.Size;
                if (plan.x < 0.3f || plan.y < 0.3f || plan.x > size.x - 0.3f || plan.y > size.y - 0.3f) continue;

                if (!shown)
                {
                    shown = true;
                    reticle.gameObject.SetActive(true);
                    reticle.position = hit + Vector3.up * 0.02f;
                    var face = hit - viewer.position; face.y = 0;
                    if (face.sqrMagnitude > 1e-4f) hint.transform.rotation = Quaternion.LookRotation(face);
                    float pulse = 0.85f + 0.15f * Mathf.Sin(Time.time * 6f);
                    ring.transform.localScale = new Vector3(0.55f * pulse, 0.005f, 0.55f * pulse);
                    CascadeStyle.Tint(ring, CascadeStyle.Accent.A(0.55f), CascadeStyle.Accent * 0.6f);
                    CascadeStyle.Tint(dot, CascadeStyle.Accent.A(0.95f), CascadeStyle.Accent * 0.8f);
                }

                // go on release, only if the whole pinch happened over the floor
                if (ray.selectInput.ReadWasCompletedThisFrame() && armed.GetValueOrDefault(ray)
                    && Time.time - uiAt.GetValueOrDefault(ray, -9f) > 0.5f)
                {
                    armed[ray] = false;
                    ZoomTo(hit);
                    return;
                }
            }
            if (!shown) reticle.gameObject.SetActive(false);
        }

        /// Glide so the world point `floorHit` ends up under your head.
        public void ZoomTo(Vector3 floorHit)
        {
            var head = viewer.position; head.y = floorHit.y;
            zoomFrom = twin.transform.position;
            zoomTo = zoomFrom - (floorHit - head);
            zoomT = 0;
        }

        void Sticks()
        {
            var left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);

            if (left.isValid && left.TryGetFeatureValue(CommonUsages.primary2DAxis, out var mv) && mv.sqrMagnitude > 0.04f)
            {
                var fwd = viewer.forward; fwd.y = 0; fwd.Normalize();
                var rgt = viewer.right; rgt.y = 0; rgt.Normalize();
                var step = (fwd * mv.y + rgt * mv.x) * GlideSpeed * Time.deltaTime;
                twin.transform.position -= step; // move the building past you
                moveIdleAt = Time.time + 0.3f;
            }
            else if (moveIdleAt > 0 && Time.time > moveIdleAt) { moveIdleAt = 0; Moved?.Invoke(); }

            if (right.isValid && right.TryGetFeatureValue(CommonUsages.primary2DAxis, out var tv))
            {
                if (!turnHeld && Mathf.Abs(tv.x) > 0.7f)
                {
                    turnHeld = true;
                    // turning you right == turning the building left around your head
                    twin.transform.RotateAround(viewer.position, Vector3.up, -Mathf.Sign(tv.x) * SnapDeg);
                    Moved?.Invoke();
                }
                else if (Mathf.Abs(tv.x) < 0.3f) turnHeld = false;
            }
        }
    }
}

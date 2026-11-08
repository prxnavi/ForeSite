using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace Atlas
{
    /// <summary>
    /// Entry point. Builds the map and HUD at runtime and wires them to a backend.
    /// Desktop scene (Atlas.unity): put this at the table center; mouse drives the pointer.
    /// Quest scene (AtlasQuest.unity): an XR Origin is present, so XR mode switches the
    /// event system to XRI (hand pinch / controller rays) and places the table in front of you.
    /// </summary>
    public class AtlasApp : MonoBehaviour
    {
        public enum BackendKind { SpacetimeDB, LocalDemo }
        public enum DisplayMode { Auto, Desktop, XR }

        [Header("Session")]
        public string displayName = "Asritha";
        public Role startRole = Role.Engineer;
        [Tooltip("SpacetimeDB = the published module (shared between headsets). LocalDemo = offline fallback.")]
        public BackendKind backendKind = BackendKind.SpacetimeDB;

        [Header("SpacetimeDB")]
        public string spacetimeUri = "https://maincloud.spacetimedb.com";
        public string spacetimeDatabase = "atlas-prxnavi";

        [Header("View")]
        [Tooltip("Auto = XR when the scene has an XR Origin.")]
        public DisplayMode displayMode = DisplayMode.Auto;
        [Tooltip("Head / camera transform. Defaults to Camera.main.")]
        public Transform viewer;
        [Tooltip("Clear to transparent black so passthrough shows through.")]
        public bool transparentBackground = true;

        [Header("XR placement")]
        [Tooltip("How far in front of your head the table center appears (m).")]
        public float tableDistance = 0.6f;
        [Tooltip("How far below your eyes the table surface sits (m).")]
        public float tableDrop = 0.5f;

        public IAtlasBackend Backend { get; private set; }
        public AtlasMapView Map { get; private set; }
        public AtlasHud Hud { get; private set; }
        public bool IsXR { get; private set; }

        void Awake()
        {
            // Keep syncing and animating when the window loses focus (editor, casting, demos).
            Application.runInBackground = true;

            IsXR = displayMode == DisplayMode.XR ||
                   (displayMode == DisplayMode.Auto && FindFirstObjectByType<Unity.XR.CoreUtils.XROrigin>() != null);

            var cam = Camera.main;
            if (cam)
            {
                if (transparentBackground)
                {
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = new Color(0, 0, 0, 0);
                }
                cam.nearClipPlane = Mathf.Min(cam.nearClipPlane, 0.03f);
                if (!IsXR && !cam.GetComponent<PhysicsRaycaster>()) cam.gameObject.AddComponent<PhysicsRaycaster>();
                if (!viewer) viewer = cam.transform;
            }
            SetupEventSystem();

            Backend = CreateBackend();

            Map = new GameObject("AtlasMap").AddComponent<AtlasMapView>();
            Map.transform.SetParent(transform, false);
            Map.Bind(Backend);

            Hud = new GameObject("AtlasHud").AddComponent<AtlasHud>();
            Hud.transform.SetParent(transform, false);
            Hud.ShowRecenter = IsXR;
            Hud.RecenterRequested += Recenter;
            Hud.Bind(Backend, Map, cam, viewer, displayName);
            Hud.PushToTalk += down => { if (down) Debug.Log("[Atlas] Push-to-talk pressed (speech-to-text not connected yet)."); };

            Backend.Connect(displayName);
            Backend.SetRole(startRole);
        }

        IEnumerator Start()
        {
            if (!IsXR || !viewer) yield break;
            // Wait for head tracking to report a real pose, then put the table in front of the user.
            float t = 0f;
            while (t < 2f && viewer.localPosition.sqrMagnitude < 1e-4f) { t += Time.deltaTime; yield return null; }
            yield return null;
            Recenter();
        }

        /// Place the table (map + panels) in front of the viewer, facing them.
        public void Recenter()
        {
            if (!viewer) return;
            var fwd = Vector3.ProjectOnPlane(viewer.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            transform.SetPositionAndRotation(
                viewer.position + fwd * tableDistance + Vector3.down * tableDrop,
                Quaternion.LookRotation(fwd, Vector3.up));
        }

        IAtlasBackend CreateBackend() => backendKind switch
        {
            BackendKind.SpacetimeDB => new SpacetimeAtlasBackend(spacetimeUri, spacetimeDatabase),
            _ => new LocalAtlasBackend(),
        };

        void OnDestroy()
        {
            UiKit.CanvasCreated -= AddTrackedRaycaster;
            if (Backend is SpacetimeAtlasBackend stdb) stdb.Disconnect();
        }

        void Update()
        {
            Backend.Tick(Time.deltaTime);
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Backend.SelectNode(null);
                Backend.ClearScene();
            }
            if (IsXR && Input.GetKeyDown(KeyCode.R)) Recenter();
        }

        void SetupEventSystem()
        {
            var es = EventSystem.current ? EventSystem.current : FindFirstObjectByType<EventSystem>();
            if (!es) es = new GameObject("EventSystem", typeof(EventSystem)).GetComponent<EventSystem>();

            if (IsXR)
            {
                // XRI module: hand pinch + controller rays, and the mouse in the editor.
                var standalone = es.GetComponent<StandaloneInputModule>();
                if (standalone) Destroy(standalone);
                if (!es.GetComponent<XRUIInputModule>()) es.gameObject.AddComponent<XRUIInputModule>();
                if (!es.GetComponent<TrackedDevicePhysicsRaycaster>()) es.gameObject.AddComponent<TrackedDevicePhysicsRaycaster>();
                UiKit.CanvasCreated += AddTrackedRaycaster;
            }
            else if (!es.GetComponent<BaseInputModule>())
            {
                es.gameObject.AddComponent<StandaloneInputModule>();
            }
        }

        static void AddTrackedRaycaster(Canvas c)
        {
            if (!c.GetComponent<TrackedDeviceGraphicRaycaster>()) c.gameObject.AddComponent<TrackedDeviceGraphicRaycaster>();
        }
    }
}

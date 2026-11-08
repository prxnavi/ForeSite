using System.Collections;
using Atlas;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace Cascade
{
    /// <summary>
    /// Entry point. On Quest you start INSIDE the building: the floor is life-size around you
    /// with glass walls over passthrough, and a bright assistant panel follows you. "Overview"
    /// turns the same building into a tabletop model. Everything syncs through SpacetimeDB.
    /// </summary>
    public class CascadeApp : MonoBehaviour
    {
        public enum DisplayMode { Auto, Desktop, XR }

        [Header("Session")]
        public string displayName = "Manager";
        public string startRole = "manager";

        [Header("SpacetimeDB")]
        public string spacetimeUri = "https://maincloud.spacetimedb.com";
        public string spacetimeDatabase = "cascade-prxnavi";

        [Header("View")]
        public DisplayMode displayMode = DisplayMode.Auto;
        public Transform viewer;
        [Tooltip("Start inside the life-size building (XR default) instead of the tabletop overview.")]
        public bool startWalkIn = true;
        public float tableDistance = 0.6f;
        public float tableDrop = 0.5f;

        public CascadeNet Net { get; private set; }
        public TwinView Twin { get; private set; }
        public AssistantPanel Panel { get; private set; }
        public VoiceInput Voice { get; private set; }
        public IssueView Issues { get; private set; }
        public bool IsXR { get; private set; }

        Transform hudFrame, panelRoot;
        float posSendAt, followUntil, readyAt = -1;
        Vector2 lastSent;
        bool sentAbsent;
        Vector2 standAt = new(1.5f, 8f), faceDir = new(1f, 0f);

        void Awake()
        {
            Application.runInBackground = true;
            IsXR = displayMode == DisplayMode.XR ||
                   (displayMode == DisplayMode.Auto && FindFirstObjectByType<Unity.XR.CoreUtils.XROrigin>() != null);
            var cam = Camera.main;
            if (cam)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0, 0, 0, 0); // passthrough
                cam.nearClipPlane = Mathf.Min(cam.nearClipPlane, 0.03f);
                cam.allowHDR = false; // required for Meta passthrough
                if (!IsXR && !cam.GetComponent<PhysicsRaycaster>()) cam.gameObject.AddComponent<PhysicsRaycaster>();
                if (!viewer) viewer = cam.transform;
            }
            SetupEventSystem();
            if (IsXR) HideRigClutter();
            SetupLighting();

            Net = new CascadeNet(spacetimeUri, spacetimeDatabase);
            Voice = gameObject.AddComponent<VoiceInput>();

            Twin = new GameObject("Twin").AddComponent<TwinView>();
            Twin.transform.SetParent(transform, false);
            Twin.transform.localScale = Vector3.one * TwinView.TabletopScale;
            Twin.Bind(Net, cam);
            Twin.TrailFrom = () => Twin.LifeSize && viewer ? Twin.PlanPoint(viewer.position) : (Vector2?)null;

            hudFrame = new GameObject("HudFrame").transform;
            panelRoot = new GameObject("AssistantRoot").transform;
            panelRoot.SetParent(hudFrame, false);
            Panel = panelRoot.gameObject.AddComponent<AssistantPanel>();
            Panel.Bind(Net, Twin, cam, Voice, IsXR);
            Panel.OverviewChanged += on => SetWalkIn(!on);
            Panel.XrayChanged += on => Twin.Xray = on;
            Panel.StructureChanged += on => Twin.Structure = on;
            Panel.RecenterRequested += Recenter;
            Panel.TakeMeThere += GoTo;

            Issues = new GameObject("IssueView").AddComponent<IssueView>();
            Issues.transform.SetParent(transform, false);
            Issues.Bind(Net, Twin, cam);
            Panel.BindIssues(Issues);

            var collab = new GameObject("Collab").AddComponent<CollabView>();
            collab.transform.SetParent(transform, false);
            collab.Bind(Net, Twin, cam);

            var orbs = new GameObject("AgentOrbs").AddComponent<AgentOrbs>();
            orbs.Bind(Net, Twin, cam, panelRoot);

            // each headset gets its own name so two people can tell each other apart
            if (IsXR && displayName == "Manager")
            {
                var id = SystemInfo.deviceUniqueIdentifier ?? "";
                displayName = $"Quest {(id.Length >= 4 ? id.Substring(id.Length - 4).ToUpperInvariant() : Random.Range(1000, 9999).ToString())}";
            }
            if (IsXR)
            {
                var nav = gameObject.AddComponent<ZoomNav>();
                nav.Bind(Twin, viewer, FloorY);
                // remember where you zoomed to, so Recenter keeps you there
                nav.Moved += () =>
                {
                    standAt = Twin.PlanPoint(viewer.position);
                    var f = Twin.transform.InverseTransformDirection(Flat(viewer.forward));
                    faceDir = new Vector2(f.x, f.z);
                    Twin.RebuildTrail();
                };
            }

            Net.Connect(displayName, startRole);
            SetWalkIn(IsXR && startWalkIn);
        }

        IEnumerator Start()
        {
            if (!IsXR || !viewer) yield break;
            float t = 0f;
            while (t < 2f && viewer.localPosition.sqrMagnitude < 1e-4f) { t += Time.deltaTime; yield return null; }
            yield return null;
            Recenter();
        }

        /// Soft architectural light for the lit interior (the look of the Keystone branch):
        /// three-color ambient plus one sun, no shadows (cheap on Quest).
        static void SetupLighting()
        {
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = AtlasStyle.Hex("#F4F8FF");
            RenderSettings.ambientEquatorColor = AtlasStyle.Hex("#E9E4F5");
            RenderSettings.ambientGroundColor = AtlasStyle.Hex("#C9D7C3");
            RenderSettings.ambientIntensity = 0.8f;
            RenderSettings.fog = false;
            var sun = FindFirstObjectByType<Light>();
            if (!sun || sun.type != LightType.Directional) sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = AtlasStyle.Hex("#FFF4E5");
            sun.intensity = 0.5f;
            sun.shadows = LightShadows.None;
            sun.transform.rotation = Quaternion.Euler(52, -35, 0);
        }

        static Vector3 Flat(Vector3 v)
        {
            v = Vector3.ProjectOnPlane(v, Vector3.up);
            return v.sqrMagnitude < 1e-4f ? Vector3.forward : v.normalized;
        }

        public void Recenter()
        {
            if (!viewer) return;
            var fwd = Flat(viewer.forward);
            transform.SetPositionAndRotation(viewer.position + fwd * tableDistance + Vector3.down * tableDrop, Quaternion.LookRotation(fwd, Vector3.up));
            if (Twin.LifeSize) PlaceAt(standAt, faceDir);
            SnapPanel();
        }

        void SetWalkIn(bool on)
        {
            Twin.LifeSize = on;
            if (on) PlaceAt(standAt, faceDir);
            else
            {
                Twin.transform.SetParent(transform, false);
                Twin.transform.localPosition = Vector3.zero;
                Twin.transform.localRotation = Quaternion.identity;
                Twin.transform.localScale = Vector3.one * TwinView.TabletopScale;
            }
            SnapPanel();
            Twin.RebuildTrail();
            Panel?.SyncOverview(!on);
        }

        /// Life-size: put plan point `plan` under your feet with plan direction `dir` straight ahead.
        void PlaceAt(Vector2 plan, Vector2 dir)
        {
            if (!viewer) return;
            standAt = plan; faceDir = dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector2.right;
            var fwd = Flat(viewer.forward);
            var rot = Quaternion.LookRotation(fwd, Vector3.up) * Quaternion.Inverse(Quaternion.LookRotation(new Vector3(faceDir.x, 0, faceDir.y), Vector3.up));
            Twin.transform.SetParent(null, true);
            Twin.transform.localScale = Vector3.one;
            Twin.transform.rotation = rot;
            var head = viewer.position; head.y = FloorY();
            Twin.transform.position = head - rot * Twin.L(plan.x, plan.y);
        }

        /// "Take me there": stand just inside the door of the target's room, facing it.
        void GoTo(string id)
        {
            if (Twin.WallRow(id) != null)
            {
                // stand ~2.2 m from the middle of the wall, on the side that is inside the building
                var (mid, n) = Twin.WallPlan(id);
                var side = mid + n * 2.2f;
                bool inside = side.x > 0.3f && side.x < Twin.Size.x - 0.3f && side.y > 0.3f && side.y < Twin.Size.y - 0.3f;
                var stand = inside ? side : mid - n * 2.2f;
                if (!Twin.LifeSize) { Twin.LifeSize = true; Panel.SyncOverview(false); }
                PlaceAt(stand, mid - stand);
                SnapPanel();
                Twin.Selected = id;
                return;
            }
            var issue = Issues ? Issues.Row(id) : null;
            if (issue != null)
            {
                // stand ~1.8 m in front of the finding, facing it
                var at = new Vector2(issue.X, issue.Z);
                var n = new Vector2(issue.Nx, issue.Nz);
                if (n.sqrMagnitude < 0.01f) { var rr = Twin.RoomRow(issue.RoomId); n = rr != null ? (new Vector2(rr.DoorX, rr.DoorZ) - at).normalized : Vector2.up; }
                if (!Twin.LifeSize) { Twin.LifeSize = true; Panel.SyncOverview(false); }
                var stand = at + n.normalized * 1.8f;
                PlaceAt(stand, at - stand);
                SnapPanel();
                Issues.Selected = id;
                return;
            }
            if (string.IsNullOrEmpty(id) || !Twin.Has(id)) return;
            var asset = Twin.AssetRow(id);
            var room = asset != null ? Twin.RoomRow(asset.RoomId) : Twin.RoomRow(id);
            if (room == null) return;
            var target = asset != null ? new Vector2(asset.X, asset.Z) : new Vector2((room.X0 + room.X1) / 2, (room.Z0 + room.Z1) / 2);
            var door = new Vector2(room.DoorX, room.DoorZ);
            var inward = new Vector2((room.X0 + room.X1) / 2, (room.Z0 + room.Z1) / 2) - door;
            var spot = door + (inward.sqrMagnitude > 1e-3f ? inward.normalized : Vector2.zero) * 1.2f;
            if (Vector2.Distance(spot, target) < 1.0f) spot = door;
            if (!Twin.LifeSize) { Twin.LifeSize = true; Panel.SyncOverview(false); }
            PlaceAt(spot, target - spot);
            SnapPanel();
            Twin.Selected = id;
            Twin.RebuildTrail();
        }

        float FloorY()
        {
            var origin = FindFirstObjectByType<Unity.XR.CoreUtils.XROrigin>();
            return origin ? origin.transform.position.y : viewer.position.y - 1.6f;
        }

        void SnapPanel()
        {
            panelRoot.localPosition = Vector3.zero; panelRoot.localRotation = Quaternion.identity;
            if (Twin.LifeSize)
            {
                followUntil = 0; FollowGoal(out var p, out var r); hudFrame.SetPositionAndRotation(p, r);
                // frame sits at your head: cards low-left / low-right, dock at waist, center stays clear
                Pose(Panel.Left, new Vector3(-0.34f, -0.04f, 0.78f), new Vector3(16, -24, 0));
                Pose(Panel.Right, new Vector3(0.34f, -0.04f, 0.78f), new Vector3(16, 24, 0));
                Pose(Panel.Dock, new Vector3(0, -0.60f, 0.60f), new Vector3(48, 0, 0));
            }
            else
            {
                hudFrame.SetPositionAndRotation(transform.position, transform.rotation);
                Pose(Panel.Left, new Vector3(-0.64f, 0.50f, 0.04f), new Vector3(0, -30, 0));
                Pose(Panel.Right, new Vector3(0.64f, 0.50f, 0.04f), new Vector3(0, 30, 0));
                Pose(Panel.Dock, new Vector3(0, 0.0f, -0.40f), new Vector3(42, 0, 0));
            }
        }

        static void Pose(Transform t, Vector3 pos, Vector3 euler) { if (!t) return; t.localPosition = pos; t.localRotation = Quaternion.Euler(euler); }

        void FollowGoal(out Vector3 pos, out Quaternion rot)
        {
            pos = viewer.position;
            rot = Quaternion.LookRotation(Flat(viewer.forward), Vector3.up);
        }

        /// The starter rig ships controller models and teleport arcs/reticles; Cascade uses
        /// neither, so hide them and keep only the pointing rays and hands. The pinch/poke
        /// pointer meshes use URP-only shaders and render magenta in this pipeline.
        void HideRigClutter()
        {
            var origin = FindFirstObjectByType<Unity.XR.CoreUtils.XROrigin>();
            if (!origin) return;
            foreach (var t in origin.GetComponentsInChildren<Transform>(true))
                if (t && (t.name.Contains("Teleport") || t.name.EndsWith("Controller Visual") || t.name == "TeleportUI"
                    || t.name.StartsWith("Pinch Visual") || t.name.StartsWith("Pinch_Pointer")))
                    t.gameObject.SetActive(false);
            // The tracked-hand meshes ship with a URP-only material (renders magenta in this pipeline):
            // give them a soft lit skin material that the build includes.
            var skin = new Material(CascadeStyle.Lit(null, 0.3f)) { color = new Color(0.93f, 0.86f, 0.80f) };
            foreach (var r in origin.GetComponentsInChildren<Renderer>(true))
            {
                var m = r.sharedMaterial;
                if (m && (!m.shader.isSupported || m.shader.name.Contains("Error") || m.shader.name.Contains("Universal")))
                    r.sharedMaterial = skin;
            }
        }

        void Update()
        {
            Net.Tick();
            Twin.Hour = Panel.DisplayHour;
            Twin.Label = Panel.DisplayLabel;

            if (Twin.LifeSize && viewer)
            {
                // the panel lazily follows you as you walk and look around
                FollowGoal(out var gp, out var gr);
                if (Vector3.Distance(hudFrame.position, gp) > 0.35f || Quaternion.Angle(hudFrame.rotation, gr) > 40f) followUntil = Time.time + 0.9f;
                if (Time.time < followUntil)
                    hudFrame.SetPositionAndRotation(Vector3.Lerp(hudFrame.position, gp, Time.deltaTime * 4f), Quaternion.Slerp(hudFrame.rotation, gr, Time.deltaTime * 4f));
            }

            // no hidden "build" step: the walls rise on their own once synced
            if (Net.Ready && Net.Timeline != null && !Net.Timeline.Built)
            {
                if (readyAt < 0) readyAt = Time.time;
                if (Time.time - readyAt > 1.5f) { readyAt = Time.time + 3f; Net.BuildTwin(); }
            }
            else readyAt = -1;

            if (Net.Ready && Time.time > posSendAt)
            {
                posSendAt = Time.time + 0.1f;
                if (Twin.LifeSize && viewer)
                {
                    var p = Twin.PlanPoint(viewer.position);
                    if (sentAbsent || (p - lastSent).sqrMagnitude > 0.0025f) { Net.UpdatePosition(p, true); lastSent = p; }
                    sentAbsent = false;
                }
                else if (!sentAbsent) { Net.UpdatePosition(Vector2.zero, false); sentAbsent = true; }
            }

            // desktop shortcuts
            if (Input.GetKeyDown(KeyCode.Space)) Net.Ask("Anything I should worry about?");
            if (Input.GetKeyDown(KeyCode.B)) Net.BuildTwin();
            if (Input.GetKeyDown(KeyCode.X)) Twin.Xray = !Twin.Xray;
            if (Input.GetKeyDown(KeyCode.L)) SetWalkIn(!Twin.LifeSize);
            if (Input.GetKeyDown(KeyCode.G)) GoTo("CH-2");
            if (IsXR && Input.GetKeyDown(KeyCode.R)) Recenter();
        }

        void OnDestroy()
        {
            UiKit.CanvasCreated -= AddTrackedRaycaster;
            Net?.Disconnect();
        }

        void SetupEventSystem()
        {
            var es = EventSystem.current ? EventSystem.current : FindFirstObjectByType<EventSystem>();
            if (!es) es = new GameObject("EventSystem", typeof(EventSystem)).GetComponent<EventSystem>();
            if (IsXR)
            {
                var standalone = es.GetComponent<StandaloneInputModule>();
                if (standalone) Destroy(standalone);
                if (!es.GetComponent<XRUIInputModule>()) es.gameObject.AddComponent<XRUIInputModule>();
                if (!es.GetComponent<TrackedDevicePhysicsRaycaster>()) es.gameObject.AddComponent<TrackedDevicePhysicsRaycaster>();
                UiKit.CanvasCreated += AddTrackedRaycaster;
            }
            else if (!es.GetComponent<BaseInputModule>()) es.gameObject.AddComponent<StandaloneInputModule>();
        }

        static void AddTrackedRaycaster(Canvas c)
        {
            if (!c.GetComponent<TrackedDeviceGraphicRaycaster>()) c.gameObject.AddComponent<PanelRaycaster>();
        }
    }
}

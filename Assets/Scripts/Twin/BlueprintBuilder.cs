using System.Collections.Generic;
using System.Linq;
using SpacetimeDB.Types;
using UnityEngine;

namespace CbreTwin
{
    /// <summary>Tag on floor colliders so clicks resolve to a room.</summary>
    public class RoomTag : MonoBehaviour { public uint RoomId; }

    /// <summary>Scales a piece in from the ground so the prototype "builds" itself.</summary>
    public class BuildIn : MonoBehaviour
    {
        public float Delay;
        Vector3 target;
        float t;

        void Awake()
        {
            target = transform.localScale;
            transform.localScale = new Vector3(target.x, 0.0001f, target.z);
        }

        void Update()
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01((t - Delay) / 0.35f);
            k = 1f - Mathf.Pow(1f - k, 3f);
            transform.localScale = new Vector3(target.x, Mathf.Max(0.0001f, target.y * k), target.z);
            if (k >= 1f) Destroy(this);
        }
    }

    /// <summary>Procedurally generates a walkable 3D prototype from blueprint rows.</summary>
    public class BlueprintBuilder : MonoBehaviour
    {
        public const float FT = 0.3048f;
        const float WallT = 0.45f;
        const float SlabT = 0.6f;

        public uint BlueprintId { get; private set; }
        public bool PlanMode;
        public bool ShowStructural = true, ShowSystems = true, ShowCascade = true, ShowRoof;
        public int MaxFloor = 99;
        public int FloorCount { get; private set; }
        public Bounds WorldBounds { get; private set; }
        public float BuildStartTime { get; private set; } = -10f;
        public float BuildDuration { get; private set; } = 1f;

        public readonly Dictionary<uint, Vector3> ComponentWorld = new();
        public readonly Dictionary<uint, Room> RoomsById = new();
        public readonly Dictionary<uint, Vector3> RoomCenter = new();
        public uint SelectedComponent;
        public uint SelectedRoom;

        readonly List<float> levelBase = new();
        readonly List<Transform> levels = new();
        Transform root, overlayRoot, systemsRoot, cascadeRoot, groundRoot;
        readonly Dictionary<string, Material> mats = new();
        readonly Dictionary<uint, Transform> markers = new();
        readonly Dictionary<uint, Renderer> tintRenderers = new();
        readonly List<(LineRenderer line, Transform spark, Vector3[] pts, float speed)> cascades = new();
        float delay;

        TwinConnection Net => TwinConnection.I;

        public float LevelBaseFt(int floor) => floor < levelBase.Count ? levelBase[floor] : 0f;

        public static Vector3 W(float xFt, float yFt, float zFt) => new(xFt * FT, yFt * FT, zFt * FT);

        // ───────────── Materials ─────────────

        Shader litShader;
        Material Mat(Color c, bool transparent = false, float alpha = 1f, bool emissive = false)
        {
            var key = $"{ColorUtility.ToHtmlStringRGBA(c)}:{transparent}:{alpha:0.00}:{emissive}";
            if (mats.TryGetValue(key, out var m)) return m;
            litShader ??= Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            m = new Material(litShader);
            var col = new Color(c.r, c.g, c.b, transparent ? alpha : 1f);
            m.SetColor("_BaseColor", col);
            m.color = col;
            m.SetFloat("_Smoothness", transparent ? 0.9f : 0.25f);
            if (transparent)
            {
                m.SetFloat("_Surface", 1f);
                m.SetFloat("_Blend", 0f);
                m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                m.SetFloat("_ZWrite", 0f);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
            if (emissive)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", c * 0.9f);
            }
            mats[key] = m;
            return m;
        }

        Material lineMat;
        Material LineMat() => lineMat ??= new Material(Shader.Find("Sprites/Default"));

        // ───────────── Primitive helpers ─────────────

        GameObject Box(Transform parent, Vector3 centerFt, Vector3 sizeFt, Color c, bool keepCollider = false, bool transparent = false, float alpha = 1f, bool animate = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = centerFt * FT;
            go.transform.localScale = sizeFt * FT;
            go.GetComponent<Renderer>().sharedMaterial = Mat(c, transparent, alpha);
            if (!keepCollider) Destroy(go.GetComponent<Collider>());
            if (animate && !PlanMode)
            {
                var b = go.AddComponent<BuildIn>();
                b.Delay = delay;
            }
            return go;
        }

        // ───────────── Build ─────────────

        public void Clear()
        {
            if (root) Destroy(root.gameObject);
            levels.Clear();
            levelBase.Clear();
            markers.Clear();
            tintRenderers.Clear();
            cascades.Clear();
            ComponentWorld.Clear();
            RoomsById.Clear();
            RoomCenter.Clear();
        }

        public void Build(uint bpId)
        {
            Clear();
            BlueprintId = bpId;
            var bp = Net.Blueprint(bpId);
            if (bp == null) return;
            var rooms = Net.Rooms(bpId);
            foreach (var r in rooms) RoomsById[r.Id] = r;

            root = new GameObject($"Blueprint {bp.Name}").transform;
            root.SetParent(transform, false);
            groundRoot = new GameObject("Site").transform; groundRoot.SetParent(root, false);
            overlayRoot = new GameObject("Structural overlay").transform; overlayRoot.SetParent(root, false);
            systemsRoot = new GameObject("Systems").transform; systemsRoot.SetParent(root, false);
            cascadeRoot = new GameObject("Cascades").transform; cascadeRoot.SetParent(root, false);

            FloorCount = rooms.Count == 0 ? 1 : (int)rooms.Max(r => r.Floor) + 1;
            float y = 0;
            for (int f = 0; f < FloorCount; f++)
            {
                levelBase.Add(y);
                var lv = new GameObject($"Level {f + 1}").transform;
                lv.SetParent(root, false);
                levels.Add(lv);
                var onFloor = rooms.Where(r => r.Floor == f && r.Kind != "deck" && r.Kind != "pool" && r.Kind != "patio" && r.Ceiling > 0 && !r.Flags.Contains("vaulted")).ToList();
                float h = onFloor.Count > 0 ? (float)onFloor.Max(r => r.Ceiling) : 9f;
                y += h + 1f;
            }

            // Site & lot
            float minX = rooms.Count > 0 ? (float)rooms.Min(r => r.X) : 0, maxX = rooms.Count > 0 ? (float)rooms.Max(r => r.X + r.W) : 40;
            float minZ = rooms.Count > 0 ? (float)rooms.Min(r => r.Z) : 0, maxZ = rooms.Count > 0 ? (float)rooms.Max(r => r.Z + r.D) : 40;
            var cx = (minX + maxX) / 2f; var cz = (minZ + maxZ) / 2f;
            float pad = 22f;
            delay = 0;
            Box(groundRoot, new Vector3(cx, -0.55f, cz), new Vector3(maxX - minX + pad * 2, 0.5f, maxZ - minZ + pad * 2),
                PlanMode ? Palette.Hex("#245CA3") : Palette.Hex("#BFE3B4"), animate: false);
            if (!PlanMode)
            {
                // driveway toward the garage
                var garage = rooms.FirstOrDefault(r => r.Kind == "garage" && r.Floor == 0);
                if (garage != null)
                    Box(groundRoot, new Vector3((float)(garage.X + garage.W / 2), -0.28f, (float)garage.Z - pad / 2f), new Vector3((float)garage.W * 0.9f, 0.1f, pad), Palette.Hex("#D9DCE3"), animate: false);
            }

            WorldBounds = new Bounds(W(cx, LevelBaseFt(FloorCount - 1) / 2f, cz), W(maxX - minX, LevelBaseFt(FloorCount - 1) + 10, maxZ - minZ));

            // Level by level: slabs → walls → fixtures
            for (int f = 0; f < FloorCount; f++)
            {
                var lv = levels[f];
                var floorRooms = rooms.Where(r => r.Floor == f).ToList();
                float baseY = levelBase[f];
                foreach (var r in floorRooms)
                {
                    delay += 0.03f;
                    BuildSlab(lv, r, baseY);
                    RoomCenter[r.Id] = W((float)(r.X + r.W / 2), baseY + 0.1f, (float)(r.Z + r.D / 2));
                }
                foreach (var r in floorRooms)
                {
                    delay += 0.035f;
                    BuildWalls(lv, r, floorRooms, baseY);
                }
                if (!PlanMode)
                    foreach (var r in floorRooms) BuildFurniture(lv, r, baseY);
                if (ShowRoof && !PlanMode)
                {
                    foreach (var r in floorRooms.Where(r => r.Kind != "pool" && r.Kind != "deck" && r.Ceiling > 0))
                    {
                        bool covered = rooms.Any(u => u.Floor == f + 1 && Overlap(r, u) > 0.3 * r.W * r.D);
                        if (!covered && r.Kind != "patio")
                            Box(lv, new Vector3((float)(r.X + r.W / 2), baseY + (float)r.Ceiling + 0.4f, (float)(r.Z + r.D / 2)), new Vector3((float)r.W + 1.2f, 0.8f, (float)r.D + 1.2f), Palette.Hex("#8C95A8"));
                    }
                }
            }
            BuildDuration = delay + 0.4f;
            BuildStartTime = Time.time;
            ApplyFloorVisibility();
            RefreshOverlays();
        }

        static double Overlap(Room a, Room b)
        {
            var ox = System.Math.Max(0, System.Math.Min(a.X + a.W, b.X + b.W) - System.Math.Max(a.X, b.X));
            var oz = System.Math.Max(0, System.Math.Min(a.Z + a.D, b.Z + b.D) - System.Math.Max(a.Z, b.Z));
            return ox * oz;
        }

        static bool HasWalls(Room r) => r.Kind != "patio" && r.Kind != "deck" && r.Kind != "pool";

        void BuildSlab(Transform parent, Room r, float baseY)
        {
            var center = new Vector3((float)(r.X + r.W / 2), baseY - SlabT / 2f, (float)(r.Z + r.D / 2));
            Color c = PlanMode ? Palette.Hex("#2D6DB8") : Palette.Room(r.Kind);
            GameObject slab;
            if (r.Kind == "pool")
            {
                slab = Box(parent, center + new Vector3(0, -1.6f, 0), new Vector3((float)r.W, 0.3f, (float)r.D), Palette.Hex("#3FA9F5"), keepCollider: true);
                Box(parent, center + new Vector3(0, -0.3f, 0), new Vector3((float)r.W, 0.2f, (float)r.D), Palette.Hex("#8FD8FF"), transparent: true, alpha: 0.65f);
                // coping
                Box(parent, new Vector3(center.x, baseY - 0.1f, (float)r.Z - 0.6f), new Vector3((float)r.W + 2.4f, 0.4f, 1.2f), Palette.Hex("#F4EFE6"));
                Box(parent, new Vector3(center.x, baseY - 0.1f, (float)(r.Z + r.D) + 0.6f), new Vector3((float)r.W + 2.4f, 0.4f, 1.2f), Palette.Hex("#F4EFE6"));
            }
            else
            {
                slab = Box(parent, center, new Vector3((float)r.W, SlabT, (float)r.D), c, keepCollider: true);
            }
            slab.AddComponent<RoomTag>().RoomId = r.Id;
            slab.name = $"Room {r.Name}";

            if (r.Kind == "patio" && r.Flags.Contains("beam") && !PlanMode)
            {
                // Covered patio: posts + glulam beam + roof
                var post = Palette.Hex("#C59B6D");
                float h = (float)r.Ceiling;
                foreach (var px in new[] { r.X + 0.6, r.X + r.W / 2, r.X + r.W - 0.6 })
                    Box(parent, new Vector3((float)px, baseY + h / 2f, (float)r.Z + 0.6f), new Vector3(0.9f, h, 0.9f), post);
                Box(parent, new Vector3((float)(r.X + r.W / 2), baseY + h + 0.6f, (float)r.Z + 0.6f), new Vector3((float)r.W, 1.2f, 0.9f), Palette.Hex("#B07F4F"));
                Box(parent, new Vector3((float)(r.X + r.W / 2), baseY + h + 1.4f, (float)(r.Z + r.D / 2)), new Vector3((float)r.W + 1f, 0.4f, (float)r.D + 1f), Palette.Hex("#9AA3B5"), transparent: true, alpha: 0.55f);
            }
            if (r.Kind == "deck" && !PlanMode)
            {
                // Glass guard rail around the deck
                foreach (var e in Edges(r))
                {
                    var (cen, size) = EdgeBox(e, e.A0, e.A1, baseY + 1.75f, 3.5f, 0.15f);
                    Box(parent, cen, size, Palette.Hex("#A9D6FF"), transparent: true, alpha: 0.35f);
                }
            }
        }

        // ───────────── Walls ─────────────

        struct Edge { public bool Horiz; public float C; public float A0; public float A1; public int Side; }

        static IEnumerable<Edge> Edges(Room r)
        {
            yield return new Edge { Horiz = true, C = (float)r.Z, A0 = (float)r.X, A1 = (float)(r.X + r.W), Side = 0 };
            yield return new Edge { Horiz = true, C = (float)(r.Z + r.D), A0 = (float)r.X, A1 = (float)(r.X + r.W), Side = 1 };
            yield return new Edge { Horiz = false, C = (float)r.X, A0 = (float)r.Z, A1 = (float)(r.Z + r.D), Side = 2 };
            yield return new Edge { Horiz = false, C = (float)(r.X + r.W), A0 = (float)r.Z, A1 = (float)(r.Z + r.D), Side = 3 };
        }

        static (Vector3 center, Vector3 size) EdgeBox(Edge e, float a0, float a1, float yCenter, float height, float thick)
        {
            float mid = (a0 + a1) / 2f, len = Mathf.Max(0.01f, a1 - a0);
            return e.Horiz
                ? (new Vector3(mid, yCenter, e.C), new Vector3(len, height, thick))
                : (new Vector3(e.C, yCenter, mid), new Vector3(thick, height, len));
        }

        void BuildWalls(Transform parent, Room r, List<Room> floorRooms, float baseY)
        {
            if (!HasWalls(r)) return;
            float h = (float)r.Ceiling;
            Color wallCol = PlanMode ? Palette.BlueprintLine : Palette.Hex("#FBFAF7");
            Color extCol = PlanMode ? Palette.BlueprintLine : Palette.Hex("#F3EEE6");

            foreach (var e in Edges(r))
            {
                var shared = new List<(float a0, float a1, Room n)>();
                foreach (var n in floorRooms)
                {
                    if (n.Id == r.Id) continue;
                    foreach (var ne in Edges(n))
                    {
                        if (ne.Horiz != e.Horiz || Mathf.Abs(ne.C - e.C) > 0.5f) continue;
                        float o0 = Mathf.Max(e.A0, ne.A0), o1 = Mathf.Min(e.A1, ne.A1);
                        if (o1 - o0 > 0.3f) shared.Add((o0, o1, n));
                    }
                }
                shared.Sort((a, b) => a.a0.CompareTo(b.a0));
                float cursor = e.A0;
                foreach (var s in shared)
                {
                    if (s.a0 > cursor + 0.3f) ExteriorWall(parent, r, e, cursor, s.a0, baseY, h, extCol, null);
                    if (!HasWalls(s.n))
                        ExteriorWall(parent, r, e, s.a0, s.a1, baseY, h, extCol, s.n); // opens to patio/deck
                    else if (r.Id < s.n.Id)
                        InteriorWall(parent, r, s.n, e, s.a0, s.a1, baseY, Mathf.Min(h, (float)s.n.Ceiling), wallCol);
                    cursor = Mathf.Max(cursor, s.a1);
                }
                if (e.A1 > cursor + 0.3f) ExteriorWall(parent, r, e, cursor, e.A1, baseY, h, extCol, null);
            }
        }

        static readonly HashSet<string> OpenPlan = new() { "living", "kitchen", "dining" };

        void InteriorWall(Transform parent, Room a, Room b, Edge e, float a0, float a1, float baseY, float h, Color c)
        {
            float len = a1 - a0;
            bool open = OpenPlan.Contains(a.Kind) && OpenPlan.Contains(b.Kind);
            bool noDoor = len < 3.2f || (a.Kind == "garage" && b.Kind != "mudroom" && b.Kind != "laundry" && b.Kind != "kitchen" && b.Kind != "hall") ||
                          (b.Kind == "garage" && a.Kind != "mudroom" && a.Kind != "laundry" && a.Kind != "kitchen" && a.Kind != "hall");
            if (open && len > 6)
            {
                Segment(parent, e, a0, a0 + 1f, baseY, 0, h, c);
                Segment(parent, e, a1 - 1f, a1, baseY, 0, h, c);
                Segment(parent, e, a0 + 1f, a1 - 1f, baseY, h - 1f, h, c); // header
                return;
            }
            if (noDoor)
            {
                Segment(parent, e, a0, a1, baseY, 0, h, c);
                return;
            }
            float mid = (a0 + a1) / 2f, dw = Mathf.Min(3f, len - 0.4f);
            Segment(parent, e, a0, mid - dw / 2, baseY, 0, h, c);
            Segment(parent, e, mid + dw / 2, a1, baseY, 0, h, c);
            Segment(parent, e, mid - dw / 2, mid + dw / 2, baseY, 7f, h, c);
        }

        void ExteriorWall(Transform parent, Room r, Edge e, float a0, float a1, float baseY, float h, Color c, Room toOutdoor)
        {
            float len = a1 - a0;
            float glaz = (float)r.Glazing;
            bool slider = toOutdoor != null && len >= 6f;
            if (r.Kind == "garage" && len >= 16f && e.Side == 0)
            {
                // Garage doors on the street face
                float dw = Mathf.Min(16f, len - 2f);
                float mid = (a0 + a1) / 2f;
                Segment(parent, e, a0, mid - dw / 2, baseY, 0, h, c);
                Segment(parent, e, mid + dw / 2, a1, baseY, 0, h, c);
                Segment(parent, e, mid - dw / 2, mid + dw / 2, baseY, 8f, h, c);
                var (gc, gs) = EdgeBox(e, mid - dw / 2, mid + dw / 2, baseY + 4f, 8f, 0.25f);
                Box(parent, gc, gs, PlanMode ? Palette.BlueprintLine : Palette.Hex("#D0D6E2"));
                return;
            }
            if (slider)
            {
                float dw = Mathf.Min(len * 0.6f, 12f);
                float mid = (a0 + a1) / 2f;
                Opening(parent, e, a0, a1, mid - dw / 2, mid + dw / 2, 0f, 8f, baseY, h, c);
                return;
            }
            float ww = len * glaz;
            if (ww < 2f || len < 3f)
            {
                Segment(parent, e, a0, a1, baseY, 0, h, c);
                return;
            }
            bool wallOfGlass = glaz >= 0.55f;
            float sill = wallOfGlass ? 1f : 2.8f, head = wallOfGlass ? Mathf.Min(h - 0.8f, 12f) : 7f;
            if (h > 14f && wallOfGlass) head = h - 1.5f;
            // Split long windows into two for realism
            if (!wallOfGlass && len > 14f)
            {
                float half = (a0 + a1) / 2f;
                ExteriorWindow(parent, e, a0, half, ww / 2f, sill, head, baseY, h, c);
                ExteriorWindow(parent, e, half, a1, ww / 2f, sill, head, baseY, h, c);
            }
            else
            {
                ExteriorWindow(parent, e, a0, a1, ww, sill, head, baseY, h, c);
            }
        }

        void ExteriorWindow(Transform parent, Edge e, float a0, float a1, float ww, float sill, float head, float baseY, float h, Color c)
        {
            float mid = (a0 + a1) / 2f;
            ww = Mathf.Min(ww, a1 - a0 - 0.8f);
            if (ww < 1.5f) { Segment(parent, e, a0, a1, baseY, 0, h, c); return; }
            Opening(parent, e, a0, a1, mid - ww / 2, mid + ww / 2, sill, head, baseY, h, c);
        }

        void Opening(Transform parent, Edge e, float a0, float a1, float o0, float o1, float sill, float head, float baseY, float h, Color c)
        {
            Segment(parent, e, a0, o0, baseY, 0, h, c);
            Segment(parent, e, o1, a1, baseY, 0, h, c);
            if (sill > 0.05f) Segment(parent, e, o0, o1, baseY, 0, sill, c);
            if (head < h - 0.05f) Segment(parent, e, o0, o1, baseY, head, h, c);
            var (gc, gs) = EdgeBox(e, o0, o1, baseY + (sill + head) / 2f, head - sill, 0.12f);
            Box(parent, gc, gs, PlanMode ? Palette.Hex("#9CC9FF") : Palette.Hex("#9FD3FF"), transparent: true, alpha: 0.38f);
            // frame
            var (fc, fs) = EdgeBox(e, o0, o1, baseY + sill, 0.25f, WallT + 0.1f);
            Box(parent, fc, fs, PlanMode ? Palette.BlueprintLine : Palette.Hex("#3A4255"));
        }

        void Segment(Transform parent, Edge e, float a0, float a1, float baseY, float y0, float y1, Color c)
        {
            if (a1 - a0 < 0.05f || y1 - y0 < 0.05f) return;
            var (cen, size) = EdgeBox(e, a0, a1, baseY + (y0 + y1) / 2f, y1 - y0, WallT);
            if (PlanMode) { size.y = Mathf.Min(size.y, 2f); cen.y = baseY + 1f; if (y0 > 0.1f) return; }
            Box(parent, cen, size, c);
        }

        // ───────────── Furniture (for walkthrough legibility) ─────────────

        void BuildFurniture(Transform p, Room r, float y)
        {
            float cx = (float)(r.X + r.W / 2), cz = (float)(r.Z + r.D / 2), w = (float)r.W, d = (float)r.D;
            var wood = Palette.Hex("#C9A27E"); var soft = Palette.Hex("#8EA4FF"); var white = Palette.Hex("#FFFFFF");
            switch (r.Kind)
            {
                case "living":
                    Box(p, new Vector3(cx, y + 0.05f, cz), new Vector3(w * 0.5f, 0.1f, d * 0.45f), Palette.Hex("#F7C59F"));
                    Box(p, new Vector3(cx, y + 1.25f, cz + d * 0.3f), new Vector3(Mathf.Min(8, w * 0.5f), 2.5f, 3f), soft);
                    Box(p, new Vector3(cx, y + 0.75f, cz), new Vector3(4f, 1.5f, 2.2f), wood);
                    break;
                case "kitchen":
                    Box(p, new Vector3(cx, y + 1.5f, cz), new Vector3(Mathf.Min(8, w * 0.45f), 3f, 3.5f), white);
                    Box(p, new Vector3(cx, y + 1.5f, (float)r.Z + 1.3f), new Vector3(w * 0.85f, 3f, 2.2f), Palette.Hex("#F5F0E6"));
                    break;
                case "dining":
                    Box(p, new Vector3(cx, y + 1.25f, cz), new Vector3(Mathf.Min(6, w * 0.5f), 0.2f, 3.4f), wood);
                    Box(p, new Vector3(cx, y + 0.6f, cz), new Vector3(0.6f, 1.2f, 0.6f), wood);
                    break;
                case "bedroom":
                case "primary":
                    {
                        float bw = r.Kind == "primary" ? 6.5f : 5f;
                        Box(p, new Vector3(cx, y + 1f, cz + d * 0.15f), new Vector3(bw, 2f, 6.8f), white);
                        Box(p, new Vector3(cx, y + 1.3f, cz + d * 0.15f - 1.2f), new Vector3(bw + 0.1f, 2.3f, 4f), r.Kind == "primary" ? Palette.Hex("#FFB4A2") : Palette.Hex("#A0C4FF"));
                        Box(p, new Vector3(cx, y + 2f, cz + d * 0.15f + 3.5f), new Vector3(bw + 0.4f, 4f, 0.5f), wood);
                        break;
                    }
                case "bath":
                    Box(p, new Vector3(cx, y + 0.9f, (float)(r.Z + r.D) - 1.6f), new Vector3(Mathf.Min(5, w - 1), 1.8f, 2.6f), white);
                    break;
                case "office":
                    Box(p, new Vector3(cx, y + 1.25f, cz), new Vector3(5f, 0.2f, 2.4f), wood);
                    Box(p, new Vector3(cx, y + 2.1f, cz + 0.8f), new Vector3(2.2f, 1.4f, 0.15f), Palette.Hex("#2B3245"));
                    break;
                case "game":
                    Box(p, new Vector3(cx, y + 1.4f, cz), new Vector3(4.2f, 0.5f, 7.5f), Palette.Hex("#1F9D6B"));
                    Box(p, new Vector3(cx, y + 0.6f, cz), new Vector3(3.6f, 1.2f, 6.8f), wood);
                    break;
                case "media":
                    Box(p, new Vector3(cx, y + 1.2f, cz + d * 0.2f), new Vector3(w * 0.6f, 2.4f, 3.2f), Palette.Hex("#6C5CE7"));
                    Box(p, new Vector3(cx, y + 4f, (float)r.Z + 0.8f), new Vector3(w * 0.6f, 4f, 0.2f), Palette.Hex("#1B1F2E"));
                    break;
                case "garage":
                    {
                        int cars = Mathf.Clamp(Mathf.FloorToInt(w / 10.5f), 1, 3);
                        var cols = new[] { Palette.Hex("#FF8FA3"), Palette.Hex("#7EC8E3"), Palette.Hex("#FFD166") };
                        for (int i = 0; i < cars; i++)
                        {
                            float x = (float)r.X + w * (i + 0.5f) / cars;
                            Box(p, new Vector3(x, y + 1.6f, cz), new Vector3(6f, 3.2f, 14.5f), cols[i]);
                            Box(p, new Vector3(x, y + 3.6f, cz + 1f), new Vector3(5.4f, 1.6f, 7f), Palette.Hex("#BFD8F5"), transparent: true, alpha: 0.7f);
                        }
                        break;
                    }
                case "stair":
                    {
                        int steps = 12;
                        bool alongZ = d >= w;
                        for (int i = 0; i < steps; i++)
                        {
                            float t = (i + 0.5f) / steps;
                            var pos = alongZ ? new Vector3(cx, y + (i + 0.5f) * 0.75f / 2f, (float)r.Z + d * t) : new Vector3((float)r.X + w * t, y + (i + 0.5f) * 0.75f / 2f, cz);
                            var size = alongZ ? new Vector3(w * 0.8f, (i + 1) * 0.75f, d / steps) : new Vector3(w / steps, (i + 1) * 0.75f, d * 0.8f);
                            pos.y = y + size.y / 2f;
                            Box(p, pos, size, Palette.Hex("#D9C7AE"));
                        }
                        break;
                    }
                case "patio":
                    Box(p, new Vector3(cx - 3, y + 1.2f, cz), new Vector3(6f, 0.2f, 3.5f), wood);
                    break;
                case "deck":
                    Box(p, new Vector3(cx, y + 1.2f, cz), new Vector3(3f, 2.4f, 3f), Palette.Hex("#7ED957"));
                    Box(p, new Vector3(cx + 5, y + 1.2f, cz - 4), new Vector3(7f, 2.2f, 3f), Palette.Hex("#FFB4A2"));
                    break;
            }
        }

        // ───────────── Overlays (re-run whenever risk / structural tables change) ─────────────

        public void RefreshOverlays()
        {
            if (root == null || Net == null || !Net.Ready) return;
            foreach (Transform c in overlayRoot) Destroy(c.gameObject);
            foreach (Transform c in systemsRoot) Destroy(c.gameObject);
            foreach (Transform c in cascadeRoot) Destroy(c.gameObject);
            markers.Clear();
            tintRenderers.Clear();
            cascades.Clear();
            ComponentWorld.Clear();

            // Structural heatmap: worst finding per room
            var worst = new Dictionary<uint, string>();
            foreach (var f in Net.Findings(BlueprintId))
            {
                if (!worst.TryGetValue(f.RoomId, out var w) || Rank(f.Severity) > Rank(w)) worst[f.RoomId] = f.Severity;
            }
            if (ShowStructural)
            {
                foreach (var kv in worst)
                {
                    if (!RoomsById.TryGetValue(kv.Key, out var r)) continue;
                    if (r.Floor > MaxFloor) continue;
                    var col = Palette.Severity(kv.Value);
                    var go = Box(overlayRoot, new Vector3((float)(r.X + r.W / 2), LevelBaseFt((int)r.Floor) + 0.08f, (float)(r.Z + r.D / 2)),
                        new Vector3((float)r.W - 0.5f, 0.12f, (float)r.D - 0.5f), col, transparent: true, alpha: kv.Value == "ok" ? 0.28f : 0.55f, animate: false);
                    tintRenderers[kv.Key] = go.GetComponent<Renderer>();
                    go.name = $"Tint {r.Name} {kv.Value}";
                }
            }

            // System markers
            foreach (var c in Net.Components(BlueprintId))
            {
                if (!RoomsById.TryGetValue(c.RoomId, out var r)) continue;
                float x = (float)(r.X + Mathf.Clamp01((float)c.Ox) * r.W);
                float z = (float)(r.Z + Mathf.Clamp01((float)c.Oz) * r.D);
                x = Mathf.Clamp(x, (float)r.X + 1.2f, (float)(r.X + r.W) - 1.2f);
                z = Mathf.Clamp(z, (float)r.Z + 1.2f, (float)(r.Z + r.D) - 1.2f);
                float yb = LevelBaseFt((int)r.Floor);
                var pos = new Vector3(x, yb + (float)c.Oy, z);
                ComponentWorld[c.Id] = pos * FT;
                if (!ShowSystems || r.Floor > MaxFloor) continue;
                var risk = Net.Risk(c.Id);
                var col = Palette.Tier(risk?.Tier ?? "low");
                var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                s.transform.SetParent(systemsRoot, false);
                s.transform.localPosition = pos * FT;
                float size = 1.1f + (float)(risk?.CascadeProb ?? 0) * 1.6f;
                s.transform.localScale = Vector3.one * size * FT;
                s.GetComponent<Renderer>().sharedMaterial = Mat(col, emissive: true);
                Destroy(s.GetComponent<Collider>());
                markers[c.Id] = s.transform;
                var stalk = Box(systemsRoot, new Vector3(x, yb + (float)c.Oy / 2f, z), new Vector3(0.12f, (float)c.Oy, 0.12f), col, transparent: true, alpha: 0.6f, animate: false);
                stalk.name = "stalk";
            }

            // Cascade chains — animated "spread" sparks along the top predicted failure paths
            if (ShowCascade)
            {
                int n = 0;
                foreach (var p in Net.Paths(BlueprintId))
                {
                    if (n++ >= 3) break;
                    var pts = new List<Vector3>();
                    for (int i = 0; i < p.ComponentIds.Count; i++)
                    {
                        if (!ComponentWorld.TryGetValue(p.ComponentIds[i], out var a)) continue;
                        if (pts.Count > 0)
                        {
                            var prev = pts[^1];
                            for (int k = 1; k <= 12; k++)
                            {
                                float t = k / 12f;
                                var m = Vector3.Lerp(prev, a, t);
                                m.y += Mathf.Sin(t * Mathf.PI) * (1.2f + 0.08f * Vector3.Distance(prev, a));
                                pts.Add(m);
                            }
                        }
                        else pts.Add(a);
                    }
                    if (pts.Count < 2) continue;
                    var go = new GameObject($"Cascade {p.Label}");
                    go.transform.SetParent(cascadeRoot, false);
                    var lr = go.AddComponent<LineRenderer>();
                    lr.useWorldSpace = false;
                    lr.positionCount = pts.Count;
                    lr.SetPositions(pts.ToArray());
                    float wdt = Mathf.Lerp(0.05f, 0.16f, Mathf.Clamp01((float)p.Probability * 8f));
                    lr.startWidth = wdt; lr.endWidth = wdt * 0.6f;
                    lr.material = LineMat();
                    var c0 = n == 1 ? Palette.Hex("#FF4D6D") : n == 2 ? Palette.Hex("#FF8C42") : Palette.Hex("#B07CFF");
                    lr.startColor = c0; lr.endColor = new Color(c0.r, c0.g, c0.b, 0.35f);
                    lr.numCapVertices = 4;
                    var spark = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    Destroy(spark.GetComponent<Collider>());
                    spark.transform.SetParent(go.transform, false);
                    spark.transform.localScale = Vector3.one * 0.32f;
                    spark.GetComponent<Renderer>().sharedMaterial = Mat(c0, emissive: true);
                    cascades.Add((lr, spark.transform, pts.ToArray(), 0.35f + 0.1f * n));
                }
            }
        }

        static int Rank(string s) => s == "critical" ? 2 : s == "watch" ? 1 : 0;

        public void ApplyFloorVisibility()
        {
            for (int i = 0; i < levels.Count; i++) levels[i].gameObject.SetActive(i <= MaxFloor);
        }

        public Vector3 RoomFocusPoint(uint roomId) => RoomCenter.TryGetValue(roomId, out var p) ? p : WorldBounds.center;

        void Update()
        {
            float time = Time.time;
            foreach (var kv in markers)
            {
                if (kv.Value == null) continue;
                var risk = Net != null ? Net.Risk(kv.Key) : null;
                float baseSize = (1.1f + (float)(risk?.CascadeProb ?? 0) * 1.6f) * FT;
                bool hot = risk != null && (risk.Tier == "severe" || risk.Tier == "high");
                float pulse = hot ? 1f + 0.18f * Mathf.Sin(time * 5f + kv.Key) : 1f;
                if (kv.Key == SelectedComponent) pulse *= 1.5f;
                kv.Value.localScale = Vector3.one * baseSize * pulse;
            }
            foreach (var kv in tintRenderers)
            {
                if (kv.Value == null) continue;
                var name = kv.Value.gameObject.name;
                if (name.EndsWith("critical"))
                {
                    var m = kv.Value.sharedMaterial;
                    var c = m.GetColor("_BaseColor");
                    // shared material: all critical tints pulse in sync, which reads as an alarm
                    c.a = 0.4f + 0.25f * (0.5f + 0.5f * Mathf.Sin(time * 3f));
                    m.SetColor("_BaseColor", c);
                }
            }
            foreach (var (line, spark, pts, speed) in cascades)
            {
                if (spark == null) continue;
                float u = (time * speed) % 1f;
                float f = u * (pts.Length - 1);
                int i = Mathf.Min(pts.Length - 2, Mathf.FloorToInt(f));
                spark.localPosition = Vector3.Lerp(pts[i], pts[i + 1], f - i);
            }
        }
    }
}

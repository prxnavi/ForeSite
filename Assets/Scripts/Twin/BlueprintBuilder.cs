using System.Collections.Generic;
using System.Linq;
using SpacetimeDB.Types;
using UnityEngine;

namespace CbreTwin
{
    /// <summary>Tag on floor colliders so clicks resolve to a room.</summary>
    public class RoomTag : MonoBehaviour { public uint RoomId; }

    /// <summary>Raises a piece out of the ground so the model assembles itself.</summary>
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
            float k = Mathf.Clamp01((t - Delay) / 0.45f);
            k = 1f - Mathf.Pow(1f - k, 3f);
            transform.localScale = new Vector3(target.x, Mathf.Max(0.0001f, target.y * k), target.z);
            if (k >= 1f) Destroy(this);
        }
    }

    /// <summary>A walkable connection between two rooms (door, open plan, or patio slider).</summary>
    public struct Doorway
    {
        public uint A, B;
        public Vector3 PointFt; // centre of the opening at floor level
    }

    /// <summary>Procedurally generates a walkable 3D model from blueprint rows.</summary>
    public class BlueprintBuilder : MonoBehaviour
    {
        public const float FT = 0.3048f;
        public const int WallLayer = 2; // "Ignore Raycast": blocks the visitor, not mouse picks
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
        public readonly List<Doorway> Doorways = new();
        public uint SelectedComponent;
        public uint SelectedRoom;

        readonly List<float> levelBase = new();
        readonly List<Transform> levels = new();
        Transform root, overlayRoot, systemsRoot, cascadeRoot, groundRoot;
        readonly Dictionary<string, Material> mats = new();
        readonly Dictionary<uint, Transform> markers = new();
        readonly List<Material> criticalTints = new();
        readonly List<(LineRenderer line, Transform spark, Vector3[] pts, float speed)> cascades = new();
        float delay;
        bool animateBuild = true;

        TwinConnection Net => TwinConnection.I;

        public float LevelBaseFt(int floor) => floor >= 0 && floor < levelBase.Count ? levelBase[floor] : 0f;

        public static Vector3 W(float xFt, float yFt, float zFt) => new(xFt * FT, yFt * FT, zFt * FT);

        // ───────────── Materials ─────────────

        Shader litShader;

        Material Mat(Color c, bool transparent = false, float alpha = 1f, bool emissive = false, float smooth = 0.2f)
        {
            var key = $"{ColorUtility.ToHtmlStringRGBA(c)}:{transparent}:{alpha:0.00}:{emissive}:{smooth:0.0}";
            if (mats.TryGetValue(key, out var m)) return m;
            litShader ??= Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            m = new Material(litShader);
            var col = new Color(c.r, c.g, c.b, transparent ? alpha : 1f);
            m.SetColor("_BaseColor", col);
            m.color = col;
            m.SetFloat("_Smoothness", transparent ? 0.92f : smooth);
            if (transparent)
            {
                m.SetFloat("_Surface", 1f);
                m.SetFloat("_Blend", 0f);
                m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                m.SetFloat("_ZWrite", 0f);
                m.SetOverrideTag("RenderType", "Transparent");
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
            if (emissive)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", c * 0.6f);
            }
            mats[key] = m;
            return m;
        }

        Material lineMat;
        Material LineMat() => lineMat ??= new Material(Shader.Find("Sprites/Default"));

        // ───────────── Primitive helpers ─────────────

        GameObject Box(Transform parent, Vector3 centerFt, Vector3 sizeFt, Color c, bool keepCollider = false, bool transparent = false, float alpha = 1f, bool animate = true, bool solid = false, float smooth = 0.2f)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = centerFt * FT;
            go.transform.localScale = sizeFt * FT;
            go.GetComponent<Renderer>().sharedMaterial = Mat(c, transparent, alpha, smooth: smooth);
            if (solid) go.layer = WallLayer;            // keeps its collider for the visitor
            else if (!keepCollider) Destroy(go.GetComponent<Collider>());
            if (animate && animateBuild && !PlanMode)
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
            criticalTints.Clear();
            cascades.Clear();
            ComponentWorld.Clear();
            RoomsById.Clear();
            RoomCenter.Clear();
            Doorways.Clear();
        }

        public void Build(uint bpId, bool animate = true)
        {
            Clear();
            animateBuild = animate;
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

            float minX = rooms.Count > 0 ? (float)rooms.Min(r => r.X) : 0, maxX = rooms.Count > 0 ? (float)rooms.Max(r => r.X + r.W) : 40;
            float minZ = rooms.Count > 0 ? (float)rooms.Min(r => r.Z) : 0, maxZ = rooms.Count > 0 ? (float)rooms.Max(r => r.Z + r.D) : 40;
            var cx = (minX + maxX) / 2f;
            var cz = (minZ + maxZ) / 2f;
            const float pad = 24f;
            delay = 0;

            // Site: a soft ground plane and a slightly lighter lot
            Box(groundRoot, new Vector3(cx, -0.8f, cz), new Vector3(maxX - minX + pad * 6, 0.4f, maxZ - minZ + pad * 6), PlanMode ? Palette.PlanPaper : Palette.Ground, animate: false);
            if (!PlanMode)
            {
                Box(groundRoot, new Vector3(cx, -0.55f, cz), new Vector3(maxX - minX + pad * 2, 0.2f, maxZ - minZ + pad * 2), Palette.Hex("#E1E6D8"), animate: false);
                var garage = rooms.FirstOrDefault(r => r.Kind == "garage" && r.Floor == 0);
                if (garage != null)
                    Box(groundRoot, new Vector3((float)(garage.X + garage.W / 2), -0.4f, (float)garage.Z - pad / 2f), new Vector3((float)garage.W * 0.9f, 0.1f, pad), Palette.Hex("#D6D4CF"), animate: false);
            }

            WorldBounds = new Bounds(W(cx, LevelBaseFt(FloorCount - 1) / 2f, cz), W(maxX - minX, LevelBaseFt(FloorCount - 1) + 10, maxZ - minZ));

            for (int f = 0; f < FloorCount; f++)
            {
                var lv = levels[f];
                var floorRooms = rooms.Where(r => r.Floor == f).ToList();
                float baseY = levelBase[f];
                foreach (var r in floorRooms)
                {
                    delay += 0.025f;
                    BuildSlab(lv, r, baseY);
                    RoomCenter[r.Id] = W((float)(r.X + r.W / 2), baseY + 0.1f, (float)(r.Z + r.D / 2));
                }
                foreach (var r in floorRooms)
                {
                    delay += 0.03f;
                    BuildWalls(lv, r, floorRooms, baseY);
                }
                if (!PlanMode)
                    foreach (var r in floorRooms) BuildFurniture(lv, r, baseY);
                if (ShowRoof && !PlanMode)
                {
                    foreach (var r in floorRooms.Where(r => r.Kind != "pool" && r.Kind != "deck" && r.Kind != "patio" && r.Ceiling > 0))
                    {
                        bool covered = rooms.Any(u => u.Floor == f + 1 && Overlap(r, u) > 0.3 * r.W * r.D);
                        if (!covered)
                            Box(lv, new Vector3((float)(r.X + r.W / 2), baseY + (float)r.Ceiling + 0.4f, (float)(r.Z + r.D / 2)), new Vector3((float)r.W + 1.2f, 0.8f, (float)r.D + 1.2f), Palette.Hex("#F3F1EC"));
                    }
                }
            }
            BuildDuration = delay + 0.5f;
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
            Color c = PlanMode ? Palette.Surface : Palette.Floor(r.Kind);
            GameObject slab;
            if (r.Kind == "pool")
            {
                slab = Box(parent, center + new Vector3(0, -1.6f, 0), new Vector3((float)r.W, 0.3f, (float)r.D), Palette.Hex("#5E9DB0"), keepCollider: true);
                Box(parent, center + new Vector3(0, -0.3f, 0), new Vector3((float)r.W, 0.2f, (float)r.D), Palette.Hex("#A9CFD8"), transparent: true, alpha: 0.6f);
                Box(parent, new Vector3(center.x, baseY - 0.1f, (float)r.Z - 0.6f), new Vector3((float)r.W + 2.4f, 0.4f, 1.2f), Palette.Hex("#EDEAE3"));
                Box(parent, new Vector3(center.x, baseY - 0.1f, (float)(r.Z + r.D) + 0.6f), new Vector3((float)r.W + 2.4f, 0.4f, 1.2f), Palette.Hex("#EDEAE3"));
            }
            else
            {
                slab = Box(parent, center, new Vector3((float)r.W, SlabT, (float)r.D), c, keepCollider: true, smooth: r.Kind == "kitchen" || r.Kind == "bath" ? 0.45f : 0.25f);
            }
            slab.AddComponent<RoomTag>().RoomId = r.Id;
            slab.name = $"Room {r.Name}";

            if (r.Kind == "patio" && r.Flags.Contains("beam") && !PlanMode)
            {
                var post = Palette.Hex("#A8835F");
                float h = (float)r.Ceiling;
                foreach (var px in new[] { r.X + 0.6, r.X + r.W / 2, r.X + r.W - 0.6 })
                    Box(parent, new Vector3((float)px, baseY + h / 2f, (float)r.Z + 0.6f), new Vector3(0.8f, h, 0.8f), post, solid: true);
                Box(parent, new Vector3((float)(r.X + r.W / 2), baseY + h + 0.6f, (float)r.Z + 0.6f), new Vector3((float)r.W, 1.2f, 0.9f), Palette.Hex("#96714F"));
                Box(parent, new Vector3((float)(r.X + r.W / 2), baseY + h + 1.4f, (float)(r.Z + r.D / 2)), new Vector3((float)r.W + 1f, 0.4f, (float)r.D + 1f), Palette.Hex("#EDEAE4"));
            }
            if (r.Kind == "deck" && !PlanMode)
            {
                foreach (var e in Edges(r))
                {
                    var (cen, size) = EdgeBox(e, e.A0, e.A1, baseY + 1.75f, 3.5f, 0.12f);
                    Box(parent, cen, size, Palette.Glass, transparent: true, alpha: 0.3f, solid: true);
                    var (rc, rs) = EdgeBox(e, e.A0, e.A1, baseY + 3.5f, 0.15f, 0.25f);
                    Box(parent, rc, rs, Palette.Frame);
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

        static Vector3 EdgePoint(Edge e, float a, float y) => e.Horiz ? new Vector3(a, y, e.C) : new Vector3(e.C, y, a);

        void AddDoorway(Room a, Room b, Edge e, float a0, float a1, float baseY)
        {
            Doorways.Add(new Doorway { A = a.Id, B = b.Id, PointFt = EdgePoint(e, (a0 + a1) / 2f, baseY) });
        }

        void BuildWalls(Transform parent, Room r, List<Room> floorRooms, float baseY)
        {
            if (!HasWalls(r)) return;
            float h = (float)r.Ceiling;
            Color wallCol = PlanMode ? Palette.PlanInk : Palette.WallInterior;
            Color extCol = PlanMode ? Palette.PlanInk : Palette.WallExterior;

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
                        ExteriorWall(parent, r, e, s.a0, s.a1, baseY, h, extCol, s.n);
                    else if (r.Id < s.n.Id)
                        InteriorWall(parent, r, s.n, e, s.a0, s.a1, baseY, Mathf.Min(h, (float)s.n.Ceiling), wallCol);
                    cursor = Mathf.Max(cursor, s.a1);
                }
                if (e.A1 > cursor + 0.3f) ExteriorWall(parent, r, e, cursor, e.A1, baseY, h, extCol, null);
            }
        }

        static readonly HashSet<string> OpenPlan = new() { "living", "kitchen", "dining" };

        static bool GarageBlocks(Room a, Room b)
        {
            bool Ok(string k) => k == "mudroom" || k == "laundry" || k == "kitchen" || k == "hall";
            return (a.Kind == "garage" && !Ok(b.Kind)) || (b.Kind == "garage" && !Ok(a.Kind));
        }

        void InteriorWall(Transform parent, Room a, Room b, Edge e, float a0, float a1, float baseY, float h, Color c)
        {
            float len = a1 - a0;
            bool open = OpenPlan.Contains(a.Kind) && OpenPlan.Contains(b.Kind);
            bool noDoor = len < 3.2f || GarageBlocks(a, b);
            if (open && len > 6)
            {
                Segment(parent, e, a0, a0 + 1f, baseY, 0, h, c);
                Segment(parent, e, a1 - 1f, a1, baseY, 0, h, c);
                Segment(parent, e, a0 + 1f, a1 - 1f, baseY, h - 1f, h, c);
                AddDoorway(a, b, e, a0, a1, baseY);
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
            if (!PlanMode)
            {
                // slim door casing
                var (fc, fs) = EdgeBox(e, mid - dw / 2 - 0.15f, mid + dw / 2 + 0.15f, baseY + 7.1f, 0.25f, WallT + 0.08f);
                Box(parent, fc, fs, Palette.Hex("#E9E5DD"));
            }
            AddDoorway(a, b, e, mid - dw / 2, mid + dw / 2, baseY);
        }

        void ExteriorWall(Transform parent, Room r, Edge e, float a0, float a1, float baseY, float h, Color c, Room toOutdoor)
        {
            float len = a1 - a0;
            float glaz = (float)r.Glazing;
            bool slider = toOutdoor != null && len >= 6f;
            if (r.Kind == "garage" && len >= 16f && e.Side == 0)
            {
                float dw = Mathf.Min(16f, len - 2f);
                float mid = (a0 + a1) / 2f;
                Segment(parent, e, a0, mid - dw / 2, baseY, 0, h, c);
                Segment(parent, e, mid + dw / 2, a1, baseY, 0, h, c);
                Segment(parent, e, mid - dw / 2, mid + dw / 2, baseY, 8f, h, c);
                var (gc, gs) = EdgeBox(e, mid - dw / 2, mid + dw / 2, baseY + 4f, 8f, 0.25f);
                Box(parent, gc, gs, PlanMode ? Palette.PlanInk : Palette.Hex("#DAD7D0"), solid: true);
                return;
            }
            if (slider)
            {
                float dw = Mathf.Min(len * 0.6f, 12f);
                float mid = (a0 + a1) / 2f;
                Opening(parent, e, a0, a1, mid - dw / 2, mid + dw / 2, 0f, 8f, baseY, h, c, walkable: true);
                AddDoorway(r, toOutdoor, e, mid - dw / 2, mid + dw / 2, baseY);
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
            if (!wallOfGlass && len > 14f)
            {
                float half = (a0 + a1) / 2f;
                ExteriorWindow(parent, e, a0, half, ww / 2f, sill, head, baseY, h, c);
                ExteriorWindow(parent, e, half, a1, ww / 2f, sill, head, baseY, h, c);
            }
            else ExteriorWindow(parent, e, a0, a1, ww, sill, head, baseY, h, c);
        }

        void ExteriorWindow(Transform parent, Edge e, float a0, float a1, float ww, float sill, float head, float baseY, float h, Color c)
        {
            float mid = (a0 + a1) / 2f;
            ww = Mathf.Min(ww, a1 - a0 - 0.8f);
            if (ww < 1.5f) { Segment(parent, e, a0, a1, baseY, 0, h, c); return; }
            Opening(parent, e, a0, a1, mid - ww / 2, mid + ww / 2, sill, head, baseY, h, c, walkable: false);
        }

        void Opening(Transform parent, Edge e, float a0, float a1, float o0, float o1, float sill, float head, float baseY, float h, Color c, bool walkable)
        {
            Segment(parent, e, a0, o0, baseY, 0, h, c);
            Segment(parent, e, o1, a1, baseY, 0, h, c);
            if (sill > 0.05f) Segment(parent, e, o0, o1, baseY, 0, sill, c);
            if (head < h - 0.05f) Segment(parent, e, o0, o1, baseY, head, h, c);
            if (PlanMode)
            {
                var (pc, ps) = EdgeBox(e, o0, o1, baseY + 0.8f, 0.4f, 0.15f);
                Box(parent, pc, ps, Palette.Slate);
                return;
            }
            if (!walkable)
            {
                var (gc, gs) = EdgeBox(e, o0, o1, baseY + (sill + head) / 2f, head - sill, 0.1f);
                Box(parent, gc, gs, Palette.Glass, transparent: true, alpha: 0.28f, solid: true);
            }
            // thin dark frame: sill + head + jambs
            var (sc, ss) = EdgeBox(e, o0, o1, baseY + sill, 0.18f, WallT + 0.06f);
            Box(parent, sc, ss, Palette.Frame);
            var (hc, hs) = EdgeBox(e, o0, o1, baseY + head, 0.18f, WallT + 0.06f);
            Box(parent, hc, hs, Palette.Frame);
            if (!walkable && o1 - o0 > 5f)
            {
                var (mc, ms) = EdgeBox(e, (o0 + o1) / 2f - 0.08f, (o0 + o1) / 2f + 0.08f, baseY + (sill + head) / 2f, head - sill, 0.14f);
                Box(parent, mc, ms, Palette.Frame);
            }
        }

        void Segment(Transform parent, Edge e, float a0, float a1, float baseY, float y0, float y1, Color c)
        {
            if (a1 - a0 < 0.05f || y1 - y0 < 0.05f) return;
            var (cen, size) = EdgeBox(e, a0, a1, baseY + (y0 + y1) / 2f, y1 - y0, WallT);
            if (PlanMode)
            {
                if (y0 > 0.1f) return;
                size.y = 1.2f; cen.y = baseY + 0.6f;
            }
            Box(parent, cen, size, c, solid: !PlanMode);
        }

        // ───────────── Furniture ─────────────

        void BuildFurniture(Transform p, Room r, float y)
        {
            float cx = (float)(r.X + r.W / 2), cz = (float)(r.Z + r.D / 2), w = (float)r.W, d = (float)r.D;
            var oak = Palette.Hex("#B08B66"); var fabric = Palette.Hex("#A9A49B"); var linen = Palette.Hex("#F3F0EA");
            var stone = Palette.Hex("#F1EFEA"); var dark = Palette.Hex("#3A3C40");
            switch (r.Kind)
            {
                case "living":
                    Box(p, new Vector3(cx, y + 0.04f, cz), new Vector3(w * 0.5f, 0.06f, d * 0.42f), Palette.Hex("#D8CFC2"));
                    Box(p, new Vector3(cx, y + 0.9f, cz + d * 0.28f), new Vector3(Mathf.Min(8, w * 0.5f), 1.8f, 3f), fabric);
                    Box(p, new Vector3(cx, y + 1.6f, cz + d * 0.28f + 1.2f), new Vector3(Mathf.Min(8, w * 0.5f), 1.4f, 0.7f), fabric);
                    Box(p, new Vector3(cx, y + 0.65f, cz), new Vector3(4f, 0.25f, 2.2f), oak);
                    break;
                case "kitchen":
                    Box(p, new Vector3(cx, y + 1.5f, cz), new Vector3(Mathf.Min(8, w * 0.45f), 3f, 3.4f), dark);
                    Box(p, new Vector3(cx, y + 3.05f, cz), new Vector3(Mathf.Min(8, w * 0.45f) + 0.3f, 0.12f, 3.7f), stone);
                    Box(p, new Vector3(cx, y + 1.5f, (float)r.Z + 1.3f), new Vector3(w * 0.85f, 3f, 2.1f), Palette.Hex("#E7E3DB"));
                    Box(p, new Vector3(cx, y + 6f, (float)r.Z + 0.9f), new Vector3(w * 0.85f, 2.4f, 1.2f), Palette.Hex("#E7E3DB"));
                    break;
                case "dining":
                    Box(p, new Vector3(cx, y + 2.45f, cz), new Vector3(Mathf.Min(6.5f, w * 0.5f), 0.15f, 3.4f), oak);
                    Box(p, new Vector3(cx, y + 1.2f, cz), new Vector3(0.5f, 2.4f, 0.5f), oak);
                    Box(p, new Vector3(cx, y + 6.6f, cz), new Vector3(1.4f, 0.6f, 1.4f), Palette.Hex("#C9B79C"));
                    break;
                case "bedroom":
                case "primary":
                    {
                        float bw = r.Kind == "primary" ? 6.5f : 5f;
                        float bz = cz + d * 0.12f;
                        Box(p, new Vector3(cx, y + 0.8f, bz), new Vector3(bw, 1.6f, 6.8f), linen);
                        Box(p, new Vector3(cx, y + 1.65f, bz - 1.4f), new Vector3(bw + 0.1f, 0.2f, 2.6f), Palette.Hex("#C9BBA8"));
                        Box(p, new Vector3(cx, y + 1.9f, bz + 3.5f), new Vector3(bw + 0.4f, 3.8f, 0.35f), oak);
                        Box(p, new Vector3(cx + bw / 2f + 1.2f, y + 1f, bz + 2.9f), new Vector3(1.6f, 2f, 1.4f), oak);
                        break;
                    }
                case "bath":
                    Box(p, new Vector3(cx, y + 0.9f, (float)(r.Z + r.D) - 1.6f), new Vector3(Mathf.Min(5, w - 1), 1.8f, 2.6f), stone);
                    Box(p, new Vector3((float)r.X + 1.2f, y + 1.5f, cz), new Vector3(1.8f, 3f, Mathf.Min(4, d - 2)), Palette.Hex("#E2DED6"));
                    break;
                case "office":
                    Box(p, new Vector3(cx, y + 2.4f, cz), new Vector3(5f, 0.15f, 2.4f), oak);
                    Box(p, new Vector3(cx, y + 3.2f, cz + 0.8f), new Vector3(2.2f, 1.4f, 0.1f), dark);
                    Box(p, new Vector3(cx, y + 1.2f, cz - 1.8f), new Vector3(1.8f, 2.4f, 1.8f), fabric);
                    break;
                case "game":
                    Box(p, new Vector3(cx, y + 2.6f, cz), new Vector3(4.2f, 0.4f, 7.5f), Palette.Hex("#3F6B57"));
                    Box(p, new Vector3(cx, y + 1.2f, cz), new Vector3(3.6f, 2.4f, 6.8f), oak);
                    break;
                case "media":
                    Box(p, new Vector3(cx, y + 1f, cz + d * 0.2f), new Vector3(w * 0.6f, 2f, 3.2f), Palette.Hex("#4A4C52"));
                    Box(p, new Vector3(cx, y + 4f, (float)r.Z + 0.6f), new Vector3(w * 0.6f, 3.6f, 0.15f), Palette.Hex("#151618"));
                    break;
                case "garage":
                    {
                        int cars = Mathf.Clamp(Mathf.FloorToInt(w / 10.5f), 1, 3);
                        var cols = new[] { Palette.Hex("#8C939C"), Palette.Hex("#3B3E44"), Palette.Hex("#D8D4CC") };
                        for (int i = 0; i < cars; i++)
                        {
                            float x = (float)r.X + w * (i + 0.5f) / cars;
                            Box(p, new Vector3(x, y + 1.4f, cz), new Vector3(6f, 2.6f, 14.5f), cols[i], smooth: 0.6f);
                            Box(p, new Vector3(x, y + 3.3f, cz + 1f), new Vector3(5.4f, 1.4f, 7f), Palette.Hex("#AEB8BF"), transparent: true, alpha: 0.75f);
                        }
                        break;
                    }
                case "stair":
                    {
                        const int steps = 12;
                        bool alongZ = d >= w;
                        for (int i = 0; i < steps; i++)
                        {
                            float t = (i + 0.5f) / steps;
                            var size = alongZ ? new Vector3(w * 0.8f, (i + 1) * 0.75f, d / steps) : new Vector3(w / steps, (i + 1) * 0.75f, d * 0.8f);
                            var pos = alongZ ? new Vector3(cx, y + size.y / 2f, (float)r.Z + d * t) : new Vector3((float)r.X + w * t, y + size.y / 2f, cz);
                            Box(p, pos, size, Palette.Hex("#C7B497"));
                        }
                        break;
                    }
                case "patio":
                    Box(p, new Vector3(cx - 3, y + 2.4f, cz), new Vector3(6f, 0.15f, 3.5f), oak);
                    Box(p, new Vector3(cx + 5, y + 0.9f, cz), new Vector3(6f, 1.8f, 2.6f), fabric);
                    break;
                case "deck":
                    Box(p, new Vector3(cx, y + 1.2f, cz), new Vector3(2.5f, 2.4f, 2.5f), Palette.Hex("#6D8C68"));
                    Box(p, new Vector3(cx + 5, y + 0.9f, cz - 4), new Vector3(7f, 1.8f, 2.6f), fabric);
                    break;
            }
        }

        // ───────────── Overlays ─────────────

        public void RefreshOverlays()
        {
            if (root == null || Net == null || !Net.Ready) return;
            foreach (Transform c in overlayRoot) Destroy(c.gameObject);
            foreach (Transform c in systemsRoot) Destroy(c.gameObject);
            foreach (Transform c in cascadeRoot) Destroy(c.gameObject);
            markers.Clear();
            criticalTints.Clear();
            cascades.Clear();
            ComponentWorld.Clear();

            var worst = new Dictionary<uint, string>();
            foreach (var f in Net.Findings(BlueprintId))
                if (!worst.TryGetValue(f.RoomId, out var w) || Rank(f.Severity) > Rank(w)) worst[f.RoomId] = f.Severity;

            if (ShowStructural)
            {
                foreach (var kv in worst)
                {
                    if (kv.Value == "ok" || !RoomsById.TryGetValue(kv.Key, out var r) || r.Floor > MaxFloor) continue;
                    var col = Palette.Severity(kv.Value);
                    var go = Box(overlayRoot, new Vector3((float)(r.X + r.W / 2), LevelBaseFt((int)r.Floor) + 0.06f, (float)(r.Z + r.D / 2)),
                        new Vector3((float)r.W - 0.6f, 0.08f, (float)r.D - 0.6f), col, transparent: true, alpha: kv.Value == "critical" ? 0.42f : 0.3f, animate: false);
                    go.name = $"Tint {r.Name} {kv.Value}";
                    if (kv.Value == "critical") criticalTints.Add(go.GetComponent<Renderer>().sharedMaterial);
                }
            }

            foreach (var c in Net.Components(BlueprintId))
            {
                if (!RoomsById.TryGetValue(c.RoomId, out var r)) continue;
                float x = Mathf.Clamp((float)(r.X + Mathf.Clamp01((float)c.Ox) * r.W), (float)r.X + 1.2f, (float)(r.X + r.W) - 1.2f);
                float z = Mathf.Clamp((float)(r.Z + Mathf.Clamp01((float)c.Oz) * r.D), (float)r.Z + 1.2f, (float)(r.Z + r.D) - 1.2f);
                float yb = LevelBaseFt((int)r.Floor);
                float oy = Mathf.Min((float)c.Oy, (float)r.Ceiling - 1f);
                var pos = new Vector3(x, yb + Mathf.Max(1f, oy), z);
                ComponentWorld[c.Id] = pos * FT;
                if (!ShowSystems || r.Floor > MaxFloor) continue;
                var risk = Net.Risk(c.Id);
                var col = Palette.Tier(risk?.Tier ?? "low");
                var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                s.transform.SetParent(systemsRoot, false);
                s.transform.localPosition = pos * FT;
                s.transform.localScale = Vector3.one * 0.7f * FT;
                s.GetComponent<Renderer>().sharedMaterial = Mat(col, emissive: true, smooth: 0.6f);
                Destroy(s.GetComponent<Collider>());
                markers[c.Id] = s.transform;
            }

            if (ShowCascade)
            {
                int n = 0;
                foreach (var p in Net.Paths(BlueprintId))
                {
                    if (n++ >= 3) break;
                    var pts = new List<Vector3>();
                    foreach (var id in p.ComponentIds)
                    {
                        if (!ComponentWorld.TryGetValue(id, out var a)) continue;
                        if (pts.Count > 0)
                        {
                            var prev = pts[^1];
                            for (int k = 1; k <= 14; k++)
                            {
                                float t = k / 14f;
                                var m = Vector3.Lerp(prev, a, t);
                                m.y += Mathf.Sin(t * Mathf.PI) * (0.9f + 0.06f * Vector3.Distance(prev, a));
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
                    float wdt = n == 1 ? 0.045f : 0.03f;
                    lr.startWidth = wdt; lr.endWidth = wdt;
                    lr.material = LineMat();
                    var c0 = n == 1 ? Palette.Critical : n == 2 ? Palette.Clay : Palette.Slate;
                    lr.startColor = new Color(c0.r, c0.g, c0.b, 0.85f);
                    lr.endColor = new Color(c0.r, c0.g, c0.b, 0.25f);
                    lr.numCapVertices = 4;
                    var spark = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    Destroy(spark.GetComponent<Collider>());
                    spark.transform.SetParent(go.transform, false);
                    spark.transform.localScale = Vector3.one * 0.14f;
                    spark.GetComponent<Renderer>().sharedMaterial = Mat(c0, emissive: true);
                    cascades.Add((lr, spark.transform, pts.ToArray(), 0.22f + 0.06f * n));
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
                bool hot = risk != null && (risk.Tier == "severe" || risk.Tier == "high");
                float s = 0.7f * FT * (hot ? 1f + 0.12f * Mathf.Sin(time * 3f + kv.Key) : 1f);
                if (kv.Key == SelectedComponent) s *= 1.6f;
                kv.Value.localScale = Vector3.one * s;
            }
            float a = 0.3f + 0.15f * (0.5f + 0.5f * Mathf.Sin(time * 2f));
            foreach (var m in criticalTints)
            {
                if (m == null) continue;
                var c = m.GetColor("_BaseColor");
                c.a = a;
                m.SetColor("_BaseColor", c);
            }
            foreach (var (_, spark, pts, speed) in cascades)
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

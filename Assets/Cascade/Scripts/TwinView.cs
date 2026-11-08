using System;
using System.Collections.Generic;
using System.Linq;
using Atlas;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Cascade
{
    /// <summary>
    /// The building twin. Everything is authored in floor-plan meters under this transform,
    /// so the same model is a tabletop (scale ≈ 1:28) or life-size (scale 1) just by scaling it.
    /// Reads rooms/assets/dependencies from SpacetimeDB and colors them from the simulation
    /// frames of the scenario on the shared timeline.
    /// </summary>
    public class TwinView : MonoBehaviour
    {
        public event Action<string, bool> Picked; // id, isAsset

        public float Hour;              // displayed timeline hour (0..24)
        public string Label = "do_nothing";
        public bool Xray;
        /// Structure view: walls colored by type and load; proposals drawn in place.
        public bool Structure;
        public event Action<string> WallPicked;
        public bool LifeSize;
        public string Selected;
        /// Where the guide trail starts (your spot on the plan in walk-in mode).
        public Func<Vector2?> TrailFrom;

        CascadeNet net;
        Camera cam;
        Vector2 center;
        string signature = "";
        bool built, buildAnimating;
        float buildT;

        class RoomVis { public Net.Room row; public Renderer floor; public TextMeshPro name, temp, sign; public GameObject ceiling; public Material litFloor; public Renderer ceilR; }
        class AssetVis { public Net.Asset row; public Transform root; public Renderer body, light; public TextMeshPro label; }
        class WallVis { public Transform t; public Renderer r; public float delay; public Transform rail, foot; public Renderer railR, footR; public bool h; public float c, a0, a1; public string wallId; }
        class Seg { public Renderer r; public string from; public string kind; }

        readonly Dictionary<string, RoomVis> rooms = new();
        readonly Dictionary<string, AssetVis> assets = new();
        readonly List<WallVis> walls = new();
        readonly List<Seg> ducts = new();
        readonly List<Renderer> thread = new();
        readonly List<Renderer> trail = new();
        readonly Dictionary<string, Transform> people = new();
        readonly Dictionary<string, Vector3> peopleGoal = new();
        readonly HashSet<string> highlight = new();
        readonly Dictionary<string, float> pulseUntil = new();
        Transform staticRoot, fxRoot, peopleRoot, interior;
        GameObject slab;
        Transform threadBead;
        List<Vector3> threadPts = new();
        string trailTarget;
        int seenVersion = -1;

        public const float TabletopScale = 0.035f;

        public void Bind(CascadeNet n, Camera c)
        {
            net = n; cam = c;
            net.SceneCommand += OnCommand;
        }

        // ---------------- coordinates ----------------

        public Vector3 L(float x, float z, float y = 0) => new(x - center.x, y, z - center.y);

        public Vector3 LocalOf(string id, float y = 0)
        {
            if (assets.TryGetValue(id, out var a)) return L(a.row.X, a.row.Z, y);
            if (rooms.TryGetValue(id, out var r)) return L((r.row.X0 + r.row.X1) / 2, (r.row.Z0 + r.row.Z1) / 2, y);
            return L(center.x, center.y, y);
        }

        public Vector3 WorldOf(string id, float y = 0) => transform.TransformPoint(LocalOf(id, y));
        public Vector2 PlanPoint(Vector3 world) { var l = transform.InverseTransformPoint(world); return new Vector2(l.x + center.x, l.z + center.y); }
        public bool Has(string id) => assets.ContainsKey(id) || rooms.ContainsKey(id);
        public Net.Asset AssetRow(string id) => assets.TryGetValue(id, out var a) ? a.row : null;
        public Net.Room RoomRow(string id) => rooms.TryGetValue(id, out var r) ? r.row : null;
        public Vector2 Size { get; private set; } = new(24, 16);

        // ---------------- build ----------------

        void Rebuild()
        {
            if (staticRoot) Destroy(staticRoot.gameObject);
            rooms.Clear(); assets.Clear(); walls.Clear(); ducts.Clear();
            staticRoot = new GameObject("Building").transform;
            staticRoot.SetParent(transform, false);

            var roomRows = net.Db.Room.Iter().ToList();
            float minX = roomRows.Min(r => r.X0), maxX = roomRows.Max(r => r.X1);
            float minZ = roomRows.Min(r => r.Z0), maxZ = roomRows.Max(r => r.Z1);
            center = new Vector2((minX + maxX) / 2, (minZ + maxZ) / 2);
            Size = new Vector2(maxX - minX, maxZ - minZ);

            // base slab
            slab = CascadeStyle.Cube(staticRoot, "Slab", L(center.x, center.y, -0.12f), new Vector3(Size.x + 0.6f, 0.2f, Size.y + 0.6f), CascadeStyle.Solid);
            CascadeStyle.Tint(slab.GetComponent<Renderer>(), AtlasStyle.Hex("#2B3440").A(0.9f));

            foreach (var r in roomRows)
            {
                var v = new RoomVis { row = r };
                float w = r.X1 - r.X0, d = r.Z1 - r.Z0;
                var floor = CascadeStyle.Cube(staticRoot, "Floor " + r.Id, L((r.X0 + r.X1) / 2, (r.Z0 + r.Z1) / 2, 0.03f),
                    new Vector3(w - 0.08f, 0.06f, d - 0.08f), CascadeStyle.Flat, collider: r.Kind != "corridor");
                v.floor = floor.GetComponent<Renderer>();
                var ftex = FloorTexture(r);
                v.litFloor = CascadeStyle.Lit(ftex, r.Kind == "common" ? 0.45f : 0.12f);
                CascadeStyle.Tile(v.floor, w, d, ProcTex.RepeatMeters(ftex));
                if (r.Kind != "corridor") floor.AddComponent<Pick>().Init(this, r.Id, false);
                bool small = r.Kind == "corridor";
                v.name = MakeLabel(staticRoot, r.Name, small ? 3f : 4.6f, CascadeStyle.Ink, L((r.X0 + r.X1) / 2, (r.Z0 + r.Z1) / 2 + (small ? 0 : 0.35f), 0.08f),
                    new Vector2(w - 0.6f, 1.2f), flat: true, bold: !small);
                if (!small && r.Kind != "mech")
                    v.temp = MakeLabel(staticRoot, "", 3.6f, CascadeStyle.Ink.A(0.75f), L((r.X0 + r.X1) / 2, (r.Z0 + r.Z1) / 2 - 0.55f, 0.08f),
                        new Vector2(w - 0.6f, 1f), flat: true, bold: false);
                if (r.Kind != "corridor")
                {
                    var inward = new Vector2((r.X0 + r.X1) / 2 - r.DoorX, (r.Z0 + r.Z1) / 2 - r.DoorZ).normalized;
                    v.sign = MakeLabel(staticRoot, r.Name, 1.2f, CascadeStyle.Ink, L(r.DoorX + inward.x * 0.7f, r.DoorZ + inward.y * 0.7f, 2.4f), new Vector2(w, 1.2f), flat: false, bold: true);
                    v.sign.gameObject.SetActive(false);
                }
                // walk-through interior: a ceiling with a light panel (hidden on the tabletop)
                v.ceiling = new GameObject("Ceiling " + r.Id);
                v.ceiling.transform.SetParent(staticRoot, false);
                var ceil = CascadeStyle.Cube(v.ceiling.transform, "Tiles", L((r.X0 + r.X1) / 2, (r.Z0 + r.Z1) / 2, 2.72f), new Vector3(w, 0.04f, d), CascadeStyle.Lit(ProcTex.Kind.Plaster, 0.05f));
                v.ceilR = ceil.GetComponent<Renderer>();
                CascadeStyle.Tile(v.ceilR, w, d, 2.5f);
                CascadeStyle.Tint(v.ceilR, AtlasStyle.Hex("#F4F2EE"));
                int lights = Mathf.Max(1, Mathf.RoundToInt(w * d / 18f));
                for (int k = 0; k < lights; k++)
                {
                    float fx = r.X0 + w * (k + 0.5f) / lights;
                    var lp = CascadeStyle.Cube(v.ceiling.transform, "Light", L(fx, (r.Z0 + r.Z1) / 2, 2.69f), new Vector3(Mathf.Min(1.2f, w * 0.4f), 0.02f, 0.6f), CascadeStyle.Glow);
                    CascadeStyle.Tint(lp.GetComponent<Renderer>(), Color.white, Color.white * 0.5f);
                }
                v.ceiling.SetActive(false);
                rooms[r.Id] = v;
            }

            BuildWalls(roomRows);

            foreach (var a in net.Db.Asset.Iter())
            {
                var v = new AssetVis { row = a };
                v.root = new GameObject(a.Id).transform;
                v.root.SetParent(staticRoot, false);
                v.root.localPosition = L(a.X, a.Z);
                Vector3 size = a.Kind switch { "chiller" => new(1.5f, 1.1f, 1.1f), "panel" => new(0.9f, 1.8f, 0.45f), _ => new(1.4f, 0.9f, 0.9f) };
                var body = CascadeStyle.Cube(v.root, "Body", new Vector3(0, size.y / 2 + 0.06f, 0), size, CascadeStyle.Solid, collider: true);
                body.AddComponent<Pick>().Init(this, a.Id, true);
                v.body = body.GetComponent<Renderer>();
                var light = CascadeStyle.Cube(v.root, "Status", new Vector3(0, size.y + 0.16f, 0), new Vector3(size.x * 0.7f, 0.14f, size.z * 0.7f), CascadeStyle.Glow);
                v.light = light.GetComponent<Renderer>();
                v.label = MakeLabel(v.root, "", 3.2f, Color.white, new Vector3(0, size.y + 1.0f, 0), new Vector2(6f, 1.6f), flat: false, bold: true);
                v.label.alignment = TextAlignmentOptions.Bottom;
                assets[a.Id] = v;
            }

            interior = InteriorDecor.Build(this, staticRoot, roomRows, net.Db.Asset.Iter().ToList(), Size);

            // X-ray: pipes and ducts from the dependency graph, at ceiling height
            foreach (var d in net.Db.Dependency.Iter())
            {
                if (!Has(d.FromId) || !Has(d.ToId)) continue;
                float y = d.Kind == "power" ? 2.0f : d.Kind == "chilled_water" ? 2.35f : 2.6f;
                var s = Segment(staticRoot, LocalOf(d.FromId, y), LocalOf(d.ToId, y), d.Kind == "air" ? 0.14f : 0.1f, CascadeStyle.Glow);
                ducts.Add(new Seg { r = s, from = d.FromId, kind = d.Kind });
            }
            built = false;
            buildT = 0;
            seenVersion = -1;
        }

        void BuildWalls(List<Net.Room> roomRows)
        {
            // Union every room edge per line, then cut a 1.2 m gap at each door.
            var lines = new Dictionary<(bool h, float c), List<Vector2>>();
            void Add(bool h, float c, float a0, float a1)
            {
                var k = (h, Mathf.Round(c * 100) / 100);
                if (!lines.TryGetValue(k, out var l)) lines[k] = l = new List<Vector2>();
                l.Add(new Vector2(Mathf.Min(a0, a1), Mathf.Max(a0, a1)));
            }
            foreach (var r in roomRows)
            {
                Add(true, r.Z0, r.X0, r.X1); Add(true, r.Z1, r.X0, r.X1);
                Add(false, r.X0, r.Z0, r.Z1); Add(false, r.X1, r.Z0, r.Z1);
            }
            int i = 0;
            foreach (var (key, list) in lines)
            {
                var merged = new List<Vector2>();
                foreach (var s in list.OrderBy(s => s.x))
                {
                    if (merged.Count > 0 && s.x <= merged[^1].y + 0.01f) merged[^1] = new Vector2(merged[^1].x, Mathf.Max(merged[^1].y, s.y));
                    else merged.Add(s);
                }
                foreach (var r in roomRows)
                {
                    bool onLine = key.h ? Mathf.Abs(r.DoorZ - key.c) < 0.01f : Mathf.Abs(r.DoorX - key.c) < 0.01f;
                    if (!onLine) continue;
                    float d = key.h ? r.DoorX : r.DoorZ;
                    for (int k = 0; k < merged.Count; k++)
                    {
                        var s = merged[k];
                        if (d <= s.x || d >= s.y) continue;
                        merged.RemoveAt(k);
                        if (d - 0.6f > s.x + 0.05f) merged.Insert(k++, new Vector2(s.x, d - 0.6f));
                        if (d + 0.6f < s.y - 0.05f) merged.Insert(k, new Vector2(d + 0.6f, s.y));
                        break;
                    }
                }
                foreach (var s in merged)
                {
                    float len = s.y - s.x, mid = (s.x + s.y) / 2;
                    var pos = key.h ? L(mid, key.c) : L(key.c, mid);
                    var size = key.h ? new Vector3(len + 0.15f, 1, 0.15f) : new Vector3(0.15f, 1, len + 0.15f);
                    var go = CascadeStyle.Cube(staticRoot, "Wall", pos, size, CascadeStyle.WallMat, collider: true);
                    var railSize = key.h ? new Vector3(len + 0.15f, 0.07f, 0.17f) : new Vector3(0.17f, 0.07f, len + 0.15f);
                    var rail = CascadeStyle.Cube(staticRoot, "Rail", pos, railSize, CascadeStyle.Glow);
                    var footSize = key.h ? new Vector3(len + 0.15f, 0.1f, 0.18f) : new Vector3(0.18f, 0.1f, len + 0.15f);
                    var foot = CascadeStyle.Cube(staticRoot, "Baseboard", pos + Vector3.up * 0.05f, footSize, CascadeStyle.Solid);
                    CascadeStyle.Tile(go.GetComponent<Renderer>(), len, 2.7f, 2.5f);
                    var wv = new WallVis { t = go.transform, r = go.GetComponent<Renderer>(), delay = (pos.magnitude / 14f) + (i++ % 5) * 0.03f,
                        rail = rail.transform, railR = rail.GetComponent<Renderer>(), foot = foot.transform, footR = foot.GetComponent<Renderer>(),
                        h = key.h, c = key.c, a0 = s.x, a1 = s.y };
                    // which structural wall (server row) this segment belongs to
                    var mid2 = (s.x + s.y) / 2;
                    var row = net.Db.Wall.Iter().FirstOrDefault(wr => key.h
                        ? Mathf.Abs(wr.Z0 - wr.Z1) < 0.01f && Mathf.Abs(wr.Z0 - key.c) < 0.05f && mid2 > Mathf.Min(wr.X0, wr.X1) && mid2 < Mathf.Max(wr.X0, wr.X1)
                        : Mathf.Abs(wr.X0 - wr.X1) < 0.01f && Mathf.Abs(wr.X0 - key.c) < 0.05f && mid2 > Mathf.Min(wr.Z0, wr.Z1) && mid2 < Mathf.Max(wr.Z0, wr.Z1));
                    wv.wallId = row?.Id;
                    if (row != null) go.AddComponent<WallPick>().Init(this, row.Id);
                    walls.Add(wv);
                }
            }
        }

        static Renderer Segment(Transform parent, Vector3 a, Vector3 b, float width, Material mat)
        {
            var go = CascadeStyle.Cube(parent, "Seg", (a + b) / 2, new Vector3(width, width, Vector3.Distance(a, b) + width * 0.5f), mat);
            if ((b - a).sqrMagnitude > 1e-6f) go.transform.localRotation = Quaternion.LookRotation(b - a, Vector3.up);
            return go.GetComponent<Renderer>();
        }

        public static TextMeshPro MakeLabel(Transform parent, string text, float size, Color color, Vector3 pos, Vector2 box, bool flat, bool bold)
        {
            var go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            if (flat) go.transform.localRotation = Quaternion.Euler(90, 0, 0);
            var t = go.AddComponent<TextMeshPro>();
            t.text = text;
            t.fontSize = size;
            t.color = color;
            t.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
            t.alignment = TextAlignmentOptions.Center;
            t.textWrappingMode = TextWrappingModes.Normal;
            t.overflowMode = TextOverflowModes.Overflow;
            t.rectTransform.sizeDelta = box;
            return t;
        }

        // ---------------- scene commands ----------------

        void OnCommand(SceneCmd c)
        {
            switch (c.kind)
            {
                case "clear":
                    highlight.Clear(); SetThread(null); trailTarget = null; RebuildTrail(); break;
                case "highlight":
                    highlight.Clear(); foreach (var id in c.ids ?? Array.Empty<string>()) highlight.Add(id); break;
                case "pulse":
                    foreach (var id in c.ids ?? Array.Empty<string>()) pulseUntil[id] = Time.time + 8f; break;
                case "thread":
                    SetThread(c.ids); break;
                case "guide_trail":
                    trailTarget = c.target; RebuildTrail(); break;
                case "focus":
                    Selected = c.target; break;
            }
        }

        void SetThread(string[] ids)
        {
            foreach (var r in thread) if (r) Destroy(r.gameObject);
            thread.Clear();
            threadPts.Clear();
            if (threadBead) Destroy(threadBead.gameObject);
            if (ids == null || ids.Length < 2 || !fxRoot) return;
            threadPts = ids.Where(Has).Select(id => LocalOf(id, 3.0f)).ToList();
            for (int i = 0; i + 1 < threadPts.Count; i++) thread.Add(Segment(fxRoot, threadPts[i], threadPts[i + 1], 0.12f, CascadeStyle.Glow));
            var bead = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(bead.GetComponent<Collider>());
            bead.transform.SetParent(fxRoot, false);
            bead.transform.localScale = Vector3.one * 0.15f;
            bead.GetComponent<Renderer>().sharedMaterial = CascadeStyle.Glow;
            CascadeStyle.Tint(bead.GetComponent<Renderer>(), Color.white);
            threadBead = bead.transform;
        }

        /// Path on the floor: from where the technician stands (or the entrance) along the corridor to the target.
        public void RebuildTrail()
        {
            foreach (var r in trail) if (r) Destroy(r.gameObject);
            trail.Clear();
            if (string.IsNullOrEmpty(trailTarget) || !fxRoot || !Has(trailTarget)) return;

            var tech = net.Db.User.Iter().FirstOrDefault(u => u.Online && u.Present && u.Role == "technician");
            var mine = TrailFrom?.Invoke();
            Vector2 start = mine ?? (tech != null ? new Vector2(tech.X, tech.Z) : new Vector2(0.6f, 8f));
            var targetRoom = assets.TryGetValue(trailTarget, out var a) ? RoomRow(a.row.RoomId) : RoomRow(trailTarget);
            Vector2 goal = assets.ContainsKey(trailTarget) ? new Vector2(a.row.X, a.row.Z + (a.row.Z < targetRoom?.DoorZ ? 0.9f : -0.9f))
                : new Vector2((targetRoom.X0 + targetRoom.X1) / 2, (targetRoom.Z0 + targetRoom.Z1) / 2);
            float corridorZ = 8f;
            var pts = new List<Vector2> { start, new(start.x, corridorZ) };
            if (targetRoom != null && targetRoom.Kind != "corridor")
            {
                pts.Add(new Vector2(targetRoom.DoorX, corridorZ));
                pts.Add(new Vector2(targetRoom.DoorX, targetRoom.DoorZ));
                pts.Add(new Vector2(targetRoom.DoorX, goal.y));
            }
            pts.Add(goal);

            // dashes every 0.7 m
            float dist = 0;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                var p0 = pts[i]; var p1 = pts[i + 1];
                float len = Vector2.Distance(p0, p1);
                for (float s = 0; s < len; s += 0.7f)
                {
                    var p = Vector2.Lerp(p0, p1, s / Mathf.Max(0.001f, len));
                    var q = Vector2.Lerp(p0, p1, Mathf.Min(len, s + 0.4f) / Mathf.Max(0.001f, len));
                    var seg = Segment(fxRoot, L(p.x, p.y, 0.05f), L(q.x, q.y, 0.05f), 0.3f, CascadeStyle.Glow);
                    var sc = seg.transform.localScale; sc.y = 0.025f; seg.transform.localScale = sc;
                    seg.gameObject.name = "Trail " + (dist + s).ToString("0.0");
                    trail.Add(seg);
                }
                dist += len;
            }
        }

        // ---------------- per frame ----------------

        void Update()
        {
            if (net == null || !net.Ready) return;
            var sig = string.Join(",", net.Db.Room.Iter().Select(r => r.Id)) + "|" + string.Join(",", net.Db.Asset.Iter().Select(a => a.Id));
            if (sig != signature && net.Db.Room.Count > 0)
            {
                signature = sig;
                Rebuild();
                if (!fxRoot) { fxRoot = new GameObject("Effects").transform; fxRoot.SetParent(transform, false); }
                if (!peopleRoot) { peopleRoot = new GameObject("People").transform; peopleRoot.SetParent(transform, false); }
            }
            if (!staticRoot) return;

            if (net.Version != seenVersion)
            {
                seenVersion = net.Version;
                foreach (var v in assets.Values) v.row = net.Db.Asset.Id.Find(v.row.Id) ?? v.row;
                UpdatePeople();
                if (trailTarget != null && trail.Count == 0) RebuildTrail();
            }

            bool shouldBeBuilt = net.Timeline?.Built ?? false;
            if (shouldBeBuilt && !built) { built = true; buildAnimating = true; buildT = 0; }
            if (!shouldBeBuilt && built) { built = false; buildAnimating = false; }
            if (buildAnimating) { buildT += Time.deltaTime; if (buildT > 3f) buildAnimating = false; }

            var frames = net.Frames(net.ScenarioFor(Label));
            float t = Time.time;
            float wallH = LifeSize ? 2.72f : 1.25f;

            if (slab) slab.SetActive(!LifeSize);
            if (interior) interior.gameObject.SetActive(built && !Xray && (!buildAnimating || buildT > 2.2f));
            // walls rise out of the blueprint
            foreach (var w in walls)
            {
                float k = built ? Mathf.SmoothStep(0, 1, Mathf.Clamp01((buildT - w.delay) / 0.8f)) : 0f;
                if (!buildAnimating && built) k = 1;
                float h = Mathf.Lerp(0.03f, wallH, k);
                var s = w.t.localScale; s.y = h; w.t.localScale = s;
                var p = w.t.localPosition; p.y = h / 2; w.t.localPosition = p;
                // walk-through: solid painted walls (x-ray turns them to glass)
                var c = built ? (LifeSize ? (Xray ? AtlasStyle.Hex("#E0F2FE").A(0.08f) : AtlasStyle.Hex("#ECEEF1")) : CascadeStyle.Wall.A(Xray ? 0.08f : 0.85f)) : CascadeStyle.Blueprint.A(0.95f);
                // vertical faces are shaded darker by the shader; lift interior walls so they read as bright paint
                Color emit = !built ? CascadeStyle.Blueprint * 0.3f : LifeSize && !Xray ? new Color(0.3f, 0.3f, 0.31f, 0) : Color.clear;
                var wr = w.wallId != null ? net.Db.Wall.Id.Find(w.wallId) : null;
                if (built && wr != null)
                {
                    if (Structure) { c = StructureColor(wr, t); emit = c * 0.25f; }
                    if (wr.Proposal == "remove")
                    {
                        // the wall being questioned: ghosted red; gone once the change is approved
                        float pulse = 0.5f + 0.5f * Mathf.Sin(t * 3f);
                        c = wr.ProposalState == "approved" ? c.A(0f) : CascadeStyle.Danger.A(0.25f + 0.15f * pulse);
                        emit = CascadeStyle.Danger * 0.2f * pulse;
                    }
                    if (Selected == w.wallId) emit += new Color(0.25f, 0.25f, 0.25f, 0);
                }
                w.r.enabled = c.a > 0.01f;
                // lit plaster when it's just a wall; flat colors for blueprint, x-ray, structure view and proposals
                bool plain = built && !Xray && !Structure && !(wr != null && wr.Proposal == "remove");
                var want = plain ? CascadeStyle.Lit(ProcTex.Kind.Plaster, 0.05f) : CascadeStyle.WallMat;
                if (w.r.sharedMaterial != want) w.r.sharedMaterial = want;
                if (plain) { c = AtlasStyle.Hex("#F7F5F1"); emit = Color.clear; if (Selected == w.wallId) c = AtlasStyle.Hex("#E0F2FE"); }
                CascadeStyle.Tint(w.r, c, emit);
                w.rail.localPosition = new Vector3(p.x, h, p.z);
                w.rail.gameObject.SetActive(built && !LifeSize);
                w.foot.gameObject.SetActive(built && LifeSize);
                var railC = LifeSize ? Color.white.A(0.9f) : Color.white.A(0.95f);
                CascadeStyle.Tint(w.railR, railC, CascadeStyle.Accent * 0.15f);
                CascadeStyle.Tint(w.footR, AtlasStyle.Hex("#6B7280"));
            }

            UpdateBeams(wallH, built, t);

            foreach (var r in rooms.Values)
            {
                var row = r.row;
                float temp = frames != null ? frames.Temp(row.Id, Hour) : row.StartTemp;
                bool cooled = row.Demand > 0;
                Color c = row.Kind == "corridor" ? CascadeStyle.Corridor : row.Kind == "mech" ? CascadeStyle.Mech
                    : CascadeStyle.RoomColor(cooled ? temp : float.NaN, row.StartTemp, row.LimitTemp);
                bool down = cooled && !float.IsNaN(temp) && temp > row.LimitTemp;
                float glow = (highlight.Contains(row.Id) ? 0.12f + 0.08f * Mathf.Sin(t * 4f) : 0f) + (Selected == row.Id ? 0.12f : 0f)
                             + (down ? 0.1f * (0.5f + 0.5f * Mathf.Sin(t * 5f)) : 0f);
                var wantFloor = built ? r.litFloor : CascadeStyle.Flat;
                if (r.floor.sharedMaterial != wantFloor) r.floor.sharedMaterial = wantFloor;
                if (!built) c = CascadeStyle.Blueprint.A(0.12f);
                else
                {
                    // walk-through: real floor finishes, warming toward amber/red as heat spreads
                    float heat = cooled && !float.IsNaN(temp) ? Mathf.Clamp01((temp - row.StartTemp) / Mathf.Max(1f, row.LimitTemp - row.StartTemp)) : 0f;
                    var finish = Color.white; // the texture carries the finish; heat tints it
                    c = down ? Color.Lerp(finish, CascadeStyle.FloorDown, 0.75f) : heat > 0.02f ? Color.Lerp(finish, heat < 0.5f ? CascadeStyle.FloorWarm : CascadeStyle.FloorHot, Mathf.Clamp01(heat * 1.4f)) : finish;
                }
                if (r.ceiling) r.ceiling.SetActive(built && LifeSize && !Xray);
                if (r.ceilR) CascadeStyle.Tint(r.ceilR, AtlasStyle.Hex("#F4F2EE"));
                CascadeStyle.Tint(r.floor, c, Color.white * glow);
                r.name.color = built ? CascadeStyle.Ink : CascadeStyle.Blueprint;
                r.name.gameObject.SetActive(!LifeSize || !built);
                if (r.sign)
                {
                    r.sign.gameObject.SetActive(built && LifeSize);
                    if (LifeSize)
                    {
                        r.sign.text = cooled && !float.IsNaN(temp) ? $"{row.Name}\n<size=70%><color=#{(down ? "DC2626" : "475569")}>{temp:0}°F{(down ? " · DOWN" : "")}</color></size>" : row.Name;
                        r.sign.color = CascadeStyle.Ink;
                        Billboard(r.sign.transform);
                    }
                }
                if (r.temp)
                {
                    r.temp.gameObject.SetActive(built && !LifeSize);
                    r.temp.text = down ? $"<b>{temp:0}°F · DOWN</b>" : float.IsNaN(temp) ? "" : $"{temp:0}°F";
                    r.temp.color = down ? Color.white : CascadeStyle.Ink.A(0.75f);
                }
            }

            foreach (var a in assets.Values)
            {
                float appear = built ? Mathf.Clamp01((buildT - 1.4f) / 0.6f) : 0f;
                if (built && !buildAnimating) appear = 1;
                a.root.localScale = Vector3.one * Mathf.Max(0.001f, Mathf.SmoothStep(0, 1, appear));
                a.root.gameObject.SetActive(appear > 0.001f);
                string state = StateOf(a.row, frames);
                var hc = CascadeStyle.HealthColor(state);
                bool alarm = state is "critical" or "failed";
                float pulse = pulseUntil.TryGetValue(a.row.Id, out var until) && until > t ? 0.5f + 0.5f * Mathf.Sin(t * 8f) : 0f;
                float sel = Selected == a.row.Id || highlight.Contains(a.row.Id) ? 0.25f : 0f;
                CascadeStyle.Tint(a.body, CascadeStyle.AssetBody, (alarm ? hc * (0.25f + 0.25f * Mathf.Sin(t * 6f)) : Color.clear) + Color.white * (sel * 0.6f + pulse * 0.3f));
                CascadeStyle.Tint(a.light, hc, hc * (alarm ? 0.6f + 0.4f * Mathf.Sin(t * 6f) : 0.35f));
                a.label.fontSize = LifeSize ? 2.2f : 3.2f;
                a.label.text = $"{a.row.Name}\n<size=75%><color=#{ColorUtility.ToHtmlStringRGB(hc)}>•</color> {Reading(a.row)} · {CascadeStyle.HealthLabel(state)}</size>";
                Billboard(a.label.transform);
            }

            // x-ray pipes: colored by the health of what feeds them
            foreach (var d in ducts)
            {
                bool show = built && (Xray || highlight.Contains(d.from));
                d.r.gameObject.SetActive(show);
                if (!show) continue;
                var srcState = assets.TryGetValue(d.from, out var src) ? StateOf(src.row, frames) : "ok";
                Color baseC = d.kind == "power" ? CascadeStyle.Warn : d.kind == "chilled_water" ? CascadeStyle.Accent : AtlasStyle.Hex("#A5B4FC");
                Color c = srcState is "failed" or "critical" ? CascadeStyle.Danger : srcState is "starved" or "degraded" or "watch" ? CascadeStyle.Warn : baseC;
                float flow = 0.5f + 0.5f * Mathf.Sin(t * 3f + d.r.transform.localPosition.x);
                CascadeStyle.Tint(d.r, c.A(0.9f), c * 0.25f * flow);
            }

            // evidence thread with a bead running along it
            for (int i = 0; i < thread.Count; i++) CascadeStyle.Tint(thread[i], CascadeStyle.Accent.A(0.95f), CascadeStyle.Accent * (0.3f + 0.2f * Mathf.Sin(t * 4f - i)));
            if (threadBead && threadPts.Count >= 2)
            {
                float u = (t * 0.6f) % 1f * (threadPts.Count - 1);
                int si = Mathf.Min(threadPts.Count - 2, Mathf.FloorToInt(u));
                threadBead.localPosition = Vector3.Lerp(threadPts[si], threadPts[si + 1], u - si);
            }

            // guide trail: chevron wave toward the target
            for (int i = 0; i < trail.Count; i++)
            {
                float w = 0.5f + 0.5f * Mathf.Sin(i * 0.9f - t * 6f);
                CascadeStyle.Tint(trail[i], Color.Lerp(CascadeStyle.Accent, Color.white, w * 0.6f).A(0.95f), CascadeStyle.Accent * (0.2f + 0.5f * w));
            }

            foreach (var (k, p) in people)
            {
                if (!p) continue;
                if (peopleGoal.TryGetValue(k, out var g)) p.localPosition = Vector3.Lerp(p.localPosition, g, Time.deltaTime * 10f); // glide between 10 Hz updates
                Billboard(p.Find("Label"));
            }
        }

        void Billboard(Transform t)
        {
            if (!t || !cam) return;
            var dir = t.position - cam.transform.position;
            dir.y = 0;
            if (dir.sqrMagnitude > 1e-6f) t.rotation = Quaternion.LookRotation(dir, Vector3.up);
        }

        public string StateOf(Net.Asset a, FrameSet frames)
        {
            // timeline in the future: the simulation decides; at "now" the live health does
            string sim = frames?.AssetState(a.Id, Hour);
            if (Hour > 0.25f && sim != null)
            {
                if (sim == "degraded" && a.Health is "critical" or "watch") return a.Health;
                return sim;
            }
            return a.Health;
        }

        public static string Reading(Net.Asset a) => a.ReadingUnit switch
        {
            "mm/s" => $"{a.Reading:0.0} mm/s",
            "%" => $"{a.Reading:0}% {a.ReadingLabel}",
            _ => $"{a.Reading:0}{a.ReadingUnit} {a.ReadingLabel}",
        };

        void UpdatePeople()
        {
            var me = net.Me;
            var mine = TrailFrom?.Invoke();
            var present = net.Db.User.Iter().Where(u => u.Online && u.Present && (!me.HasValue || !u.Identity.Equals(me.Value))).ToList();
            var keep = new HashSet<string>();
            foreach (var u in present)
            {
                var key = u.Identity.ToString();
                // someone standing where you stand would sit inside your head: skip
                if (LifeSize && mine.HasValue && Vector2.Distance(mine.Value, new Vector2(u.X, u.Z)) < 1.2f) continue;
                keep.Add(key);
                if (!people.TryGetValue(key, out var tr) || !tr)
                {
                    // a ring on the floor + a small name tag (no body in your way)
                    tr = new GameObject("Person").transform;
                    tr.SetParent(peopleRoot, false);
                    var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    Destroy(ring.GetComponent<Collider>());
                    ring.name = "Ring";
                    ring.transform.SetParent(tr, false);
                    ring.transform.localScale = new Vector3(0.6f, 0.01f, 0.6f);
                    ring.GetComponent<Renderer>().sharedMaterial = CascadeStyle.Glow;
                    var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    Destroy(dot.GetComponent<Collider>());
                    dot.name = "Dot";
                    dot.transform.SetParent(tr, false);
                    dot.GetComponent<Renderer>().sharedMaterial = CascadeStyle.Glow;
                    var lbl = MakeLabel(tr, "", 1.4f, CascadeStyle.Ink, Vector3.zero, new Vector2(6, 0.8f), false, true);
                    lbl.name = "Label";
                    people[key] = tr;
                }
                if (!peopleGoal.ContainsKey(key)) tr.localPosition = L(u.X, u.Z, 0.02f);
                peopleGoal[key] = L(u.X, u.Z, 0.02f);
                var dotT = tr.Find("Dot");
                dotT.localPosition = new Vector3(0, LifeSize ? 1.75f : 1.2f, 0);
                dotT.localScale = Vector3.one * (LifeSize ? 0.12f : 0.6f);
                tr.Find("Label").localPosition = new Vector3(0, LifeSize ? 1.95f : 2.1f, 0);
                tr.Find("Label").localScale = Vector3.one * (LifeSize ? 1f : 2.5f);
                CascadeStyle.Tint(tr.Find("Ring").GetComponent<Renderer>(), CascadeStyle.Accent.A(0.85f), CascadeStyle.Accent * 0.3f);
                CascadeStyle.Tint(dotT.GetComponent<Renderer>(), CascadeStyle.Accent, CascadeStyle.Accent * 0.4f);
                tr.Find("Label").GetComponent<TextMeshPro>().text = $"{u.Name} · {(u.Role == "technician" ? "Technician" : "Manager")}";
            }
            foreach (var k in people.Keys.ToList()) if (!keep.Contains(k)) { if (people[k]) Destroy(people[k].gameObject); people.Remove(k); peopleGoal.Remove(k); }
        }

        public void OnPick(string id, bool isAsset)
        {
            Selected = Selected == id ? null : id;
            Picked?.Invoke(Selected, isAsset);
        }

        /// Pointer target on rooms and assets (mouse, hand pinch or controller ray).
        static ProcTex.Kind FloorTexture(Net.Room r) => r.Kind switch
        {
            "office" => ProcTex.Kind.Carpet,
            "server" => ProcTex.Kind.LargeTile,
            "mech" => ProcTex.Kind.Concrete,
            "corridor" => ProcTex.Kind.Tile,
            _ => r.Id == "LOBBY" ? ProcTex.Kind.Marble : ProcTex.Kind.Wood,
        };

        // ---------------- structure ----------------

        public static Color StructureColor(Net.Wall w, float t)
        {
            if (w.Health == "damaged") return Color.Lerp(AtlasStyle.Hex("#EF4444"), AtlasStyle.Hex("#7F1D1D"), 0.5f + 0.5f * Mathf.Sin(t * 4f));
            if (w.Kind == "partition") return AtlasStyle.Hex("#E5E7EB").A(0.45f);
            if (w.Kind == "exterior") return AtlasStyle.Hex("#64748B");
            return w.Utilization < 0.5f ? AtlasStyle.Hex("#3B82F6") : w.Utilization < 0.68f ? AtlasStyle.Hex("#F59E0B") : AtlasStyle.Hex("#EF4444");
        }

        public static string WallKindLabel(Net.Wall w) => w.Kind switch { "exterior" => "Exterior · load-bearing", "bearing" => "Load-bearing", _ => "Partition (non-structural)" };

        public Net.Wall WallRow(string id) => net?.Db?.Wall.Id.Find(id);

        /// Midpoint and a unit normal of a wall, in plan meters.
        public (Vector2 mid, Vector2 normal) WallPlan(string id)
        {
            var w = WallRow(id);
            if (w == null) return (center, Vector2.up);
            var mid = new Vector2((w.X0 + w.X1) / 2, (w.Z0 + w.Z1) / 2);
            var dir = new Vector2(w.X1 - w.X0, w.Z1 - w.Z0).normalized;
            return (mid, new Vector2(-dir.y, dir.x));
        }

        public Vector3 WallWorld(string id, float y)
        {
            var (mid, _) = WallPlan(id);
            return transform.TransformPoint(L(mid.x, mid.y, y));
        }

        readonly Dictionary<string, Transform> beams = new();

        /// Proposed load-bearing removals show the replacement: a steel beam on two posts.
        void UpdateBeams(float wallH, bool built, float t)
        {
            if (net?.Db == null || !staticRoot) return;
            foreach (var w in net.Db.Wall.Iter())
            {
                bool show = built && w.Proposal == "remove" && w.Kind != "partition" && w.ProposalState is "evaluating" or "recommended" or "approved";
                beams.TryGetValue(w.Id, out var root);
                if (!show) { if (root) root.gameObject.SetActive(false); continue; }
                if (!root)
                {
                    root = new GameObject("Beam " + w.Id).transform;
                    root.SetParent(staticRoot, false);
                    var a = L(w.X0, w.Z0); var b = L(w.X1, w.Z1);
                    var beam = CascadeStyle.Cube(root, "Beam", (a + b) / 2, new Vector3(0.22f, 0.36f, Vector3.Distance(a, b)), CascadeStyle.Solid);
                    beam.transform.localRotation = Quaternion.LookRotation(b - a, Vector3.up);
                    var dir = (b - a).normalized;
                    CascadeStyle.Cube(root, "Post A", a + dir * 0.3f, new Vector3(0.2f, 1, 0.2f), CascadeStyle.Solid);
                    CascadeStyle.Cube(root, "Post B", b - dir * 0.3f, new Vector3(0.2f, 1, 0.2f), CascadeStyle.Solid);
                    beams[w.Id] = root;
                }
                root.gameObject.SetActive(true);
                var steel = w.ProposalState == "approved" ? AtlasStyle.Hex("#F59E0B") : AtlasStyle.Hex("#F59E0B").A(0.55f + 0.25f * Mathf.Sin(t * 3f));
                foreach (Transform part in root)
                {
                    var p = part.localPosition;
                    if (part.name == "Beam") { p.y = wallH - 0.18f; }
                    else { var sc = part.localScale; sc.y = wallH - 0.36f; part.localScale = sc; p.y = sc.y / 2; }
                    part.localPosition = p;
                    CascadeStyle.Tint(part.GetComponent<Renderer>(), steel, AtlasStyle.Hex("#F59E0B") * 0.25f);
                }
            }
        }

        public void OnWallPick(string id)
        {
            Selected = Selected == id ? null : id;
            WallPicked?.Invoke(Selected);
        }

        public class WallPick : MonoBehaviour, IPointerClickHandler
        {
            TwinView view; string id;
            public void Init(TwinView v, string i) { view = v; id = i; }
            public void OnPointerClick(PointerEventData e) => view.OnWallPick(id);
        }

        public class Pick : MonoBehaviour, IPointerClickHandler
        {
            TwinView view; string id; bool asset;
            public void Init(TwinView v, string i, bool a) { view = v; id = i; asset = a; }
            public void OnPointerClick(PointerEventData e) => view.OnPick(id, asset);
        }
    }
}

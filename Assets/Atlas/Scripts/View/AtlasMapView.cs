using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Atlas
{
    /// <summary>
    /// Renders the architecture graph on the table and applies scene commands.
    /// It only reacts to backend rows; the agent changes the map exclusively
    /// through scene_command JSON, exactly as the spec describes.
    /// </summary>
    public class AtlasMapView : MonoBehaviour
    {
        public IAtlasBackend Backend { get; private set; }
        public string Selected { get; private set; }
        public string Layer { get; private set; } = "code";
        public IReadOnlyDictionary<string, NodeBlock> Blocks => blocks;

        public event Action<string> SelectionChanged;
        public event Action<SceneCommand> CommandApplied;

        const float PullThreshold = 0.07f;
        const float FocusScale = 1.35f;

        readonly Dictionary<string, NodeBlock> blocks = new();
        readonly List<EdgeLine> edges = new();
        readonly HashSet<string> pulled = new();

        // highlight state, rebuilt by each scene command
        readonly Dictionary<string, Color> rims = new();
        readonly Dictionary<string, Color> fills = new();
        readonly Dictionary<string, string> tags = new();
        readonly Dictionary<string, Color> litEdges = new();
        bool dimOthers;

        Transform zoomRoot;
        Vector3 zoomTargetPos;
        float zoomTargetScale = 1f;

        // flow animation
        readonly List<Transform> pulses = new();
        string[] flowPath;
        float flowClock;

        // plates
        Transform basePlate;
        readonly Dictionary<string, (Transform plate, TextMeshPro label)> featurePlates = new();
        MaterialPropertyBlock mpb;

        public void Bind(IAtlasBackend backend)
        {
            Backend = backend;
            mpb = new MaterialPropertyBlock();
            zoomRoot = new GameObject("Zoom").transform;
            zoomRoot.SetParent(transform, false);
            basePlate = Plate("BasePlate");

            backend.NodeInserted += AddNode;
            backend.EdgeInserted += AddEdge;
            backend.PoseUpdated += OnPose;
            backend.ParticipantUpdated += OnParticipant;
            backend.SceneCommandInserted += Apply;
        }

        // ---------------- table rows ----------------

        void AddNode(ArchNode n)
        {
            var b = new GameObject().AddComponent<NodeBlock>();
            b.transform.SetParent(zoomRoot, false);
            b.Init(n, this);
            blocks[n.Id] = b;
            Relayout(true);
        }

        void AddEdge(ArchEdge e)
        {
            if (!blocks.TryGetValue(e.From, out var a) || !blocks.TryGetValue(e.To, out var b)) return;
            var line = new GameObject().AddComponent<EdgeLine>();
            line.transform.SetParent(zoomRoot, false);
            line.Init(e, a, b, zoomRoot);
            edges.Add(line);
            RefreshVisuals();
        }

        void OnPose(NodePose p)
        {
            if (!blocks.TryGetValue(p.NodeId, out var b)) return;
            b.Offset = p.Offset;

            // "Pull a piece out to see its blast radius" (spec, hand interactions)
            bool mine = p.HeldBy == Backend.LocalIdentity;
            if (mine && p.Held && p.Offset.magnitude > PullThreshold && pulled.Add(p.NodeId))
                Backend.ShowBlastRadius(p.NodeId);
        }

        void OnParticipant(Participant p)
        {
            if (p.Identity != Backend.LocalIdentity) return;
            if (Selected == p.SelectedNode) return;
            Selected = p.SelectedNode;
            RefreshVisuals();
            SelectionChanged?.Invoke(Selected);
        }

        // ---------------- input from blocks ----------------

        public void OnBlockClicked(NodeBlock b) => Backend.SelectNode(Selected == b.Data.Id ? null : b.Data.Id);

        public void OnBlockReleased(NodeBlock b)
        {
            Backend.MoveNode(b.Data.Id, Vector2.zero, false); // spring home
            if (pulled.Remove(b.Data.Id)) Backend.ClearScene();
        }

        /// Pointer ray -> point on the table plane, in map space.
        public bool TryTablePoint(PointerEventData e, out Vector3 local)
        {
            local = default;
            Ray ray;
            if (e is UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceEventData td && td.rayPoints != null && td.rayPoints.Count >= 2)
            {
                // hand / controller ray on Quest
                ray = new Ray(td.rayPoints[0], (td.rayPoints[1] - td.rayPoints[0]).normalized);
            }
            else
            {
                var cam = e.pressEventCamera ? e.pressEventCamera : Camera.main;
                if (!cam) return false;
                ray = cam.ScreenPointToRay(e.position);
            }
            var plane = new Plane(zoomRoot.up, zoomRoot.position);
            if (!plane.Raycast(ray, out float d)) return false;
            local = zoomRoot.InverseTransformPoint(ray.GetPoint(d));
            local.y = 0;
            return true;
        }

        // ---------------- scene commands ----------------

        void Apply(string json)
        {
            SceneCommand c;
            try { c = SceneCommand.Parse(json); }
            catch (Exception ex) { Debug.LogWarning($"[Atlas] Bad scene command: {ex.Message}"); return; }
            if (c == null || string.IsNullOrEmpty(c.kind)) return;

            var nodes = (c.nodes ?? Array.Empty<string>()).Where(blocks.ContainsKey).ToArray();
            switch (c.kind)
            {
                case "highlight_nodes":
                    ResetHighlights();
                    var col = !string.IsNullOrEmpty(c.color) ? AtlasStyle.Hex(c.color) : AtlasStyle.Accent;
                    foreach (var n in nodes) rims[n] = col;
                    dimOthers = true;
                    break;

                case "highlight_path":
                    ResetHighlights();
                    LightPath(nodes, AtlasStyle.Accent);
                    dimOthers = true;
                    break;

                case "animate_flow":
                    LightPath(nodes, AtlasStyle.Accent);
                    dimOthers = true;
                    StartFlow(nodes);
                    break;

                case "show_blast_radius":
                    ResetHighlights();
                    if (blocks.ContainsKey(c.origin ?? ""))
                    {
                        rims[c.origin] = AtlasStyle.Danger;
                        tags[c.origin] = "Change origin";
                    }
                    var level = new Dictionary<string, int>();
                    for (int i = 0; i < nodes.Length; i++)
                    {
                        int lv = c.levels != null && i < c.levels.Length ? c.levels[i] : 1;
                        level[nodes[i]] = lv;
                        var rc = AtlasStyle.RiskColor(lv);
                        rims[nodes[i]] = rc;
                        fills[nodes[i]] = Color.Lerp(AtlasStyle.Fill(blocks[nodes[i]].Data.Status), rc, 0.35f);
                        tags[nodes[i]] = lv == 1 ? "Impacted · direct dependency" : $"Impacted · {lv} hops away";
                    }
                    foreach (var e in edges)
                    {
                        bool aIn = level.ContainsKey(e.Data.From) || e.Data.From == c.origin;
                        bool bIn = level.ContainsKey(e.Data.To) || e.Data.To == c.origin;
                        if (aIn && bIn)
                            litEdges[Key(e.Data.From, e.Data.To)] = AtlasStyle.RiskColor(level.TryGetValue(e.Data.From, out var l) ? l : 1);
                    }
                    dimOthers = true;
                    break;

                case "set_layer":
                    Layer = c.layer == "business" ? "business" : "code";
                    Relayout(false);
                    break;

                case "focus_node":
                    ResetHighlights();
                    if (blocks.ContainsKey(c.node ?? ""))
                    {
                        rims[c.node] = AtlasStyle.Accent;
                        dimOthers = true;
                        Focus(c.node);
                    }
                    break;

                case "clear":
                    ResetHighlights();
                    if (Layer != "code") { Layer = "code"; Relayout(false); }
                    break;

                default:
                    Debug.LogWarning($"[Atlas] Unknown scene command '{c.kind}'");
                    return;
            }
            RefreshVisuals();
            CommandApplied?.Invoke(c);
        }

        void LightPath(string[] nodes, Color col)
        {
            foreach (var n in nodes) rims[n] = col;
            for (int i = 0; i + 1 < nodes.Length; i++)
            {
                litEdges[Key(nodes[i], nodes[i + 1])] = col;
                litEdges[Key(nodes[i + 1], nodes[i])] = col;
            }
        }

        void ResetHighlights()
        {
            rims.Clear(); fills.Clear(); tags.Clear(); litEdges.Clear();
            dimOthers = false;
            StopFlow();
            Focus(null);
        }

        static string Key(string a, string b) => a + ">" + b;

        void RefreshVisuals()
        {
            foreach (var (id, b) in blocks)
            {
                bool sel = id == Selected;
                bool lit = rims.TryGetValue(id, out var rim);
                b.TargetFill = fills.TryGetValue(id, out var f) ? f : AtlasStyle.Fill(b.Data.Status);
                b.TargetRim = lit ? rim : null;
                b.TargetDim = dimOthers && !lit && !sel;
                b.TagOverride = tags.TryGetValue(id, out var t) ? t : null;
                b.TargetLift = lit ? 0.008f : 0f;
                if (sel)
                {
                    if (!fills.ContainsKey(id)) b.TargetFill = AtlasStyle.FillSelected;
                    b.TargetRim ??= AtlasStyle.Accent;
                    b.TargetLift = 0.016f;
                }
            }

            bool business = Layer == "business";
            foreach (var e in edges)
            {
                if (litEdges.TryGetValue(Key(e.Data.From, e.Data.To), out var c))
                {
                    e.TargetColor = c; e.TargetWidth = 0.0065f; e.TargetLabelAlpha = 0.9f;
                }
                else
                {
                    float a = dimOthers ? 0.08f : business ? 0.14f : 0.38f;
                    e.TargetColor = Color.white.A(a); e.TargetWidth = 0.0035f;
                    e.TargetLabelAlpha = business || dimOthers ? 0f : 0.55f;
                }
            }
        }

        // ---------------- layout ----------------

        void Relayout(bool snap)
        {
            var features = blocks.Values.Select(b => b.Data.Feature).Distinct().ToList();
            const float colStep = 0.21f, rowStep = 0.135f;

            foreach (var b in blocks.Values)
            {
                Vector3 pos;
                if (Layer == "business")
                {
                    int col = features.IndexOf(b.Data.Feature);
                    var members = blocks.Values.Where(x => x.Data.Feature == b.Data.Feature).ToList();
                    int row = members.IndexOf(b);
                    pos = new Vector3((col - (features.Count - 1) / 2f) * colStep, 0, 0.135f - row * rowStep);
                }
                else pos = new Vector3(b.Data.CodePos.x, 0, b.Data.CodePos.y);

                b.HomePos = pos;
                if (snap) b.transform.localPosition = pos;
            }

            // base plate under the code layout; per-feature plates in business layout
            var all = Bounds(blocks.Values);
            SetPlate(basePlate, all, Layer == "code" ? 1f : 0f);

            foreach (var feat in features)
            {
                if (!featurePlates.TryGetValue(feat, out var fp))
                {
                    var p = Plate("Feature_" + feat);
                    var lg = new GameObject("Label");
                    lg.transform.SetParent(p, false);
                    var t = lg.AddComponent<TextMeshPro>();
                    t.text = feat.ToUpperInvariant();
                    t.fontSize = 5.5f;
                    t.fontStyle = FontStyles.Bold;
                    t.characterSpacing = 6f;
                    t.alignment = TextAlignmentOptions.Center;
                    t.textWrappingMode = TextWrappingModes.NoWrap;
                    t.rectTransform.sizeDelta = new Vector2(19f, 1.4f);
                    fp = (p, t);
                    featurePlates[feat] = fp;
                }
                var members = blocks.Values.Where(x => x.Data.Feature == feat).ToList();
                var bounds = BoundsAt(members.Select(m => m.HomePos));
                SetPlate(fp.plate, bounds, Layer == "business" ? 1f : 0f);
                fp.label.transform.SetParent(zoomRoot, false);
                fp.label.transform.localRotation = Quaternion.Euler(90, 0, 0);
                fp.label.transform.localScale = Vector3.one * 0.01f;
                fp.label.transform.localPosition = new Vector3(bounds.center.x, 0.002f, bounds.max.z + 0.028f);
            }
            RefreshVisuals();
        }

        static Bounds Bounds(IEnumerable<NodeBlock> bs) => BoundsAt(bs.Select(b => b.HomePos));

        static Bounds BoundsAt(IEnumerable<Vector3> centers)
        {
            var list = centers.ToList();
            if (list.Count == 0) return new Bounds();
            var b = new Bounds(list[0], Vector3.zero);
            foreach (var c in list) b.Encapsulate(c);
            b.Expand(new Vector3(NodeBlock.W + 0.05f, 0, NodeBlock.D + 0.05f));
            return b;
        }

        // plate visibility is animated in Update via a target alpha stored per plate
        readonly Dictionary<Transform, (Vector3 pos, Vector3 scale, float alpha, float cur)> plateTargets = new();

        Transform Plate(string n)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = n;
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(zoomRoot, false);
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = AtlasStyle.PlateMat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go.transform;
        }

        void SetPlate(Transform p, Bounds b, float alpha)
        {
            var pos = new Vector3(b.center.x, -0.001f, b.center.z);
            var scale = new Vector3(b.size.x, 0.002f, b.size.z);
            float cur = plateTargets.TryGetValue(p, out var prev) ? prev.cur : 0f;
            plateTargets[p] = (pos, scale, alpha, cur);
            if (cur <= 0.01f) { p.localPosition = pos; p.localScale = scale; }
        }

        void Focus(string id)
        {
            if (id != null && blocks.TryGetValue(id, out var b))
            {
                zoomTargetScale = FocusScale;
                zoomTargetPos = new Vector3(-b.HomePos.x, 0, -b.HomePos.z) * FocusScale;
            }
            else
            {
                zoomTargetScale = 1f;
                zoomTargetPos = Vector3.zero;
            }
        }

        // ---------------- flow pulses ----------------

        void StartFlow(string[] path)
        {
            StopFlow();
            if (path.Length < 2) return;
            flowPath = path;
            flowClock = 0f;
            for (int i = 0; i < 6; i++)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = "Pulse";
                Destroy(go.GetComponent<Collider>());
                go.transform.SetParent(zoomRoot, false);
                go.transform.localScale = Vector3.one * 0.014f;
                var r = go.GetComponent<MeshRenderer>();
                r.sharedMaterial = AtlasStyle.PulseMat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mpb.SetColor("_Color", AtlasStyle.FillSelected);
                mpb.SetColor("_Emission", AtlasStyle.Accent * 0.4f);
                r.SetPropertyBlock(mpb);
                pulses.Add(go.transform);
            }
        }

        void StopFlow()
        {
            foreach (var p in pulses) if (p) Destroy(p.gameObject);
            pulses.Clear();
            flowPath = null;
        }

        void Update()
        {
            float k = 1f - Mathf.Exp(-Time.deltaTime * 6f);
            zoomRoot.localScale = Vector3.one * Mathf.Lerp(zoomRoot.localScale.x, zoomTargetScale, k);
            zoomRoot.localPosition = Vector3.Lerp(zoomRoot.localPosition, zoomTargetPos, k);

            foreach (var p in plateTargets.Keys.ToList())
            {
                var t = plateTargets[p];
                t.cur = Mathf.Lerp(t.cur, t.alpha, k);
                plateTargets[p] = t;
                p.localPosition = Vector3.Lerp(p.localPosition, t.pos, k);
                p.localScale = Vector3.Lerp(p.localScale, t.scale, k);
                bool isBase = p == basePlate;
                var c = isBase ? new Color(0.10f, 0.11f, 0.17f, 0.55f * t.cur) : new Color(0.55f, 0.47f, 0.98f, 0.16f * t.cur);
                mpb.SetColor("_Color", c);
                mpb.SetColor("_Emission", Color.clear);
                p.GetComponent<MeshRenderer>().SetPropertyBlock(mpb);
                p.gameObject.SetActive(t.cur > 0.01f);
            }
            foreach (var (feat, fp) in featurePlates)
                fp.label.color = Color.white.A(0.75f * (plateTargets.TryGetValue(fp.plate, out var pt) ? pt.cur : 0f));

            AnimateFlow();
        }

        void AnimateFlow()
        {
            if (flowPath == null) return;
            var pts = flowPath.Select(id => blocks[id].transform.localPosition).Select(p => new Vector3(p.x, 0.012f, p.z)).ToList();
            float total = 0f;
            for (int i = 0; i + 1 < pts.Count; i++) total += Vector3.Distance(pts[i], pts[i + 1]);
            if (total <= 0f) return;

            flowClock += Time.deltaTime * 0.16f;
            for (int i = 0; i < pulses.Count; i++)
            {
                float s = Mathf.Repeat(flowClock + i * total / pulses.Count, total);
                int seg = 0;
                while (seg + 1 < pts.Count - 1 && s > Vector3.Distance(pts[seg], pts[seg + 1]))
                {
                    s -= Vector3.Distance(pts[seg], pts[seg + 1]);
                    seg++;
                }
                float len = Vector3.Distance(pts[seg], pts[seg + 1]);
                pulses[i].localPosition = Vector3.Lerp(pts[seg], pts[seg + 1], len > 0 ? Mathf.Clamp01(s / len) : 0);
                pulses[i].localScale = Vector3.one * (0.012f + 0.004f * Mathf.Sin(Time.time * 6f + i));
            }
        }
    }
}

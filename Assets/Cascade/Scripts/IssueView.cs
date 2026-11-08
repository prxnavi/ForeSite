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
    /// Inspection findings drawn where they physically are in the building: cracks in walls
    /// and slabs, a water stain dripping from a ceiling, a corroded pipe joint, alarm rings
    /// on failing equipment. Each has a pin you can tap. Rows come from the `issue` table.
    /// Lives under the twin, so it works life-size and on the tabletop.
    /// </summary>
    public class IssueView : MonoBehaviour
    {
        public event Action<string> Picked;
        public string Selected;

        CascadeNet net; TwinView twin; Camera cam;
        string signature = "";
        Transform root;

        class Vis { public Net.Issue row; public Transform pin; public Renderer pinR, ring; public TextMeshPro label; public List<Transform> drips = new(); public float seed; }
        readonly Dictionary<string, Vis> vis = new();

        public void Bind(CascadeNet n, TwinView t, Camera c) { net = n; twin = t; cam = c; }

        public static Color Severity(string s) => s switch
        {
            "critical" => AtlasStyle.Hex("#DC2626"),
            "high" => AtlasStyle.Hex("#EA580C"),
            "medium" => AtlasStyle.Hex("#D97706"),
            _ => AtlasStyle.Hex("#0284C7"),
        };

        public static string KindLabel(string k) => k switch
        {
            "structural" => "Structural", "water" => "Water leak", "mechanical" => "Mechanical", "electrical" => "Electrical", _ => k,
        };

        public Net.Issue Row(string id) => vis.TryGetValue(id, out var v) ? v.row : null;

        public Vector3 WorldOf(string id)
        {
            if (!vis.TryGetValue(id, out var v) || !v.pin) return twin.transform.position;
            return v.pin.position;
        }

        void Update()
        {
            if (net == null || !net.Ready || twin.Size.x <= 0) return;
            var rows = net.Db.Issue.Iter().ToList();
            var sig = string.Join(",", rows.Select(r => r.Id + r.Status)) + twin.Size;
            if (sig != signature && twin.Has("MECH")) { signature = sig; Rebuild(rows); }
            if (!root) return;

            bool built = net.Timeline?.Built ?? false;
            root.gameObject.SetActive(built);
            if (!built) return;
            float t = Time.time;
            foreach (var v in vis.Values)
            {
                var col = v.row.Status == "open" ? Severity(v.row.Severity) : CUi.Good;
                float pulse = 0.5f + 0.5f * Mathf.Sin(t * (v.row.Severity is "critical" or "high" ? 4f : 2f) + v.seed);
                bool sel = Selected == v.row.Id;
                CascadeStyle.Tint(v.pinR, col, col * (0.25f + 0.35f * pulse + (sel ? 0.4f : 0f)));
                v.pin.localScale = Vector3.one * ((twin.LifeSize ? 0.13f : 0.3f) * (sel ? 1.3f : 1f) * (1f + 0.08f * pulse));
                if (v.ring)
                {
                    float s = 1f + 0.25f * pulse;
                    v.ring.transform.localScale = new Vector3(baseRing(v) * s, 0.01f, baseRing(v) * s);
                    CascadeStyle.Tint(v.ring, col.A(0.55f + 0.35f * (1 - pulse)), col * 0.3f);
                }
                // drips fall from leaks
                for (int i = 0; i < v.drips.Count; i++)
                {
                    var d = v.drips[i];
                    float top = v.row.Y - 0.05f;
                    float phase = Mathf.Repeat(t * 0.7f + i * 0.37f + v.seed, 1f);
                    var p = d.localPosition; p.y = Mathf.Lerp(top, 0.08f, phase * phase); d.localPosition = p;
                }
                // labels face you
                if (cam)
                {
                    var dir = v.label.transform.position - cam.transform.position; dir.y = 0;
                    if (dir.sqrMagnitude > 1e-6f) v.label.transform.rotation = Quaternion.LookRotation(dir);
                }
                v.label.text = $"<b>{v.row.Title}</b>\n<size=70%><color=#{CUi.Hex(col)}>{KindLabel(v.row.Kind).ToUpperInvariant()} · {(v.row.Status == "open" ? v.row.Severity.ToUpperInvariant() : "REPAIR SCHEDULED")}</color></size>";
            }
        }

        static float baseRing(Vis v) => v.row.Surface == "equipment" ? 2.2f : 1.2f;

        void Rebuild(List<Net.Issue> rows)
        {
            if (root) Destroy(root.gameObject);
            vis.Clear();
            root = new GameObject("Issues").transform;
            root.SetParent(twin.transform, false);
            foreach (var r in rows)
            {
                var v = new Vis { row = r, seed = r.Id.GetHashCode() % 100 / 17f };
                var at = twin.L(r.X, r.Z);
                var n = new Vector3(r.Nx, 0, r.Nz);
                var rnd = new System.Random(r.Id.GetHashCode());
                Color dark = AtlasStyle.Hex("#3F3F46");

                switch (r.Surface)
                {
                    case "wall":
                    {
                        // jagged crack on the wall face with a couple of branches
                        var face = at + n * 0.01f;
                        var along = Vector3.Cross(Vector3.up, n).normalized;
                        Crack(face, along, Vector3.up, r.Y - 0.6f, 2.0f, rnd, dark, 0.03f, true);
                        Crack(face + along * 0.5f, along, Vector3.up, r.Y - 0.2f, 1.1f, rnd, dark, 0.02f, true);
                        // and where it starts: the pad on the floor
                        Crack(at + n * 0.9f + Vector3.up * 0.075f, n, along, 0f, 1.2f, rnd, dark, 0.03f, false);
                        break;
                    }
                    case "floor":
                        Crack(at + Vector3.up * 0.075f, Vector3.right, Vector3.forward, -1.6f, 3.2f, rnd, dark, 0.035f, false);
                        break;
                    case "ceiling":
                    {
                        Color stain = AtlasStyle.Hex("#8B6B3E");
                        for (int i = 0; i < 3; i++)
                        {
                            var disc = Prim(PrimitiveType.Cylinder, at + new Vector3((float)rnd.NextDouble() * 0.6f - 0.3f, r.Y - 0.01f * i, (float)rnd.NextDouble() * 0.6f - 0.3f),
                                new Vector3(0.9f + i * 0.35f, 0.006f, 0.8f + i * 0.3f), CascadeStyle.Glow);
                            CascadeStyle.Tint(disc.GetComponent<Renderer>(), stain.A(0.75f - i * 0.18f));
                        }
                        var puddle = Prim(PrimitiveType.Cylinder, at + Vector3.up * 0.07f, new Vector3(0.7f, 0.004f, 0.55f), CascadeStyle.Glow);
                        CascadeStyle.Tint(puddle.GetComponent<Renderer>(), AtlasStyle.Hex("#7DD3FC").A(0.55f));
                        for (int i = 0; i < 3; i++) v.drips.Add(Drip(at + new Vector3(i * 0.18f - 0.18f, r.Y, 0)));
                        break;
                    }
                    case "pipe":
                    {
                        var along = Vector3.Cross(Vector3.up, n).normalized;
                        var pipe = Prim(PrimitiveType.Cylinder, at + Vector3.up * r.Y, new Vector3(0.16f, 3f, 0.16f), CascadeStyle.Solid);
                        pipe.transform.localRotation = Quaternion.FromToRotation(Vector3.up, along);
                        CascadeStyle.Tint(pipe.GetComponent<Renderer>(), AtlasStyle.Hex("#94A3B8"));
                        var flange = Prim(PrimitiveType.Cylinder, at + Vector3.up * r.Y, new Vector3(0.3f, 0.08f, 0.3f), CascadeStyle.Solid);
                        flange.transform.localRotation = Quaternion.FromToRotation(Vector3.up, along);
                        CascadeStyle.Tint(flange.GetComponent<Renderer>(), AtlasStyle.Hex("#B45309"), AtlasStyle.Hex("#B45309") * 0.15f);
                        for (int i = 0; i < 2; i++) v.drips.Add(Drip(at + along * (i * 0.1f)));
                        break;
                    }
                    case "equipment":
                    {
                        var ring = Prim(PrimitiveType.Cylinder, at + Vector3.up * 0.075f, new Vector3(2.2f, 0.01f, 2.2f), CascadeStyle.Glow);
                        v.ring = ring.GetComponent<Renderer>();
                        break;
                    }
                }

                // pin: a glowing marker with a "!" and a label, offset so it doesn't sit inside things
                Vector3 pinAt = r.Surface switch
                {
                    "wall" => at + n * 0.45f + Vector3.up * 1.95f,
                    "ceiling" => at + Vector3.up * 2.15f,
                    "pipe" => at + Vector3.up * (r.Y - 0.45f),
                    "equipment" => at + new Vector3(n.x, 0, n.z) * 0.9f + Vector3.up * 2.35f,
                    _ => at + Vector3.up * 1.3f,
                };
                var pin = Prim(PrimitiveType.Sphere, pinAt, Vector3.one * 0.24f, CascadeStyle.Glow);
                var col = pin.AddComponent<SphereCollider>(); col.radius = 1.2f;
                pin.AddComponent<Pick>().Init(this, r.Id);
                v.pin = pin.transform; v.pinR = pin.GetComponent<Renderer>();
                var bang = TwinView.MakeLabel(pin.transform, "!", 3f, Color.white, Vector3.zero, new Vector2(1, 1), false, true);
                bang.transform.localScale = Vector3.one;
                v.label = TwinView.MakeLabel(root, "", 1.1f, CascadeStyle.Ink, pinAt + Vector3.up * 0.42f, new Vector2(4.2f, 0.9f), false, false);
                v.label.outlineWidth = 0.25f; v.label.outlineColor = new Color32(255, 255, 255, 230);
                vis[r.Id] = v;
            }
        }

        GameObject Prim(PrimitiveType type, Vector3 pos, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(type);
            var c = go.GetComponent<Collider>(); if (c) Destroy(c);
            go.transform.SetParent(root, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            return go;
        }

        Transform Drip(Vector3 at)
        {
            var d = Prim(PrimitiveType.Sphere, at, Vector3.one * 0.045f, CascadeStyle.Glow);
            CascadeStyle.Tint(d.GetComponent<Renderer>(), AtlasStyle.Hex("#38BDF8").A(0.9f), AtlasStyle.Hex("#38BDF8") * 0.3f);
            return d.transform;
        }

        /// A jagged crack as connected thin segments, plus small branches.
        void Crack(Vector3 origin, Vector3 dirA, Vector3 dirB, float startB, float length, System.Random rnd, Color color, float width, bool vertical)
        {
            var pts = new List<Vector3>();
            int n = 9;
            float side = 0;
            for (int i = 0; i <= n; i++)
            {
                float u = i / (float)n;
                side += ((float)rnd.NextDouble() - 0.5f) * 0.22f;
                side = Mathf.Clamp(side, -0.35f, 0.35f);
                var p = vertical ? origin + dirA * side + dirB * (startB + u * length) : origin + dirA * (startB + u * length) + dirB * side;
                pts.Add(p);
            }
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                Seg(pts[i], pts[i + 1], width * (1f - i / (float)n * 0.6f), color);
                if (i % 3 == 1)
                {
                    var off = (vertical ? dirA : dirB) * (((float)rnd.NextDouble() > 0.5f ? 1 : -1) * (0.15f + (float)rnd.NextDouble() * 0.2f));
                    var tip = pts[i] + off + (vertical ? dirB : dirA) * 0.12f;
                    Seg(pts[i], tip, width * 0.5f, color);
                }
            }
        }

        void Seg(Vector3 a, Vector3 b, float w, Color c)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(root, false);
            go.transform.localPosition = (a + b) / 2;
            go.transform.localScale = new Vector3(w, w * 0.6f, Vector3.Distance(a, b) + w * 0.5f);
            if ((b - a).sqrMagnitude > 1e-6f) go.transform.localRotation = Quaternion.LookRotation(b - a, Mathf.Abs(Vector3.Dot((b - a).normalized, Vector3.up)) > 0.9f ? Vector3.forward : Vector3.up);
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = CascadeStyle.Glow;
            CascadeStyle.Tint(r, c);
        }

        public class Pick : MonoBehaviour, IPointerClickHandler
        {
            IssueView view; string id;
            public void Init(IssueView v, string i) { view = v; id = i; }
            public void OnPointerClick(PointerEventData e) { view.Selected = view.Selected == id ? null : id; view.Picked?.Invoke(view.Selected); }
        }
    }
}

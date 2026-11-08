using System.Collections.Generic;
using System.Linq;
using Atlas;
using TMPro;
using UnityEngine;

namespace Cascade
{
    /// <summary>
    /// Multiplayer layer. Pings arrive on a SpacetimeDB event table (broadcast, never stored) and
    /// become a light beacon everyone sees, with a spoken "Name flagged X". Field notes are pinned
    /// where they were made; private ones are only ever sent to their author (row-level security).
    /// </summary>
    public class CollabView : MonoBehaviour
    {
        CascadeNet net; TwinView twin; Camera cam;
        Transform root;
        class Beacon { public Transform t; public Renderer beam, ring; public float born; public Color color; }
        readonly List<Beacon> beacons = new();
        readonly Dictionary<ulong, TextMeshPro> notes = new();
        int seenVersion = -1;
        public string LastPing { get; private set; } = "";

        public void Bind(CascadeNet n, TwinView t, Camera c)
        {
            net = n; twin = t; cam = c;
            net.Pinged += OnPing;
        }

        void EnsureRoot()
        {
            if (root) return;
            root = new GameObject("Collab").transform;
            root.SetParent(twin.transform, false);
        }

        void OnPing(Net.Ping p)
        {
            EnsureRoot();
            var color = p.Kind == "come" ? CascadeStyle.Accent : CascadeStyle.Warn;
            var at = twin.L(p.X, p.Z);
            var b = new Beacon { born = Time.time, color = color };
            b.t = new GameObject("Ping").transform;
            b.t.SetParent(root, false);
            b.t.localPosition = at;
            var beam = CascadeStyle.Cube(b.t, "Beam", new Vector3(0, 2.5f, 0), new Vector3(0.12f, 5f, 0.12f), CascadeStyle.Glow);
            b.beam = beam.GetComponent<Renderer>();
            var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(ring.GetComponent<Collider>());
            ring.transform.SetParent(b.t, false);
            ring.transform.localPosition = new Vector3(0, 0.08f, 0);
            ring.GetComponent<Renderer>().sharedMaterial = CascadeStyle.Glow;
            b.ring = ring.GetComponent<Renderer>();
            var label = TwinView.MakeLabel(b.t, $"{p.FromName} · {(p.Kind == "come" ? "come here" : "look here")}\n<size=70%>{p.Label}</size>", 2.2f, CascadeStyle.Ink, new Vector3(0, 3.0f, 0), new Vector2(8, 1.4f), false, true);
            label.outlineWidth = 0.25f; label.outlineColor = new Color32(255, 255, 255, 230);
            label.name = "Label";
            beacons.Add(b);
            LastPing = $"{p.FromName} ({(p.FromRole == "technician" ? "technician" : "manager")}) flagged {p.Label}";
            Speech.Say($"{p.FromName} flagged {p.Label}.");
        }

        void Update()
        {
            if (net == null || !net.Ready || !twin.Has("MECH")) return;
            EnsureRoot();
            float t = Time.time;
            for (int i = beacons.Count - 1; i >= 0; i--)
            {
                var b = beacons[i];
                float age = t - b.born;
                if (age > 8f || !b.t) { if (b.t) Destroy(b.t.gameObject); beacons.RemoveAt(i); continue; }
                float pulse = Mathf.Repeat(age, 1.2f) / 1.2f;
                b.ring.transform.localScale = new Vector3(0.6f + pulse * 3f, 0.01f, 0.6f + pulse * 3f);
                float fade = age > 6.5f ? 1 - (age - 6.5f) / 1.5f : 1;
                CascadeStyle.Tint(b.ring, b.color.A((1 - pulse) * 0.8f * fade), b.color * 0.4f);
                CascadeStyle.Tint(b.beam, b.color.A(0.55f * fade), b.color * (0.5f + 0.3f * Mathf.Sin(t * 8f)));
                Face(b.t.Find("Label"));
            }

            if (net.Version != seenVersion)
            {
                seenVersion = net.Version;
                var rows = net.Db.FieldNote.Iter().ToList();
                foreach (var id in notes.Keys.ToList()) if (!rows.Any(r => r.Id == id)) { if (notes[id]) Destroy(notes[id].gameObject); notes.Remove(id); }
                foreach (var n in rows)
                {
                    if (!notes.TryGetValue(n.Id, out var label) || !label)
                    {
                        // stack notes at the same spot so they don't overlap
                        int stack = rows.Count(o => o.Id < n.Id && Mathf.Abs(o.X - n.X) < 0.5f && Mathf.Abs(o.Z - n.Z) < 0.5f);
                        label = TwinView.MakeLabel(root, "", 1.3f, CascadeStyle.Ink, twin.L(n.X, n.Z, 1.55f + stack * 0.32f), new Vector2(5, 1), false, false);
                        label.outlineWidth = 0.3f;
                        notes[n.Id] = label;
                    }
                    label.outlineColor = n.IsPrivate ? new Color32(237, 233, 254, 255) : new Color32(254, 243, 199, 255);
                    label.text = $"<b>{(n.IsPrivate ? "Private · " : "")}{n.AuthorName}:</b> {n.Text}";
                    label.color = n.IsPrivate ? AtlasStyle.Hex("#5B21B6") : AtlasStyle.Hex("#92400E");
                }
            }
            foreach (var l in notes.Values) Face(l ? l.transform : null);
        }

        void Face(Transform t)
        {
            if (!t || !cam) return;
            var d = t.position - cam.transform.position; d.y = 0;
            if (d.sqrMagnitude > 1e-6f) t.rotation = Quaternion.LookRotation(d);
        }
    }
}

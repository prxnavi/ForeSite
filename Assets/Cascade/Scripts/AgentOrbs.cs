using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace Cascade
{
    /// <summary>
    /// One glowing orb per agent, in world space. Idle orbs hover in an arc above your
    /// assistant panel (walk-in) or behind the model (overview). When an agent uses a tool
    /// on an asset or room, its orb flies there, so you can watch the Signal Analyst go to
    /// the chiller while the Lease Analyst checks the server room. The Critic flashes red
    /// when it sends work back.
    /// </summary>
    public class AgentOrbs : MonoBehaviour
    {
        class Orb { public string agent; public Transform t; public Renderer r; public TextMeshPro label; public Vector3 vel; public float activeUntil; public string target; public bool running; public float flashUntil; }

        CascadeNet net;
        TwinView twin;
        Camera cam;
        Transform panelAnchor;
        readonly Dictionary<string, Orb> orbs = new();
        readonly HashSet<ulong> seenRevise = new();
        int seenVersion = -1;

        public void Bind(CascadeNet n, TwinView v, Camera c, Transform panel)
        {
            net = n; twin = v; cam = c; panelAnchor = panel;
            foreach (var a in CascadeStyle.Agents)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(go.GetComponent<Collider>());
                go.name = "Orb " + a;
                go.transform.SetParent(transform, false);
                var r = go.GetComponent<Renderer>();
                r.sharedMaterial = CascadeStyle.Glow;
                var label = TwinView.MakeLabel(go.transform, CascadeStyle.AgentName(a), 3.6f, CascadeStyle.AgentColor(a), new Vector3(0, 1.4f, 0), new Vector2(10, 1.2f), false, true);
                label.outlineWidth = 0.2f; label.outlineColor = new Color32(255, 255, 255, 200);
                orbs[a] = new Orb { agent = a, t = go.transform, r = r, label = label };
            }
        }

        Vector3 Home(string agent)
        {
            int i = System.Array.IndexOf(CascadeStyle.Agents, agent);
            float u = i / (CascadeStyle.Agents.Length - 1f) - 0.5f;
            if (twin.LifeSize && panelAnchor)
                return panelAnchor.TransformPoint(new Vector3(-0.34f + u * 0.40f, 0.0f + Mathf.Cos(u * 2.4f) * 0.015f, 0.80f));
            return twin.transform.TransformPoint(new Vector3(u * twin.Size.x * 0.8f, 4.2f, twin.Size.y / 2 + 1.6f));
        }

        void Update()
        {
            if (net == null || !net.Ready) return;
            bool built = net.Timeline?.Built ?? false;

            if (net.Version != seenVersion)
            {
                seenVersion = net.Version;
                var inv = net.Investigation;
                var steps = inv == null ? new List<Net.AgentStep>() : net.Db.AgentStep.Iter().Where(s => s.InvId == inv.Id).OrderBy(s => s.Seq).ToList();
                foreach (var o in orbs.Values)
                {
                    var last = steps.LastOrDefault(s => s.Agent == o.agent);
                    o.running = last != null && (last.Status == "running" || last.Status == "thinking");
                    var withTarget = steps.LastOrDefault(s => s.Agent == o.agent && !string.IsNullOrEmpty(s.Target));
                    var target = withTarget?.Target;
                    if (target != null && target != o.target) o.activeUntil = Time.time + 4f;
                    if (o.running) o.activeUntil = Time.time + 3f;
                    o.target = target;
                    if (last != null && last.Status == "revise" && seenRevise.Add(last.Id)) o.flashUntil = Time.time + 4f;
                }
            }

            float t = Time.time;
            float labelWorld = twin.LifeSize ? 0.016f : 0.005f;
            foreach (var o in orbs.Values)
            {
                o.t.gameObject.SetActive(built);
                if (!built) continue;
                bool away = o.target != null && twin.Has(o.target) && t < o.activeUntil;
                float size = twin.LifeSize ? (away ? 0.16f : 0.045f) : 0.02f;
                Vector3 goal = away
                    ? twin.WorldOf(o.target, twin.LifeSize ? 2.1f : 3.4f) + Vector3.up * (Mathf.Sin(t * 3 + o.agent.Length) * size * 0.6f)
                    : Home(o.agent) + Vector3.up * (Mathf.Sin(t * 1.5f + o.agent.Length) * size * 0.3f);
                o.t.position = Vector3.SmoothDamp(o.t.position, goal, ref o.vel, twin.LifeSize ? 0.7f : 0.45f);

                var c = CascadeStyle.AgentColor(o.agent);
                bool flash = t < o.flashUntil;
                if (flash) c = Mathf.Repeat(t * 6f, 1f) > 0.5f ? CascadeStyle.Danger : Color.white;
                float glow = o.running ? 0.55f + 0.35f * Mathf.Sin(t * 8f) : flash ? 0.8f : 0.2f;
                CascadeStyle.Tint(o.r, c, c * glow);
                float s = Mathf.Lerp(o.t.localScale.x, size * (o.running ? 1.3f : 1f), Time.deltaTime * 4f);
                o.t.localScale = Vector3.one * s;
                o.label.transform.localScale = Vector3.one * (labelWorld / 0.36f) / s;
                o.label.transform.localPosition = new Vector3(0, 1.3f, 0);
                o.label.color = flash ? CascadeStyle.Danger : Color.Lerp(CascadeStyle.AgentColor(o.agent), Color.black, 0.35f);
                o.label.text = flash ? "Critic: revise!" : CascadeStyle.AgentName(o.agent);
                o.label.gameObject.SetActive(away || o.running || flash);
                if (cam)
                {
                    var d = o.label.transform.position - cam.transform.position;
                    if (d.sqrMagnitude > 1e-6f) o.label.transform.rotation = Quaternion.LookRotation(d);
                }
            }
        }
    }
}

using System.Collections.Generic;
using UnityEngine;

namespace CbreTwin
{
    /// <summary>
    /// Multi-user walkthrough: streams this user's camera pose into the `presence`
    /// table and renders everyone else on the same blueprint in real time.
    /// </summary>
    public class PresenceSync : MonoBehaviour
    {
        public CameraRig Rig;
        /// <summary>Optional pose override (the headset uses its head transform).</summary>
        public System.Func<(Vector3 pos, float yaw)> PoseSource;
        public BlueprintBuilder Builder;
        public readonly Dictionary<string, (Transform t, string name, Color color)> Others = new();

        float nextSend;
        Vector3 lastSent;
        float lastYaw;
        readonly Dictionary<string, Material> mats = new();

        TwinConnection Net => TwinConnection.I;

        void Update()
        {
            if (Net == null || !Net.Ready || Net.Me == null || Builder == null || Builder.BlueprintId == 0 || (Rig == null && PoseSource == null)) return;
            var bp = Builder.BlueprintId;

            if (Time.unscaledTime >= nextSend)
            {
                nextSend = Time.unscaledTime + 0.2f;
                var (p, yaw) = PoseSource != null ? PoseSource() : (Rig.Position, Rig.Yaw);
                if ((p - lastSent).sqrMagnitude > 0.0025f || Mathf.Abs(yaw - lastYaw) > 1f)
                {
                    lastSent = p;
                    lastYaw = yaw;
                    Net.Conn.Reducers.UpdatePresence(bp, p.x, p.y, p.z, yaw);
                }
            }

            var seen = new HashSet<string>();
            var me = Net.MyIdentity;
            foreach (var pr in Net.Presences(bp))
            {
                if (me.HasValue && pr.Identity.Equals(me.Value)) continue;
                var key = pr.Identity.ToString();
                seen.Add(key);
                var color = Palette.Hex(pr.Color);
                if (!Others.TryGetValue(key, out var o) || o.t == null)
                {
                    var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                    Destroy(go.GetComponent<Collider>());
                    go.name = $"Teammate {pr.Name}";
                    go.transform.SetParent(transform, false);
                    go.transform.localScale = new Vector3(0.45f, 0.5f, 0.45f);
                    go.GetComponent<Renderer>().sharedMaterial = MatFor(color);
                    var visor = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    Destroy(visor.GetComponent<Collider>());
                    visor.transform.SetParent(go.transform, false);
                    visor.transform.localPosition = new Vector3(0, 0.45f, 0.45f);
                    visor.transform.localScale = new Vector3(0.8f, 0.25f, 0.3f);
                    visor.GetComponent<Renderer>().sharedMaterial = MatFor(Palette.Ink);
                    o = (go.transform, pr.Name, color);
                }
                var target = new Vector3(pr.X, pr.Y, pr.Z);
                o.t.position = Vector3.Lerp(o.t.position == Vector3.zero ? target : o.t.position, target, 1f - Mathf.Exp(-10f * Time.deltaTime));
                o.t.rotation = Quaternion.Euler(0, pr.Yaw, 0);
                Others[key] = (o.t, pr.Name, color);
            }
            var gone = new List<string>();
            foreach (var kv in Others) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (var k in gone)
            {
                if (Others[k].t) Destroy(Others[k].t.gameObject);
                Others.Remove(k);
            }
        }

        Material MatFor(Color c)
        {
            var key = ColorUtility.ToHtmlStringRGB(c);
            if (mats.TryGetValue(key, out var m)) return m;
            m = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
            m.SetColor("_BaseColor", c);
            m.color = c;
            mats[key] = m;
            return m;
        }
    }
}

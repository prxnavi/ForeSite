using Atlas;
using UnityEngine;

namespace Cascade
{
    /// Palette, agent colors and shared materials (reuses the Atlas/Block unlit shader).
    public static class CascadeStyle
    {
        static Color Hex(string h) => AtlasStyle.Hex(h);

        public static readonly Color Accent = Hex("#38BDF8");    // cyan-blue: the "cool" brand
        public static readonly Color Good = Hex("#34D399");
        public static readonly Color Warn = Hex("#FBBF24");
        public static readonly Color Danger = Hex("#F87171");
        public static readonly Color Repair = Hex("#60A5FA");

        public static readonly Color Floor = Hex("#E9EEF2");
        public static readonly Color FloorCool = Hex("#CDEBE0");
        public static readonly Color FloorWarm = Hex("#F7DFA6");
        public static readonly Color FloorHot = Hex("#F58C7A");
        public static readonly Color FloorDown = Hex("#D9483B");
        public static readonly Color Corridor = Hex("#D7DCE2");
        public static readonly Color Mech = Hex("#C3CAD3");
        public static readonly Color Wall = Hex("#F4F6F8");
        public static readonly Color Blueprint = Hex("#7DD3FC");
        public static readonly Color Ink = Hex("#1F2A37");
        public static readonly Color AssetBody = Hex("#5B6B7C");

        public static Color RoomColor(float temp, float start, float limit)
        {
            if (float.IsNaN(temp)) return FloorCool;
            if (temp > limit) return FloorDown;
            float f = Mathf.Clamp01((temp - start) / Mathf.Max(1f, limit - start));
            return f < 0.5f ? Color.Lerp(FloorCool, FloorWarm, f * 2f) : Color.Lerp(FloorWarm, FloorHot, (f - 0.5f) * 2f);
        }

        public static Color HealthColor(string h) => h switch
        {
            "watch" => Warn,
            "degraded" => Warn,
            "critical" => Danger,
            "failed" => Danger,
            "starved" => Warn,
            "repair" => Repair,
            _ => Good,
        };

        public static string HealthLabel(string h) => h switch
        {
            "watch" => "Watch",
            "degraded" => "Degraded",
            "critical" => "At risk",
            "failed" => "FAILED",
            "starved" => "Low chilled water",
            "repair" => "Under repair",
            _ => "Healthy",
        };

        // ---- agents
        public static readonly string[] Agents = { "planner", "signal", "historian", "lease", "structural", "simulation", "critic", "narrator" };

        public static Color AgentColor(string a) => a switch
        {
            "planner" => Hex("#A78BFA"),
            "signal" => Hex("#60A5FA"),
            "historian" => Hex("#FBBF24"),
            "lease" => Hex("#34D399"),
            "structural" => Hex("#F97316"),
            "simulation" => Hex("#F472B6"),
            "critic" => Hex("#E5E7EB"),
            "narrator" => Hex("#22D3EE"),
            _ => Color.white,
        };

        public static string AgentName(string a) => a switch
        {
            "planner" => "Planner",
            "signal" => "Signal Analyst",
            "historian" => "Historian",
            "lease" => "Lease Analyst",
            "structural" => "Structural Engineer",
            "simulation" => "Sim Engineer",
            "critic" => "Critic",
            "narrator" => "Narrator",
            _ => a,
        };

        // ---- materials (render-order bands; walls last so see-through walls don't hide things)
        static Material solid, flat, glow, wall;
        public static Material Solid => solid ? solid : solid = Make(2950, false);
        public static Material Flat => flat ? flat : flat = Make(2940, true);
        public static Material Glow => glow ? glow : glow = Make(2960, true);
        public static Material WallMat => wall ? wall : wall = Make(2970, false);

        static Material Make(int queue, bool isFlat)
        {
            var shader = Shader.Find("Atlas/Block") ?? Shader.Find("Sprites/Default");
            var m = new Material(shader) { renderQueue = queue };
            if (m.HasProperty("_Flat")) m.SetFloat("_Flat", isFlat ? 1f : 0f);
            return m;
        }

        static MaterialPropertyBlock mpb;
        static readonly System.Collections.Generic.Dictionary<Renderer, Vector4> tiling = new();

        public static void Tint(Renderer r, Color c, Color emission = default)
        {
            mpb ??= new MaterialPropertyBlock();
            mpb.Clear();
            mpb.SetColor("_Color", c);
            mpb.SetColor("_Emission", emission);
            if (tiling.TryGetValue(r, out var st)) mpb.SetVector("_MainTex_ST", st);
            r.SetPropertyBlock(mpb);
        }

        // ---- lit, textured look (walk-through). Shader ships via Resources/CascadeLit.mat.
        static Material litBase, glowBase;
        static readonly System.Collections.Generic.Dictionary<string, Material> lit = new();

        /// Standard-shader material with a procedural texture (null = plain), cached per texture + smoothness.
        public static Material Lit(ProcTex.Kind? tex, float smooth = 0.15f)
        {
            string key = $"{tex}:{smooth:0.00}";
            if (lit.TryGetValue(key, out var m) && m) return m;
            litBase ??= Resources.Load<Material>("CascadeLit");
            m = litBase ? new Material(litBase) : new Material(Shader.Find("Standard"));
            if (tex.HasValue) m.mainTexture = ProcTex.Get(tex.Value);
            m.SetFloat("_Glossiness", smooth);
            lit[key] = m;
            return m;
        }

        /// Lit material that also glows (screens, LEDs, window daylight).
        public static Material LitGlow(Color c, float strength = 0.8f)
        {
            string key = $"glow:{ColorUtility.ToHtmlStringRGB(c)}:{strength:0.0}";
            if (lit.TryGetValue(key, out var m) && m) return m;
            glowBase ??= Resources.Load<Material>("CascadeLitGlow");
            m = glowBase ? new Material(glowBase) : new Material(Shader.Find("Standard"));
            m.color = c;
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", c * strength);
            lit[key] = m;
            return m;
        }

        /// Repeat a renderer's texture every `repeat` meters across a surface of the given size.
        public static void Tile(Renderer r, float widthM, float depthM, float repeat)
        {
            tiling[r] = repeat > 0 ? new Vector4(Mathf.Max(0.2f, widthM / repeat), Mathf.Max(0.2f, depthM / repeat), 0, 0) : new Vector4(1, 1, 0, 0);
        }

        public static GameObject LitBox(Transform parent, string name, Vector3 pos, Vector3 size, Material mat, Color color, float repeat = 0f)
        {
            var go = Cube(parent, name, pos, size, mat);
            var r = go.GetComponent<Renderer>();
            if (repeat > 0) Tile(r, Mathf.Max(size.x, size.z), Mathf.Max(size.y, Mathf.Min(size.x, size.z)), repeat);
            Tint(r, color);
            return go;
        }

        public static GameObject Cube(Transform parent, string name, Vector3 pos, Vector3 size, Material mat, bool collider = false)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            if (!collider) Object.Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            return go;
        }
    }
}

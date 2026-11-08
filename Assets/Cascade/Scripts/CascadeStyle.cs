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
        public static readonly string[] Agents = { "planner", "signal", "historian", "lease", "simulation", "critic", "narrator" };

        public static Color AgentColor(string a) => a switch
        {
            "planner" => Hex("#A78BFA"),
            "signal" => Hex("#60A5FA"),
            "historian" => Hex("#FBBF24"),
            "lease" => Hex("#34D399"),
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
        public static void Tint(Renderer r, Color c, Color emission = default)
        {
            mpb ??= new MaterialPropertyBlock();
            mpb.SetColor("_Color", c);
            mpb.SetColor("_Emission", emission);
            r.SetPropertyBlock(mpb);
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

using UnityEngine;

namespace Atlas
{
    /// Palette and shared materials, taken from the design mockups.
    public static class AtlasStyle
    {
        public static Color Hex(string hex) { ColorUtility.TryParseHtmlString(hex, out var c); return c; }
        public static Color A(this Color c, float a) { c.a = a; return c; }

        // Accent + semantic
        public static readonly Color Accent = Hex("#7C5CFA");
        public static readonly Color AccentSoft = Hex("#A78BFA");
        public static readonly Color Good = Hex("#34D399");
        public static readonly Color Warn = Hex("#FBBF24");
        public static readonly Color Danger = Hex("#F87171");

        // Block fills
        public static readonly Color FillNotExplored = Hex("#E7E4DE");
        public static readonly Color FillUnderstood = Hex("#CFEBDD");
        public static readonly Color FillRisk = Hex("#F6E3B4");
        public static readonly Color FillExternal = Hex("#F3C9BE");
        public static readonly Color FillSelected = Hex("#E6DFFD");
        public static readonly Color Ink = Hex("#1E1B2E");

        // Panels (dark glass over passthrough)
        public static readonly Color PanelFill = new(0.075f, 0.082f, 0.125f, 0.94f);
        public static readonly Color PanelEdge = new(1f, 1f, 1f, 0.10f);
        public static readonly Color Surface = new(1f, 1f, 1f, 0.06f);
        public static readonly Color SurfaceHi = new(1f, 1f, 1f, 0.11f);
        public static readonly Color TextHi = Hex("#F4F4F8");
        public static readonly Color TextMid = Hex("#C9CCDA");
        public static readonly Color TextLo = Hex("#8E94AA");

        public static Color Fill(NodeStatus s) => s switch
        {
            NodeStatus.Understood => FillUnderstood,
            NodeStatus.Risk => FillRisk,
            NodeStatus.External => FillExternal,
            _ => FillNotExplored,
        };

        public static Color StatusInk(NodeStatus s) => s switch
        {
            NodeStatus.Understood => Hex("#2F7D5B"),
            NodeStatus.Risk => Hex("#9A6B12"),
            NodeStatus.External => Hex("#A2483A"),
            _ => Hex("#77736B"),
        };

        public static string StatusLabel(NodeStatus s) => s switch
        {
            NodeStatus.Understood => "Understood",
            NodeStatus.Risk => "Elevated risk",
            NodeStatus.External => "External",
            _ => "Not explored",
        };

        public static Color RiskColor(int level) => level <= 1 ? Danger : level == 2 ? Warn : Hex("#FDE68A");

        public static Color PhaseColor(StepPhase p) => p switch
        {
            StepPhase.Understand => Hex("#60A5FA"),
            StepPhase.Plan => AccentSoft,
            StepPhase.Investigate => Warn,
            StepPhase.Check => Good,
            _ => Hex("#F0ABFC"),
        };

        // ---- materials (one per render-order band) ----
        static Material plate, line, block, pulse;
        public static Material PlateMat => plate ? plate : plate = Make(2930, true);
        public static Material LineMat => line ? line : line = Make(2940, true);
        public static Material BlockMat => block ? block : block = Make(2950, false);
        public static Material PulseMat => pulse ? pulse : pulse = Make(2960, true);

        static Material Make(int queue, bool flat)
        {
            var shader = Shader.Find("Atlas/Block");
            if (!shader)
            {
                Debug.LogWarning("[Atlas] Atlas/Block shader missing; falling back to Sprites/Default.");
                shader = Shader.Find("Sprites/Default");
            }
            var m = new Material(shader) { renderQueue = queue };
            if (m.HasProperty("_Flat")) m.SetFloat("_Flat", flat ? 1f : 0f);
            return m;
        }
    }
}

using System;
using Atlas;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Cascade
{
    /// Bright "frosted glass" UI kit on top of the Atlas UiKit primitives.
    public static class CUi
    {
        static Color Hex(string h) => AtlasStyle.Hex(h);

        public static readonly Color Fill = new(1f, 1f, 1f, 0.94f);
        public static readonly Color Edge = Hex("#BFD3E6");
        public static readonly Color Ink = Hex("#0F172A");
        public static readonly Color InkMid = Hex("#475569");
        public static readonly Color InkLo = Hex("#94A3B8");
        public static readonly Color Surface = Hex("#F1F5F9");
        public static readonly Color SurfaceHi = Hex("#E2E8F0");
        public static readonly Color Accent = Hex("#0284C7");
        public static readonly Color AccentSoft = Hex("#E0F2FE");
        public static readonly Color Good = Hex("#059669");
        public static readonly Color GoodSoft = Hex("#D1FAE5");
        public static readonly Color Danger = Hex("#DC2626");
        public static readonly Color DangerSoft = Hex("#FEE2E2");
        public static readonly Color Warn = Hex("#D97706");
        public static readonly Color WarnSoft = Hex("#FEF3C7");
        public static readonly Color Nvidia = Hex("#76B900");

        public enum Style { Primary, Secondary, Ghost, Danger, Good }

        public static RectTransform Panel(Transform parent, string name, float corner = 1f)
        {
            var edge = UiKit.Box(parent, name, Edge, corner, true).Stretch();
            var fill = UiKit.Box(edge.transform, "Fill", Fill, corner * 0.95f).Stretch(2f);
            return (RectTransform)fill.transform;
        }

        public static Image Card(Transform parent, Color color, float x, float y, float w, float h, float corner = 0.5f)
            => UiKit.Box(parent, "Card", color, corner).At(x, y, w, h);

        public static TextMeshProUGUI Text(Transform parent, string text, float size, Color color, float x, float y, float w, float h,
            FontStyles style = FontStyles.Normal, TextAlignmentOptions align = TextAlignmentOptions.TopLeft)
            => UiKit.Text(parent, text, size, color, style, align).At(x, y, w, h);

        public static TextMeshProUGUI Caps(Transform parent, string text, Color color, float x, float y, float w, float size = 11f)
            => UiKit.Caps(parent, text, color, size).At(x, y, w, size + 6);

        public static (Button b, TextMeshProUGUI t) Button(Transform parent, string label, Style style, Action onClick,
            float x, float y, float w, float h, float size = 16f)
        {
            var bg = UiKit.Box(parent, "Button", Color.white, 0.45f, true).At(x, y, w, h);
            var b = bg.gameObject.AddComponent<Button>();
            b.targetGraphic = bg;
            if (onClick != null) b.onClick.AddListener(() => onClick());
            var t = UiKit.Text(bg.transform, label, size, Ink, FontStyles.Bold, TextAlignmentOptions.Center);
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.Stretch();
            t.rectTransform.offsetMin = new Vector2(10, 0);
            t.rectTransform.offsetMax = new Vector2(-10, 0);
            Restyle(b, t, style);
            return (b, t);
        }

        public static void Restyle(Button b, TextMeshProUGUI t, Style style)
        {
            var (bg, fg) = style switch
            {
                Style.Primary => (Accent, Color.white),
                Style.Danger => (Danger, Color.white),
                Style.Good => (Good, Color.white),
                Style.Ghost => (new Color(1, 1, 1, 0f), Accent),
                _ => (Surface, Ink),
            };
            var c = b.colors;
            c.normalColor = bg;
            c.highlightedColor = style == Style.Ghost ? AccentSoft : Color.Lerp(bg, style == Style.Secondary ? Hex("#CBD5E1") : Color.white, 0.18f);
            c.selectedColor = bg;
            c.pressedColor = Color.Lerp(bg, Color.black, 0.15f);
            c.disabledColor = new Color(bg.r, bg.g, bg.b, 0.4f);
            c.colorMultiplier = 1f;
            c.fadeDuration = 0.08f;
            b.colors = c;
            if (t) t.color = fg;
        }

        public static (Image bg, TextMeshProUGUI t) Pill(Transform parent, string text, Color bg, Color fg, float x, float y, float w, float h = 26, float size = 12)
        {
            var img = UiKit.Box(parent, "Pill", bg, 0.6f).At(x, y, w, h);
            var t = UiKit.Text(img.transform, text, size, fg, FontStyles.Bold, TextAlignmentOptions.Center);
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.Stretch();
            return (img, t);
        }

        public static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);
        public static string Money(float v) => v >= 1000 ? $"${v / 1000f:0.#}k" : $"${v:0}";

        public static string Clock(float hour)
        {
            int total = Mathf.RoundToInt((14 + hour) * 60);
            return $"{(total / 60) % 24:00}:{total % 60:00}";
        }
    }
}

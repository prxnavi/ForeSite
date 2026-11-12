using System;
using System.Text;
using UnityEngine;

namespace CbreTwin
{
    /// <summary>A clickable in-world button hit by the controller ray.</summary>
    public class VRButton : MonoBehaviour
    {
        public Action OnClick;
        public Renderer Background;
        public Color Normal, Hover;
        bool hovered;

        public void SetHover(bool on)
        {
            if (hovered == on || Background == null) return;
            hovered = on;
            Background.material.color = on ? Hover : Normal;
            transform.localScale = on ? new Vector3(1.04f, 1.04f, 1f) : Vector3.one;
        }
    }

    /// <summary>Something in the house the ray can inspect (a building system).</summary>
    public class VRTarget : MonoBehaviour { public uint ComponentId; }

    /// <summary>Minimal world-space UI kit: panels, text and buttons built from primitives.</summary>
    public static class VRUI
    {
        public static readonly Color PanelBg = new(0.118f, 0.106f, 0.294f, 0.94f); // deep indigo
        public static readonly Color Card = new(1f, 1f, 1f, 0.97f);
        public static readonly Color Ink = new(0.118f, 0.106f, 0.294f);
        public static readonly Color White = Color.white;

        static Font font;

        static Font F => font ??= Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        public static Material Flat(Color c)
        {
            var m = ShaderLib.Create(c.a < 0.999f ? ShaderLib.Kind.UnlitTransparent : ShaderLib.Kind.UnlitOpaque);
            m.SetColor("_BaseColor", c);
            m.color = c;
            return m;
        }

        /// <summary>Creates an empty panel root with a rounded-looking flat background (size in metres).</summary>
        public static Transform Panel(string name, Transform parent, Vector2 size, Color bg)
        {
            var root = new GameObject(name).transform;
            if (parent != null) root.SetParent(parent, false);
            var back = GameObject.CreatePrimitive(PrimitiveType.Cube);
            back.name = "Background";
            UnityEngine.Object.Destroy(back.GetComponent<Collider>());
            back.transform.SetParent(root, false);
            back.transform.localScale = new Vector3(size.x, size.y, 0.006f);
            back.GetComponent<Renderer>().sharedMaterial = Flat(bg);
            return root;
        }

        /// <summary>Text facing the viewer (panel forward points away from the viewer).</summary>
        public static TextMesh Text(Transform parent, string text, Vector2 pos, float lineHeight, Color color, TextAnchor anchor = TextAnchor.UpperLeft, int wrap = 0, bool bold = false)
        {
            var go = new GameObject("Text");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(pos.x, pos.y, -0.008f);
            var tm = go.AddComponent<TextMesh>();
            tm.font = F;
            tm.fontSize = 64;
            tm.characterSize = lineHeight * 10f / tm.fontSize;
            tm.anchor = anchor;
            tm.alignment = anchor == TextAnchor.UpperCenter || anchor == TextAnchor.MiddleCenter ? TextAlignment.Center : TextAlignment.Left;
            tm.color = color;
            tm.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            tm.text = wrap > 0 ? Wrap(text, wrap) : text;
            go.GetComponent<MeshRenderer>().sharedMaterial = F.material;
            return tm;
        }

        public static VRButton Button(Transform parent, string label, Vector2 pos, Vector2 size, Color bg, Color fg, Action onClick)
        {
            var go = new GameObject($"Button {label}");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(pos.x, pos.y, -0.006f);
            var back = GameObject.CreatePrimitive(PrimitiveType.Cube);
            UnityEngine.Object.Destroy(back.GetComponent<Collider>());
            back.transform.SetParent(go.transform, false);
            back.transform.localScale = new Vector3(size.x, size.y, 0.008f);
            var r = back.GetComponent<Renderer>();
            r.material = Flat(bg);
            var col = go.AddComponent<BoxCollider>();
            col.size = new Vector3(size.x, size.y, 0.02f);
            Text(go.transform, label, Vector2.zero, size.y * 0.36f, fg, TextAnchor.MiddleCenter, 0, true);
            var b = go.AddComponent<VRButton>();
            b.Background = r;
            b.Normal = bg;
            b.Hover = Color.Lerp(bg, Color.white, 0.25f);
            b.OnClick = onClick;
            return b;
        }

        public static string Wrap(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder();
            foreach (var para in text.Split('\n'))
            {
                int line = 0;
                foreach (var word in para.Split(' '))
                {
                    if (line > 0 && line + word.Length + 1 > maxChars) { sb.Append('\n'); line = 0; }
                    else if (line > 0) { sb.Append(' '); line++; }
                    sb.Append(word);
                    line += word.Length;
                }
                sb.Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        /// <summary>Turn a panel so its text reads from the viewer's eye.</summary>
        public static void Face(Transform panel, Vector3 eye)
        {
            var d = panel.position - eye;
            d.y *= 0.4f; // keep panels mostly upright
            if (d.sqrMagnitude > 0.0001f) panel.rotation = Quaternion.LookRotation(d.normalized, Vector3.up);
        }
    }
}

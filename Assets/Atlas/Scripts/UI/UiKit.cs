using System;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Atlas
{
    /// Small helpers for building world-space uGUI panels in code.
    /// Units: 1 canvas unit = 1 px; canvases are scaled so 1 px = 0.8 mm.
    public static class UiKit
    {
        public const float PxToMeters = 0.0008f;

        /// Hook for platform setup on every world canvas (XR adds a tracked-device raycaster).
        public static Action<Canvas> CanvasCreated;

        static Sprite rounded;
        public static Sprite Rounded => rounded ? rounded : rounded = MakeRounded(64, 24);

        static Sprite MakeRounded(int size, int r)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "AtlasRounded" };
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = Mathf.Max(0, Mathf.Max(r - (x + 0.5f), (x + 0.5f) - (size - r)));
                float dy = Mathf.Max(0, Mathf.Max(r - (y + 0.5f), (y + 0.5f) - (size - r)));
                float a = Mathf.Clamp01(r - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(r, r, r, r));
        }

        public static Canvas WorldCanvas(string name, Transform parent, Vector2 size, Camera cam)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            go.transform.SetParent(parent, false);
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = cam;
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = size;
            rt.localScale = Vector3.one * PxToMeters;
            CanvasCreated?.Invoke(canvas);
            return canvas;
        }

        public static RectTransform Rect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        /// Position a rect from the parent's top-left corner (y grows downward).
        public static T At<T>(this T c, float x, float y, float w, float h) where T : Component
        {
            var rt = (RectTransform)c.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            return c;
        }

        public static T Stretch<T>(this T c, float inset = 0) where T : Component
        {
            var rt = (RectTransform)c.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(inset, inset);
            rt.offsetMax = new Vector2(-inset, -inset);
            return c;
        }

        public static Image Box(Transform parent, string name, Color color, float cornerScale = 1f, bool raycast = false)
        {
            var img = Rect(parent, name).gameObject.AddComponent<Image>();
            img.sprite = Rounded;
            img.type = Image.Type.Sliced;
            img.pixelsPerUnitMultiplier = 1f / Mathf.Max(0.05f, cornerScale);
            img.color = color;
            img.raycastTarget = raycast;
            return img;
        }

        /// Dark glass panel with a hairline edge. Returns the content root.
        public static RectTransform Panel(Transform parent, string name)
        {
            var edge = Box(parent, name, AtlasStyle.PanelEdge, 0.9f, true).Stretch();
            var fill = Box(edge.transform, "Fill", AtlasStyle.PanelFill, 0.85f).Stretch(1.5f);
            return (RectTransform)fill.transform;
        }

        public static TextMeshProUGUI Text(Transform parent, string text, float size, Color color,
            FontStyles style = FontStyles.Normal, TextAlignmentOptions align = TextAlignmentOptions.TopLeft)
        {
            var t = Rect(parent, "Text").gameObject.AddComponent<TextMeshProUGUI>();
            t.text = text;
            t.fontSize = size;
            t.color = color;
            t.fontStyle = style;
            t.alignment = align;
            t.raycastTarget = false;
            t.textWrappingMode = TextWrappingModes.Normal;
            t.overflowMode = TextOverflowModes.Truncate;
            return t;
        }

        /// Small caps label: "ATLAS GUIDE"
        public static TextMeshProUGUI Caps(Transform parent, string text, Color color, float size = 12f)
        {
            var t = Text(parent, text.ToUpperInvariant(), size, color, FontStyles.Bold);
            t.characterSpacing = 8f;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            return t;
        }

        public static Button Button(Transform parent, string label, bool primary, Action onClick,
            out TextMeshProUGUI text, TextAlignmentOptions align = TextAlignmentOptions.Center, float fontSize = 16f)
        {
            // Image stays white; the ColorBlock carries the real colors so hover can brighten.
            var bg = Box(parent, "Button", Color.white, 0.45f, true);
            var b = bg.gameObject.AddComponent<Button>();
            b.targetGraphic = bg;
            SetButtonColors(b, primary ? AtlasStyle.Accent : AtlasStyle.Surface);
            if (onClick != null) b.onClick.AddListener(() => onClick());

            text = Text(bg.transform, label, fontSize, primary ? Color.white : AtlasStyle.TextHi, FontStyles.Bold, align);
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.Stretch();
            var rt = text.rectTransform;
            rt.offsetMin = new Vector2(16, 0);
            rt.offsetMax = new Vector2(-16, 0);
            return b;
        }

        public static void SetButtonColors(Button b, Color baseColor)
        {
            var hover = baseColor.a < 0.5f
                ? baseColor.A(baseColor.a + 0.08f)
                : Color.Lerp(baseColor, Color.white, 0.15f);
            var colors = b.colors;
            colors.normalColor = baseColor;
            colors.highlightedColor = hover;
            colors.selectedColor = baseColor;
            colors.pressedColor = Color.Lerp(baseColor, Color.black, 0.2f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.08f;
            b.colors = colors;
        }

        /// Press-and-hold support (push-to-talk).
        public class Hold : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
        {
            public UnityAction<bool> Changed;
            public void OnPointerDown(PointerEventData e) => Changed?.Invoke(true);
            public void OnPointerUp(PointerEventData e) => Changed?.Invoke(false);
        }
    }
}

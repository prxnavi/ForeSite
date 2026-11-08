using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Atlas
{
    /// One piece of the architecture map: a tabletop block with its label.
    /// Pointer events come from the EventSystem (mouse in editor; Meta XR ray /
    /// pinch on device), so the same handlers serve both.
    public class NodeBlock : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerClickHandler,
        IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public const float W = 0.15f, H = 0.028f, D = 0.095f;

        public ArchNode Data { get; private set; }

        // Targets written by AtlasMapView; the block animates toward them.
        [HideInInspector] public Vector3 HomePos;
        [HideInInspector] public Vector2 Offset;
        [HideInInspector] public Color TargetFill = Color.white;
        [System.NonSerialized] public Color? TargetRim;
        [HideInInspector] public bool TargetDim;
        [HideInInspector] public float TargetLift;
        [HideInInspector] public string TagOverride;

        public bool Hovered { get; private set; }
        public bool Dragging { get; private set; }

        AtlasMapView map;
        MeshRenderer body, rim;
        TextMeshPro codeText, titleText, tagText;
        MaterialPropertyBlock mpb;
        Color fill, rimColor;
        float alpha = 1f, lift;
        bool dragged;
        Vector3 grabDelta;

        public void Init(ArchNode n, AtlasMapView owner)
        {
            Data = n;
            map = owner;
            name = "Node_" + n.Id;
            mpb = new MaterialPropertyBlock();

            body = Cube("Body", new Vector3(0, H / 2f, 0), new Vector3(W, H, D), true);
            rim = Cube("Rim", new Vector3(0, H * 0.28f, 0), new Vector3(W + 0.01f, H * 0.56f, D + 0.01f), false);

            var label = new GameObject("Label").transform;
            label.SetParent(transform, false);
            label.localPosition = new Vector3(0, H + 0.0008f, 0);
            label.localRotation = Quaternion.Euler(90, 0, 0); // lie flat, readable from the near edge
            label.localScale = Vector3.one * 0.01f;           // 1 unit = 1 cm

            codeText = Text(label, n.Id, 6f, 2.9f, FontStyles.Bold, 6f);
            titleText = Text(label, n.Title, 10.5f, 0.6f, FontStyles.Bold, 0f);
            tagText = Text(label, "", 6.2f, -2.1f, FontStyles.Normal, 0f);

            fill = TargetFill = AtlasStyle.Fill(n.Status);
            rimColor = Darken(fill);
            Apply();
        }

        MeshRenderer Cube(string n, Vector3 pos, Vector3 scale, bool keepCollider)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = n;
            if (!keepCollider) Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(transform, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = AtlasStyle.BlockMat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return r;
        }

        static TextMeshPro Text(Transform parent, string s, float size, float y, FontStyles style, float spacing)
        {
            var go = new GameObject("Text");
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshPro>();
            t.text = s;
            t.fontSize = size;
            t.fontStyle = style;
            t.characterSpacing = spacing;
            t.alignment = TextAlignmentOptions.Left;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Truncate;
            t.rectTransform.sizeDelta = new Vector2(W * 100f - 2f, 1.8f);
            go.transform.localPosition = new Vector3(0, y, 0);
            return t;
        }

        static Color Darken(Color c) => new(c.r * 0.55f, c.g * 0.55f, c.b * 0.6f, 1f);

        void Update()
        {
            float k = 1f - Mathf.Exp(-Time.deltaTime * 12f);
            fill = Color.Lerp(fill, TargetFill, k);
            rimColor = Color.Lerp(rimColor, TargetRim ?? Darken(TargetFill), k);
            alpha = Mathf.Lerp(alpha, TargetDim ? 0.28f : 1f, k);
            lift = Mathf.Lerp(lift, TargetLift + (Hovered ? 0.006f : 0f) + (Dragging ? 0.02f : 0f), k);

            var target = HomePos + new Vector3(Offset.x, lift, Offset.y);
            transform.localPosition = Dragging
                ? new Vector3(target.x, Mathf.Lerp(transform.localPosition.y, target.y, k), target.z)
                : Vector3.Lerp(transform.localPosition, target, k);
            Apply();
        }

        void Apply()
        {
            mpb.SetColor("_Color", fill.A(alpha));
            mpb.SetColor("_Emission", Color.clear);
            body.SetPropertyBlock(mpb);

            mpb.SetColor("_Color", rimColor.A(alpha));
            mpb.SetColor("_Emission", TargetRim.HasValue ? rimColor * 0.15f : Color.clear);
            rim.SetPropertyBlock(mpb);

            var ink = AtlasStyle.StatusInk(Data.Status);
            codeText.color = ink.A(alpha * 0.9f);
            titleText.color = AtlasStyle.Ink.A(alpha);
            tagText.color = ink.A(alpha);
            string tag = TagOverride ?? $"{AtlasStyle.StatusLabel(Data.Status)} · {Data.Tag}";
            if (tagText.text != tag) tagText.text = tag;
        }

        // ---------------- pointer ----------------

        public void OnPointerEnter(PointerEventData e) => Hovered = true;
        public void OnPointerExit(PointerEventData e) => Hovered = false;
        public void OnPointerDown(PointerEventData e) => dragged = false;

        public void OnPointerClick(PointerEventData e)
        {
            if (dragged) return;
            map.OnBlockClicked(this);
        }

        public void OnBeginDrag(PointerEventData e)
        {
            if (!map.TryTablePoint(e, out var p)) return;
            Dragging = dragged = true;
            grabDelta = p - (HomePos + new Vector3(Offset.x, 0, Offset.y));
        }

        public void OnDrag(PointerEventData e)
        {
            if (!Dragging || !map.TryTablePoint(e, out var p)) return;
            var o = p - grabDelta - HomePos;
            map.Backend.MoveNode(Data.Id, new Vector2(o.x, o.z), true);
        }

        public void OnEndDrag(PointerEventData e)
        {
            if (!Dragging) return;
            Dragging = false;
            map.OnBlockReleased(this);
        }
    }
}

using TMPro;
using UnityEngine;

namespace Atlas
{
    /// A connection between two blocks, drawn flat on the table with a small label.
    public class EdgeLine : MonoBehaviour
    {
        public ArchEdge Data { get; private set; }
        public NodeBlock A { get; private set; }
        public NodeBlock B { get; private set; }

        [HideInInspector] public Color TargetColor = Color.white.A(0.35f);
        [HideInInspector] public float TargetWidth = 0.0035f;
        [HideInInspector] public float TargetLabelAlpha = 0.55f;

        LineRenderer lr;
        TextMeshPro label;
        MaterialPropertyBlock mpb;
        Color color;
        float width, labelAlpha;
        Transform space;

        public void Init(ArchEdge e, NodeBlock a, NodeBlock b, Transform mapSpace)
        {
            Data = e; A = a; B = b; space = mapSpace;
            name = $"Edge_{e.From}_{e.To}";
            mpb = new MaterialPropertyBlock();

            lr = gameObject.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.positionCount = 2;
            lr.alignment = LineAlignment.TransformZ;   // ribbon lies flat on the table
            lr.numCapVertices = 3;
            lr.sharedMaterial = AtlasStyle.LineMat;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            transform.localRotation = Quaternion.Euler(90, 0, 0);

            var lg = new GameObject("Label");
            lg.transform.SetParent(transform.parent, false);
            label = lg.AddComponent<TextMeshPro>();
            label.text = e.Label;
            label.fontSize = 5f;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.rectTransform.sizeDelta = new Vector2(8f, 1.2f);
            lg.transform.localRotation = Quaternion.Euler(90, 0, 0);
            lg.transform.localScale = Vector3.one * 0.01f;

            color = TargetColor;
            width = TargetWidth;
        }

        void LateUpdate()
        {
            float k = 1f - Mathf.Exp(-Time.deltaTime * 10f);
            color = Color.Lerp(color, TargetColor, k);
            width = Mathf.Lerp(width, TargetWidth, k);
            labelAlpha = Mathf.Lerp(labelAlpha, TargetLabelAlpha, k);

            Vector3 a = A.transform.localPosition, b = B.transform.localPosition;
            a.y = b.y = 0.003f;
            lr.SetPosition(0, space.TransformPoint(a));
            lr.SetPosition(1, space.TransformPoint(b));
            float scale = space.lossyScale.x;
            lr.startWidth = lr.endWidth = width * scale;
            mpb.SetColor("_Color", color);
            lr.SetPropertyBlock(mpb);

            // label beside the midpoint: right of vertical edges, above horizontal ones
            var mid = (a + b) * 0.5f;
            var dir = b - a;
            bool vertical = Mathf.Abs(dir.z) > Mathf.Abs(dir.x);
            label.alignment = vertical ? TextAlignmentOptions.Left : TextAlignmentOptions.Center;
            var lp = mid + (vertical ? new Vector3(0.048f, 0.004f, 0) : new Vector3(0, 0.004f, 0.009f));
            label.transform.localPosition = lp;
            label.color = Color.white.A(labelAlpha);
        }
    }
}

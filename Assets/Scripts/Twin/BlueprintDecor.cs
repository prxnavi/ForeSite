using System.Collections.Generic;
using CbreTwin.Net;
using UnityEngine;

namespace CbreTwin
{
    /// <summary>Textured materials, trim, lighting and furniture that make each room read as a real space.</summary>
    public partial class BlueprintBuilder
    {
        readonly Dictionary<string, Material> texMats = new();

        static readonly Color Oak = new(0.69f, 0.53f, 0.38f);
        static readonly Color Walnut = new(0.40f, 0.28f, 0.20f);
        static readonly Color Linen = new(0.95f, 0.93f, 0.89f);
        static readonly Color Charcoal = new(0.20f, 0.20f, 0.22f);
        static readonly Color Steel = new(0.78f, 0.79f, 0.80f);
        static readonly Color Leaf = new(0.33f, 0.55f, 0.32f);
        static readonly Color Terracotta = new(0.78f, 0.45f, 0.32f);
        static readonly Color Brass = new(0.83f, 0.68f, 0.38f);

        /// <summary>A lit material using a procedural texture, tiled to <paramref name="tiling"/>.</summary>
        Material TexMat(ProcTex.Kind kind, Color tint, Vector2 tiling, float smooth = 0.25f)
        {
            tiling = new Vector2(Mathf.Max(0.25f, Mathf.Round(tiling.x * 2) / 2), Mathf.Max(0.25f, Mathf.Round(tiling.y * 2) / 2));
            var key = $"{kind}:{ColorUtility.ToHtmlStringRGB(tint)}:{tiling.x}:{tiling.y}:{smooth:0.0}";
            if (texMats.TryGetValue(key, out var m) && m != null) return m;
            m = ShaderLib.Create(ShaderLib.Kind.LitOpaque);
            var tex = ProcTex.Get(kind);
            m.SetTexture("_BaseMap", tex);
            m.mainTexture = tex;
            m.SetTextureScale("_BaseMap", tiling);
            m.mainTextureScale = tiling;
            m.SetColor("_BaseColor", tint);
            m.color = tint;
            m.SetFloat("_Smoothness", smooth);
            texMats[key] = m;
            return m;
        }

        /// <summary>Box with a textured surface; tiling follows its footprint (floors) or face (walls).</summary>
        GameObject TBox(Transform parent, Vector3 centerFt, Vector3 sizeFt, ProcTex.Kind kind, Color tint, bool wallTiling = false,
            bool keepCollider = false, bool solid = false, bool animate = true, float smooth = 0.25f)
        {
            var go = Box(parent, centerFt, sizeFt, tint, keepCollider, animate: animate, solid: solid);
            float rep = ProcTex.RepeatMeters(kind);
            Vector2 tiling = Vector2.one;
            if (rep > 0)
            {
                var m = sizeFt * FT;
                tiling = wallTiling ? new Vector2(Mathf.Max(m.x, m.z) / rep, m.y / rep) : new Vector2(m.x / rep, m.z / rep);
            }
            go.GetComponent<Renderer>().sharedMaterial = TexMat(kind, tint, tiling, smooth);
            return go;
        }

        GameObject Prim(PrimitiveType type, Transform parent, Vector3 centerFt, Vector3 sizeFt, Material mat)
        {
            var go = GameObject.CreatePrimitive(type);
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = centerFt * FT;
            // Unity's cylinder is 2 units tall
            go.transform.localScale = type == PrimitiveType.Cylinder ? new Vector3(sizeFt.x, sizeFt.y / 2f, sizeFt.z) * FT : sizeFt * FT;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            if (animateBuild && !PlanMode) go.AddComponent<BuildIn>().Delay = delay;
            return go;
        }

        GameObject Cyl(Transform p, Vector3 c, float diameter, float height, Color col, bool glow = false) =>
            Prim(PrimitiveType.Cylinder, p, c, new Vector3(diameter, height, diameter), Mat(col, emissive: glow, smooth: 0.4f));

        GameObject Ball(Transform p, Vector3 c, Vector3 size, Color col, bool glow = false) =>
            Prim(PrimitiveType.Sphere, p, c, size, Mat(col, emissive: glow, smooth: 0.3f));

        // ───────────── Surfaces ─────────────

        static ProcTex.Kind FloorKind(string kind) => kind switch
        {
            "kitchen" or "laundry" or "mudroom" or "pantry" => ProcTex.Kind.LargeTile,
            "bath" => ProcTex.Kind.Tile,
            "garage" => ProcTex.Kind.Concrete,
            "patio" => ProcTex.Kind.Stone,
            "deck" => ProcTex.Kind.DarkWood,
            "bedroom" or "game" or "media" => ProcTex.Kind.Carpet,
            _ => ProcTex.Kind.Wood,
        };

        static Color FloorTint(string kind) => kind switch
        {
            "bedroom" => new Color(0.86f, 0.84f, 0.82f),
            "game" => new Color(0.74f, 0.76f, 0.82f),
            "media" => new Color(0.62f, 0.60f, 0.64f),
            _ => Color.white,
        };

        /// <summary>Baseboard along the bottom of a wall run (both faces).</summary>
        void Baseboard(Transform parent, Vector3 centerFt, Vector3 sizeFt, bool horiz)
        {
            var c = centerFt; c.y = c.y - sizeFt.y / 2f + 0.25f;
            var s = horiz ? new Vector3(sizeFt.x, 0.5f, sizeFt.z + 0.12f) : new Vector3(sizeFt.x + 0.12f, 0.5f, sizeFt.z);
            Box(parent, c, s, new Color(0.93f, 0.91f, 0.88f));
        }

        /// <summary>Ceiling fixture and a warm light, only when ceilings are shown (walkthroughs).</summary>
        void CeilingLight(Transform p, Room r, float y)
        {
            if (!ShowRoof || PlanMode || r.Kind == "garage" || r.Kind == "stair" || r.Kind == "closet") return;
            float cx = (float)(r.X + r.W / 2), cz = (float)(r.Z + r.D / 2), h = (float)r.Ceiling;
            Cyl(p, new Vector3(cx, y + h - 0.15f, cz), 1.6f, 0.25f, new Color(1f, 0.95f, 0.85f), glow: true);
            var lg = new GameObject($"Light {r.Name}").AddComponent<Light>();
            lg.transform.SetParent(p, false);
            lg.transform.localPosition = new Vector3(cx, y + h - 1.2f, cz) * FT;
            lg.type = LightType.Point;
            lg.range = Mathf.Max((float)r.W, (float)r.D) * FT * 1.1f;
            lg.intensity = 0.55f;
            lg.color = new Color(1f, 0.92f, 0.80f);
            lg.shadows = LightShadows.None;
        }

        // ───────────── Furniture kit ─────────────

        void Rug(Transform p, float cx, float cz, float w, float d, float y)
        {
            var go = Box(p, new Vector3(cx, y + 0.05f, cz), new Vector3(w, 0.08f, d), Color.white);
            go.GetComponent<Renderer>().sharedMaterial = TexMat(ProcTex.Kind.Rug, Color.white, Vector2.one, 0.05f);
        }

        void Sofa(Transform p, float cx, float cz, float w, float y, int facing, Color fabric)
        {
            float back = cz - facing * 1.2f;
            TBox(p, new Vector3(cx, y + 0.9f, cz), new Vector3(w, 1.2f, 3f), ProcTex.Kind.Fabric, fabric);
            TBox(p, new Vector3(cx, y + 2.0f, back), new Vector3(w, 2.2f, 0.7f), ProcTex.Kind.Fabric, fabric);
            TBox(p, new Vector3(cx - w / 2 + 0.3f, y + 1.5f, cz), new Vector3(0.6f, 1.6f, 3f), ProcTex.Kind.Fabric, fabric * 0.95f);
            TBox(p, new Vector3(cx + w / 2 - 0.3f, y + 1.5f, cz), new Vector3(0.6f, 1.6f, 3f), ProcTex.Kind.Fabric, fabric * 0.95f);
            int n = Mathf.Max(2, Mathf.RoundToInt((w - 1.2f) / 2.4f));
            float cw = (w - 1.2f) / n;
            for (int i = 0; i < n; i++)
                TBox(p, new Vector3(cx - w / 2 + 0.6f + cw * (i + 0.5f), y + 1.65f, cz + facing * 0.2f), new Vector3(cw - 0.12f, 0.5f, 2.3f), ProcTex.Kind.Fabric, fabric * 1.08f);
            Box(p, new Vector3(cx - w / 2 + 1.4f, y + 2.3f, back + facing * 0.55f), new Vector3(1.2f, 1.1f, 0.4f), Terracotta);
            Box(p, new Vector3(cx + w / 2 - 1.4f, y + 2.3f, back + facing * 0.55f), new Vector3(1.2f, 1.1f, 0.4f), new Color(0.93f, 0.78f, 0.45f));
        }

        void Table(Transform p, float cx, float cz, float w, float d, float topY, float y, ProcTex.Kind wood = ProcTex.Kind.Wood, Color? tint = null)
        {
            TBox(p, new Vector3(cx, topY, cz), new Vector3(w, 0.18f, d), wood, tint ?? Color.white, smooth: 0.45f);
            float lh = topY - y;
            foreach (var (sx, sz) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
                Box(p, new Vector3(cx + sx * (w / 2 - 0.3f), y + lh / 2, cz + sz * (d / 2 - 0.3f)), new Vector3(0.22f, lh, 0.22f), Walnut);
        }

        void Chair(Transform p, float cx, float cz, float y, int facingZ, Color c)
        {
            Box(p, new Vector3(cx, y + 1.5f, cz), new Vector3(1.4f, 0.15f, 1.4f), c);
            Box(p, new Vector3(cx, y + 2.6f, cz - facingZ * 0.65f), new Vector3(1.4f, 2.0f, 0.12f), c);
            foreach (var (sx, sz) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
                Box(p, new Vector3(cx + sx * 0.6f, y + 0.75f, cz + sz * 0.6f), new Vector3(0.12f, 1.5f, 0.12f), Walnut);
        }

        void Plant(Transform p, float x, float z, float y, float scale = 1f)
        {
            Cyl(p, new Vector3(x, y + 0.8f * scale, z), 1.2f * scale, 1.6f * scale, Terracotta);
            Ball(p, new Vector3(x, y + 2.6f * scale, z), new Vector3(1.9f, 2.4f, 1.9f) * scale, Leaf);
            Ball(p, new Vector3(x + 0.4f * scale, y + 3.4f * scale, z - 0.2f * scale), new Vector3(1.3f, 1.6f, 1.3f) * scale, Leaf * 1.15f);
        }

        void FloorLamp(Transform p, float x, float z, float y)
        {
            Cyl(p, new Vector3(x, y + 0.05f, z), 1f, 0.1f, Charcoal);
            Cyl(p, new Vector3(x, y + 2.6f, z), 0.12f, 5.2f, Brass);
            Cyl(p, new Vector3(x, y + 5.4f, z), 1.3f, 1f, new Color(1f, 0.93f, 0.80f), glow: true);
        }

        void Art(Transform p, Room r, float y, Color canvas)
        {
            float cx = (float)(r.X + r.W / 2), wz = (float)(r.Z + r.D) - 0.35f;
            float w = Mathf.Min(5f, (float)r.W * 0.35f);
            Box(p, new Vector3(cx, y + 5.6f, wz), new Vector3(w + 0.3f, 3.3f, 0.12f), Charcoal);
            Box(p, new Vector3(cx, y + 5.6f, wz - 0.07f), new Vector3(w, 3f, 0.05f), canvas);
            Box(p, new Vector3(cx - w * 0.18f, y + 5.9f, wz - 0.1f), new Vector3(w * 0.35f, 1.4f, 0.02f), Terracotta);
        }

        void BuildFurniture(Transform p, Room r, float y)
        {
            float cx = (float)(r.X + r.W / 2), cz = (float)(r.Z + r.D / 2), w = (float)r.W, d = (float)r.D;
            float x0 = (float)r.X, z0 = (float)r.Z, x1 = x0 + w, z1 = z0 + d;
            CeilingLight(p, r, y);
            switch (r.Kind)
            {
                case "living":
                    Rug(p, cx, cz, w * 0.55f, d * 0.45f, y);
                    Sofa(p, cx, cz + d * 0.24f, Mathf.Min(9f, w * 0.5f), y, -1, new Color(0.62f, 0.66f, 0.72f));
                    Table(p, cx, cz - 0.4f, 4.2f, 2.2f, y + 1.4f, y, ProcTex.Kind.DarkWood);
                    Box(p, new Vector3(cx, y + 1f, z0 + 1.1f), new Vector3(Mathf.Min(7f, w * 0.4f), 2f, 1.4f), Walnut);
                    Box(p, new Vector3(cx, y + 4f, z0 + 0.6f), new Vector3(Mathf.Min(5.5f, w * 0.32f), 3.1f, 0.15f), new Color(0.08f, 0.08f, 0.10f));
                    FloorLamp(p, x0 + 1.4f, z1 - 1.4f, y);
                    Plant(p, x1 - 1.4f, z1 - 1.4f, y);
                    break;
                case "kitchen":
                    // perimeter cabinets with marble top, uppers, fridge and range
                    TBox(p, new Vector3(cx, y + 1.45f, z0 + 1.2f), new Vector3(w - 1f, 2.9f, 2.1f), ProcTex.Kind.Wood, new Color(0.92f, 0.92f, 0.90f));
                    TBox(p, new Vector3(cx, y + 3.0f, z0 + 1.25f), new Vector3(w - 0.9f, 0.15f, 2.3f), ProcTex.Kind.Marble, Color.white, smooth: 0.7f);
                    TBox(p, new Vector3(cx, y + 6.2f, z0 + 0.8f), new Vector3(w - 1f, 2.4f, 1.2f), ProcTex.Kind.Wood, new Color(0.92f, 0.92f, 0.90f));
                    Box(p, new Vector3(x0 + 1.8f, y + 3.3f, z0 + 1.4f), new Vector3(2.8f, 6.6f, 2.5f), Steel, smooth: 0.8f);
                    Box(p, new Vector3(cx + 1.5f, y + 3.1f, z0 + 1.2f), new Vector3(2.5f, 0.06f, 1.9f), Charcoal);
                    Box(p, new Vector3(cx + 1.5f, y + 5.9f, z0 + 1.0f), new Vector3(2.6f, 1.2f, 1.6f), Steel, smooth: 0.8f);
                    // island + stools
                    TBox(p, new Vector3(cx, y + 1.45f, cz + 1f), new Vector3(Mathf.Min(8f, w * 0.45f), 2.9f, 3.2f), ProcTex.Kind.DarkWood, Color.white);
                    TBox(p, new Vector3(cx, y + 3.0f, cz + 1f), new Vector3(Mathf.Min(8f, w * 0.45f) + 0.4f, 0.15f, 3.6f), ProcTex.Kind.Marble, Color.white, smooth: 0.7f);
                    for (int i = -1; i <= 1; i++)
                    {
                        Cyl(p, new Vector3(cx + i * 2.2f, y + 1.3f, cz + 3.6f), 0.18f, 2.6f, Charcoal);
                        Cyl(p, new Vector3(cx + i * 2.2f, y + 2.6f, cz + 3.6f), 1.3f, 0.25f, Oak);
                    }
                    for (int i = -1; i <= 1; i++)
                        Ball(p, new Vector3(cx + i * 2.2f, y + (float)r.Ceiling - 2.2f, cz + 1f), new Vector3(0.9f, 0.7f, 0.9f), new Color(1f, 0.9f, 0.7f), glow: true);
                    break;
                case "dining":
                    Rug(p, cx, cz, Mathf.Min(10f, w * 0.7f), Mathf.Min(8f, d * 0.8f), y);
                    Table(p, cx, cz, Mathf.Min(7f, w * 0.5f), 3.4f, y + 2.45f, y, ProcTex.Kind.DarkWood);
                    for (int i = -1; i <= 1; i += 2)
                    {
                        Chair(p, cx - 1.6f, cz + i * 2.4f, y, -i, Oak);
                        Chair(p, cx + 1.6f, cz + i * 2.4f, y, -i, Oak);
                    }
                    Ball(p, new Vector3(cx, y + (float)r.Ceiling - 2.4f, cz), new Vector3(1.8f, 1f, 1.8f), new Color(1f, 0.9f, 0.7f), glow: true);
                    break;
                case "bedroom":
                case "primary":
                    {
                        float bw = r.Kind == "primary" ? 6.5f : 5f, bz = cz + d * 0.12f;
                        Rug(p, cx, bz - 1.5f, bw + 3f, 6f, y);
                        TBox(p, new Vector3(cx, y + 0.6f, bz), new Vector3(bw + 0.4f, 1.2f, 7f), ProcTex.Kind.DarkWood, Color.white);
                        Box(p, new Vector3(cx, y + 1.5f, bz), new Vector3(bw, 0.8f, 6.7f), Linen);
                        TBox(p, new Vector3(cx, y + 2.0f, bz - 1.0f), new Vector3(bw + 0.15f, 0.25f, 4.6f), ProcTex.Kind.Fabric, r.Kind == "primary" ? new Color(0.84f, 0.62f, 0.52f) : new Color(0.58f, 0.68f, 0.82f));
                        Box(p, new Vector3(cx - bw / 4, y + 2.15f, bz + 2.6f), new Vector3(bw / 2 - 0.3f, 0.6f, 1.2f), Color.white);
                        Box(p, new Vector3(cx + bw / 4, y + 2.15f, bz + 2.6f), new Vector3(bw / 2 - 0.3f, 0.6f, 1.2f), Color.white);
                        TBox(p, new Vector3(cx, y + 2.6f, bz + 3.55f), new Vector3(bw + 0.8f, 5.2f, 0.45f), ProcTex.Kind.Fabric, new Color(0.55f, 0.52f, 0.50f));
                        foreach (var s in new[] { -1, 1 })
                        {
                            float nx = cx + s * (bw / 2 + 1.3f);
                            TBox(p, new Vector3(nx, y + 1f, bz + 2.9f), new Vector3(1.8f, 2f, 1.5f), ProcTex.Kind.Wood, Color.white);
                            Cyl(p, new Vector3(nx, y + 2.6f, bz + 2.9f), 0.2f, 1.2f, Brass);
                            Cyl(p, new Vector3(nx, y + 3.4f, bz + 2.9f), 0.9f, 0.8f, new Color(1f, 0.93f, 0.8f), glow: true);
                        }
                        if (w > 12) Plant(p, x0 + 1.3f, z0 + 1.3f, y, 0.9f);
                        break;
                    }
                case "bath":
                    TBox(p, new Vector3(cx, y + 1.4f, z1 - 1.2f), new Vector3(Mathf.Min(5f, w - 1.2f), 2.8f, 2f), ProcTex.Kind.DarkWood, Color.white);
                    TBox(p, new Vector3(cx, y + 2.9f, z1 - 1.2f), new Vector3(Mathf.Min(5.2f, w - 1f), 0.15f, 2.2f), ProcTex.Kind.Marble, Color.white, smooth: 0.75f);
                    Box(p, new Vector3(cx, y + 3.05f, z1 - 1.2f), new Vector3(1.6f, 0.2f, 1.2f), Color.white, smooth: 0.8f);
                    Box(p, new Vector3(cx, y + 5.3f, z1 - 0.3f), new Vector3(Mathf.Min(4f, w - 1.6f), 2.6f, 0.08f), new Color(0.72f, 0.8f, 0.84f), smooth: 0.95f);
                    if (d > 7)
                    {
                        Box(p, new Vector3(x0 + 1.4f, y + 0.9f, z0 + 2.6f), new Vector3(2.2f, 1.8f, Mathf.Min(5f, d - 3f)), Color.white, smooth: 0.8f);
                        Box(p, new Vector3(x1 - 1.3f, y + 0.8f, z0 + 1.4f), new Vector3(1.4f, 1.6f, 2f), Color.white, smooth: 0.8f);
                    }
                    break;
                case "office":
                    Rug(p, cx, cz, w * 0.6f, d * 0.5f, y);
                    Table(p, cx, cz - 0.6f, 5f, 2.4f, y + 2.45f, y, ProcTex.Kind.Wood);
                    Box(p, new Vector3(cx, y + 3.4f, cz - 1.3f), new Vector3(2.4f, 1.5f, 0.12f), Charcoal);
                    Cyl(p, new Vector3(cx, y + 0.9f, cz + 1.2f), 0.3f, 1.8f, Charcoal);
                    Box(p, new Vector3(cx, y + 1.9f, cz + 1.2f), new Vector3(1.8f, 0.3f, 1.8f), Charcoal);
                    Box(p, new Vector3(cx, y + 3f, cz + 2f), new Vector3(1.8f, 2f, 0.25f), Charcoal);
                    {
                        float bx = x0 + 0.9f;
                        TBox(p, new Vector3(bx, y + 3.5f, cz), new Vector3(1.4f, 7f, Mathf.Min(6f, d - 2f)), ProcTex.Kind.DarkWood, Color.white);
                        var bookCols = new[] { Terracotta, new Color(0.25f, 0.42f, 0.62f), new Color(0.9f, 0.76f, 0.4f), Leaf, Linen };
                        for (int shelf = 0; shelf < 3; shelf++)
                            for (int b = 0; b < 5; b++)
                                Box(p, new Vector3(bx + 0.75f, y + 1.6f + shelf * 2f, cz - 2f + b * 0.9f), new Vector3(0.1f, 1.4f, 0.7f), bookCols[(b + shelf) % bookCols.Length]);
                    }
                    Plant(p, x1 - 1.3f, z1 - 1.3f, y, 0.8f);
                    break;
                case "game":
                    TBox(p, new Vector3(cx, y + 2.6f, cz), new Vector3(4.6f, 0.4f, 8f), ProcTex.Kind.Carpet, new Color(0.22f, 0.48f, 0.36f));
                    TBox(p, new Vector3(cx, y + 1.3f, cz), new Vector3(4f, 2.4f, 7.4f), ProcTex.Kind.DarkWood, Color.white);
                    for (int i = -1; i <= 1; i++)
                        Cyl(p, new Vector3(cx, y + 6.5f, cz + i * 2.5f), 1.2f, 0.5f, new Color(1f, 0.9f, 0.7f), glow: true);
                    Sofa(p, cx, z1 - 2f, Mathf.Min(8f, w * 0.5f), y, -1, new Color(0.30f, 0.32f, 0.40f));
                    break;
                case "media":
                    Rug(p, cx, cz, w * 0.7f, d * 0.6f, y);
                    Sofa(p, cx, cz + d * 0.2f, Mathf.Min(11f, w * 0.65f), y, -1, new Color(0.28f, 0.30f, 0.36f));
                    Box(p, new Vector3(cx, y + 4.2f, z0 + 0.6f), new Vector3(w * 0.6f, 3.6f, 0.15f), new Color(0.05f, 0.05f, 0.07f));
                    Box(p, new Vector3(cx, y + 4.2f, z0 + 0.7f), new Vector3(w * 0.56f, 3.2f, 0.02f), new Color(0.25f, 0.35f, 0.55f), smooth: 0.9f);
                    break;
                case "garage":
                    {
                        int cars = Mathf.Clamp(Mathf.FloorToInt(w / 10.5f), 1, 3);
                        var cols = new[] { new Color(0.75f, 0.18f, 0.16f), new Color(0.16f, 0.18f, 0.22f), new Color(0.88f, 0.88f, 0.86f) };
                        for (int i = 0; i < cars; i++)
                        {
                            float x = x0 + w * (i + 0.5f) / cars;
                            Box(p, new Vector3(x, y + 1.7f, cz), new Vector3(6f, 1.8f, 14.5f), cols[i], smooth: 0.75f);
                            Box(p, new Vector3(x, y + 3.2f, cz + 0.8f), new Vector3(5.2f, 1.5f, 7f), new Color(0.55f, 0.65f, 0.72f), transparent: true, alpha: 0.8f);
                            foreach (var (sx, sz) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
                            {
                                var wheel = Cyl(p, new Vector3(x + sx * 2.9f, y + 1.1f, cz + sz * 4.6f), 2.2f, 0.7f, Charcoal);
                                wheel.transform.localRotation = Quaternion.Euler(0, 0, 90);
                            }
                        }
                        TBox(p, new Vector3(cx, y + 1.5f, z1 - 1.2f), new Vector3(Mathf.Min(10f, w * 0.4f), 3f, 2f), ProcTex.Kind.Wood, Color.white);
                        break;
                    }
                case "laundry":
                    Box(p, new Vector3(x0 + 1.6f, y + 1.6f, z1 - 1.5f), new Vector3(2.4f, 3.2f, 2.4f), Color.white, smooth: 0.7f);
                    Box(p, new Vector3(x0 + 4.2f, y + 1.6f, z1 - 1.5f), new Vector3(2.4f, 3.2f, 2.4f), Color.white, smooth: 0.7f);
                    break;
                case "hall":
                    if (w > d) Rug(p, cx, cz, w * 0.75f, Mathf.Min(2.6f, d * 0.6f), y);
                    else Rug(p, cx, cz, Mathf.Min(2.6f, w * 0.6f), d * 0.75f, y);
                    break;
                case "stair":
                    {
                        const int steps = 12;
                        bool alongZ = d >= w;
                        for (int i = 0; i < steps; i++)
                        {
                            float t = (i + 0.5f) / steps;
                            var size = alongZ ? new Vector3(w * 0.8f, (i + 1) * 0.75f, d / steps) : new Vector3(w / steps, (i + 1) * 0.75f, d * 0.8f);
                            var pos = alongZ ? new Vector3(cx, y + size.y / 2f, z0 + d * t) : new Vector3(x0 + w * t, y + size.y / 2f, cz);
                            TBox(p, pos, size, ProcTex.Kind.Wood, Color.white);
                        }
                        break;
                    }
                case "patio":
                    Table(p, cx - 3, cz, 6f, 3.5f, y + 2.4f, y, ProcTex.Kind.DarkWood);
                    for (int i = -1; i <= 1; i += 2) Chair(p, cx - 3 + i * 1.6f, cz + 2.6f, y, -1, Charcoal);
                    Box(p, new Vector3(cx + 5, y + 0.8f, cz), new Vector3(2.6f, 1.6f, 6f), Linen);
                    Plant(p, x0 + 1.5f, z0 + 1.5f, y);
                    Plant(p, x1 - 1.5f, z0 + 1.5f, y, 0.8f);
                    break;
                case "deck":
                    Plant(p, cx - 3, cz, y, 1.1f);
                    Plant(p, cx + 6, cz + 4, y, 0.9f);
                    Box(p, new Vector3(cx + 4, y + 0.8f, cz - 4), new Vector3(7f, 1.6f, 2.6f), Linen);
                    Box(p, new Vector3(cx - 4, y + 0.8f, cz - 4), new Vector3(2.6f, 1.6f, 6f), Linen);
                    break;
            }
            if ((r.Kind == "living" || r.Kind == "primary" || r.Kind == "dining" || r.Kind == "office") && w >= 10)
                Art(p, r, y, r.Kind == "office" ? new Color(0.85f, 0.88f, 0.92f) : new Color(0.93f, 0.89f, 0.80f));
        }
    }
}

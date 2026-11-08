using System.Collections.Generic;
using System.Linq;
using Atlas;
using UnityEngine;

namespace Cascade
{
    /// <summary>
    /// Furnishes the twin so the walk-through reads as a real office floor, in the style of
    /// the team's Keystone branch: lit, procedurally textured surfaces, furniture per room type,
    /// framed windows on the facade and door casings. Everything is generated at runtime under
    /// the twin, in plan meters, so it works life-size and on the tabletop.
    /// </summary>
    public static class InteriorDecor
    {
        static Color Hex(string h) => AtlasStyle.Hex(h);
        static readonly Color Frame = Hex("#2E3034"), Metal = Hex("#9AA5B1"), Dark = Hex("#2B2F36"), Leaf = Hex("#5E9E5B"), Pot = Hex("#A1887F");

        public static Transform Build(TwinView twin, Transform parent, List<Net.Room> rooms, List<Net.Asset> assets, Vector2 size)
        {
            var root = new GameObject("Interior").transform;
            root.SetParent(parent, false);
            var avoid = assets.Select(a => new Vector2(a.X, a.Z)).ToList();
            foreach (var r in rooms)
            {
                avoid.Add(new Vector2(r.DoorX, r.DoorZ));
                switch (r.Kind)
                {
                    case "office": Office(twin, root, r, avoid); break;
                    case "server": Server(twin, root, r); break;
                    case "mech": Mech(twin, root, r, assets); break;
                    case "common": if (r.Id == "LOBBY") Lobby(twin, root, r); else Commons(twin, root, r); break;
                }
                if (r.Kind != "corridor") Casing(twin, root, r);
            }
            Windows(twin, root, rooms, size);
            Casing(twin, root, null, new Vector2(0, 8)); // entrance
            return root;
        }

        // ---------------- primitives (plan meters → twin local)

        static GameObject Box(TwinView t, Transform p, float x, float z, float y, Vector3 s, Material m, Color c, float repeat = 0f)
            => CascadeStyle.LitBox(p, "Decor", t.L(x, z, y), s, m, c, repeat);

        static GameObject Prim(TwinView t, Transform p, PrimitiveType type, float x, float z, float y, Vector3 s, Material m, Color c, Quaternion? rot = null)
        {
            var go = GameObject.CreatePrimitive(type);
            Object.Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(p, false);
            go.transform.localPosition = t.L(x, z, y);
            go.transform.localScale = s;
            if (rot.HasValue) go.transform.localRotation = rot.Value;
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = m;
            CascadeStyle.Tint(r, c);
            return go;
        }

        static Material Plain => CascadeStyle.Lit(null, 0.25f);
        static bool Clear(List<Vector2> avoid, float x, float z, float radius) => avoid.All(a => Vector2.Distance(a, new Vector2(x, z)) > radius);

        static void Plant(TwinView t, Transform p, float x, float z, float scale = 1f)
        {
            Prim(t, p, PrimitiveType.Cylinder, x, z, 0.22f * scale, new Vector3(0.36f, 0.22f, 0.36f) * scale, Plain, Pot);
            Prim(t, p, PrimitiveType.Sphere, x, z, 0.75f * scale, new Vector3(0.62f, 0.78f, 0.62f) * scale, CascadeStyle.Lit(null, 0.05f), Leaf);
            Prim(t, p, PrimitiveType.Sphere, x + 0.12f * scale, z - 0.08f * scale, 1.02f * scale, new Vector3(0.42f, 0.5f, 0.42f) * scale, CascadeStyle.Lit(null, 0.05f), Leaf * 0.9f);
        }

        static void Chair(TwinView t, Transform p, float x, float z, Color fabric)
        {
            Box(t, p, x, z, 0.46f, new Vector3(0.48f, 0.08f, 0.48f), CascadeStyle.Lit(ProcTex.Kind.Fabric), fabric, 0.5f);
            Box(t, p, x, z + 0.22f, 0.78f, new Vector3(0.48f, 0.56f, 0.07f), CascadeStyle.Lit(ProcTex.Kind.Fabric), fabric, 0.5f);
            Box(t, p, x, z, 0.23f, new Vector3(0.06f, 0.42f, 0.06f), Plain, Dark);
        }

        // ---------------- rooms

        static void Office(TwinView t, Transform p, Net.Room r, List<Vector2> avoid)
        {
            var wood = CascadeStyle.Lit(ProcTex.Kind.Wood, 0.3f);
            var screen = CascadeStyle.LitGlow(Hex("#A8D1FF"), 0.9f);
            var fabric = r.Id switch { "S100" => Hex("#5B6B8C"), "S200" => Hex("#8C6B5B"), "S300" => Hex("#4F7A6A"), _ => Hex("#7A5B8C") };
            int n = 0;
            for (float z = r.Z0 + 1.6f; z <= r.Z1 - 1.4f; z += 2.3f)
                for (float x = r.X0 + 1.4f; x <= r.X1 - 1.2f; x += 2.2f)
                {
                    if (!Clear(avoid, x, z, 1.7f)) continue;
                    Box(t, p, x, z, 0.74f, new Vector3(1.4f, 0.04f, 0.72f), wood, Color.white, 1.6f);
                    Box(t, p, x - 0.62f, z, 0.37f, new Vector3(0.05f, 0.72f, 0.66f), Plain, Dark);
                    Box(t, p, x + 0.62f, z, 0.37f, new Vector3(0.05f, 0.72f, 0.66f), Plain, Dark);
                    Box(t, p, x, z - 0.2f, 1.02f, new Vector3(0.62f, 0.38f, 0.03f), Plain, Dark);          // monitor
                    Box(t, p, x, z - 0.217f, 1.03f, new Vector3(0.56f, 0.32f, 0.01f), screen, Hex("#A8D1FF")); // screen
                    Box(t, p, x, z - 0.18f, 0.8f, new Vector3(0.06f, 0.14f, 0.06f), Plain, Dark);
                    Chair(t, p, x, z + 0.62f, fabric);
                    n++;
                }
            Plant(t, p, r.X0 + 0.5f, r.Z0 + 0.5f);
            Plant(t, p, r.X1 - 0.5f, r.Z1 - 0.5f, 1.15f);
            if (r.X1 - r.X0 > 6) Box(t, p, (r.X0 + r.X1) / 2, r.Z0 < 7 ? r.Z0 + 0.04f : r.Z1 - 0.04f, 1.5f, new Vector3(2.2f, 1.1f, 0.03f), CascadeStyle.Lit(null, 0.6f), Hex("#F8FAFC")); // whiteboard
        }

        static void Server(TwinView t, Transform p, Net.Room r)
        {
            var rack = CascadeStyle.Lit(null, 0.45f);
            var ledG = CascadeStyle.LitGlow(Hex("#34D399"), 0.9f);
            var ledB = CascadeStyle.LitGlow(Hex("#60A5FA"), 0.9f);
            float aisle = (r.X0 + r.X1) / 2;
            foreach (var side in new[] { -1, 1 })
            {
                float cx = aisle + side * 1.3f;
                for (float z = r.Z0 + 0.7f; z <= r.Z1 - 1.6f; z += 0.64f)
                {
                    Box(t, p, cx, z, 1.0f, new Vector3(1.0f, 2.0f, 0.6f), rack, Dark);
                    float face = cx - side * 0.505f;
                    for (int k = 0; k < 6; k++)
                        Box(t, p, face, z + ((k % 2) - 0.5f) * 0.3f, 0.4f + k * 0.26f, new Vector3(0.01f, 0.025f, 0.08f), k % 3 == 0 ? ledB : ledG, k % 3 == 0 ? Hex("#60A5FA") : Hex("#34D399"));
                }
            }
            // raised-floor tiles are the room floor; overhead cable tray
            foreach (var side in new[] { -1, 1 }) Box(t, p, aisle + side * 1.3f, (r.Z0 + r.Z1) / 2 - 0.5f, 2.5f, new Vector3(0.3f, 0.05f, r.Z1 - r.Z0 - 2f), Plain, Metal);
        }

        static void Mech(TwinView t, Transform p, Net.Room r, List<Net.Asset> assets)
        {
            var pipe = CascadeStyle.Lit(null, 0.55f);
            var concrete = CascadeStyle.Lit(ProcTex.Kind.Concrete, 0.1f);
            foreach (var a in assets.Where(a => a.RoomId == r.Id && a.Kind == "chiller"))
                Box(t, p, a.X, a.Z, 0.08f, new Vector3(2.0f, 0.12f, 1.6f), concrete, Color.white, 1.5f); // housekeeping pads
            // chilled-water headers along the east wall and the corridor wall
            Prim(t, p, PrimitiveType.Cylinder, r.X1 - 0.35f, (r.Z0 + r.Z1) / 2, 2.3f, new Vector3(0.22f, (r.Z1 - r.Z0 - 0.6f) / 2, 0.22f), pipe, Hex("#4F81BD"), Quaternion.Euler(90, 0, 0));
            Prim(t, p, PrimitiveType.Cylinder, (r.X0 + r.X1) / 2, r.Z1 - 0.35f, 2.55f, new Vector3(0.18f, (r.X1 - r.X0 - 0.6f) / 2, 0.18f), pipe, Metal, Quaternion.Euler(0, 0, 90));
            for (float z = r.Z0 + 1.2f; z < r.Z1 - 0.8f; z += 1.8f)
                Prim(t, p, PrimitiveType.Cylinder, r.X1 - 0.35f, z, 2.3f, new Vector3(0.3f, 0.06f, 0.3f), pipe, Hex("#DC2626"), Quaternion.Euler(90, 0, 0)); // valves
            Box(t, p, r.X0 + 0.9f, r.Z1 - 1.0f, 0.45f, new Vector3(0.9f, 0.9f, 0.7f), Plain, Hex("#64748B")); // pump skid
        }

        static void Lobby(TwinView t, Transform p, Net.Room r)
        {
            var marble = CascadeStyle.Lit(ProcTex.Kind.Marble, 0.7f);
            var dwood = CascadeStyle.Lit(ProcTex.Kind.DarkWood, 0.35f);
            float cx = (r.X0 + r.X1) / 2;
            Box(t, p, cx + 0.6f, r.Z0 + 2.3f, 0.52f, new Vector3(2.4f, 1.04f, 0.6f), dwood, Color.white, 1.6f);     // reception desk
            Box(t, p, cx + 0.6f, r.Z0 + 2.3f, 1.06f, new Vector3(2.5f, 0.05f, 0.7f), marble, Color.white, 1.2f);
            Box(t, p, r.X0 + 0.7f, r.Z1 - 1.6f, 0.25f, new Vector3(0.9f, 0.45f, 2.2f), CascadeStyle.Lit(ProcTex.Kind.Fabric), Hex("#3F5E77"), 0.6f); // sofa
            Box(t, p, r.X0 + 0.3f, r.Z1 - 1.6f, 0.62f, new Vector3(0.22f, 0.5f, 2.2f), CascadeStyle.Lit(ProcTex.Kind.Fabric), Hex("#3F5E77"), 0.6f);
            Box(t, p, r.X0 + 1.7f, r.Z1 - 1.6f, 0.22f, new Vector3(0.7f, 0.44f, 1.1f), marble, Color.white, 1.2f); // coffee table
            Box(t, p, cx, r.Z1 - 1.6f, 0.012f, new Vector3(2.6f, 0.012f, 2.4f), CascadeStyle.Lit(ProcTex.Kind.Rug, 0.0f), Color.white);
            Plant(t, p, r.X1 - 0.5f, r.Z1 - 0.5f, 1.3f);
            Plant(t, p, r.X0 + 0.5f, r.Z0 + 0.6f, 1.1f);
            var sign = TwinView.MakeLabel(p, "<b>HARBOR POINT</b>\n<size=55%>Floor 3</size>", 3.0f, Hex("#1F2A37"), t.L(cx, r.Z1 - 0.1f, 2.05f), new Vector2(4.5f, 1.4f), false, false);
            sign.transform.localRotation = Quaternion.LookRotation(Vector3.forward);
        }

        static void Commons(TwinView t, Transform p, Net.Room r)
        {
            var wood = CascadeStyle.Lit(ProcTex.Kind.Wood, 0.35f);
            var marble = CascadeStyle.Lit(ProcTex.Kind.Marble, 0.7f);
            var dwood = CascadeStyle.Lit(ProcTex.Kind.DarkWood, 0.35f);
            foreach (var (x, z) in new[] { (r.X0 + 1.6f, r.Z0 + 2.4f), (r.X1 - 1.6f, r.Z0 + 2.4f), (r.X0 + 1.6f, r.Z0 + 4.6f), (r.X1 - 1.6f, r.Z0 + 4.6f) })
            {
                Prim(t, p, PrimitiveType.Cylinder, x, z, 0.74f, new Vector3(1.0f, 0.02f, 1.0f), wood, Color.white);
                Prim(t, p, PrimitiveType.Cylinder, x, z, 0.37f, new Vector3(0.08f, 0.36f, 0.08f), Plain, Dark);
                foreach (var (dx, dz) in new[] { (0f, 0.75f), (0f, -0.75f), (0.75f, 0f), (-0.75f, 0f) })
                {
                    Box(t, p, x + dx, z + dz, 0.45f, new Vector3(0.42f, 0.06f, 0.42f), wood, Color.white, 0.8f);
                    Box(t, p, x + dx, z + dz, 0.22f, new Vector3(0.05f, 0.44f, 0.05f), Plain, Dark);
                }
            }
            // kitchenette along the north wall
            Box(t, p, (r.X0 + r.X1) / 2 + 0.3f, r.Z1 - 0.35f, 0.45f, new Vector3(3.6f, 0.9f, 0.6f), dwood, Color.white, 1.6f);
            Box(t, p, (r.X0 + r.X1) / 2 + 0.3f, r.Z1 - 0.35f, 0.92f, new Vector3(3.7f, 0.04f, 0.66f), marble, Color.white, 1.2f);
            Box(t, p, r.X1 - 0.45f, r.Z1 - 0.4f, 0.95f, new Vector3(0.75f, 1.9f, 0.7f), CascadeStyle.Lit(null, 0.6f), Hex("#E5E7EB")); // fridge
            Plant(t, p, r.X0 + 0.5f, r.Z1 - 0.5f, 1.2f);
        }

        // ---------------- facade and doors

        static void Windows(TwinView t, Transform p, List<Net.Room> rooms, Vector2 size)
        {
            var glass = CascadeStyle.LitGlow(Hex("#CFE6F7"), 0.75f);
            var frame = CascadeStyle.Lit(null, 0.4f);
            foreach (var r in rooms)
            {
                if (r.Kind is "server" or "mech" or "corridor") continue;
                void Run(bool alongX, float fixedC, float a0, float a1, float inward)
                {
                    for (float c = a0 + 1.6f; c <= a1 - 1.2f; c += 3.0f)
                    {
                        float x = alongX ? c : fixedC + inward, z = alongX ? fixedC + inward : c;
                        var gs = alongX ? new Vector3(1.8f, 1.35f, 0.02f) : new Vector3(0.02f, 1.35f, 1.8f);
                        Box(t, p, x, z, 1.55f, gs, glass, Hex("#CFE6F7"));
                        foreach (var dy in new[] { -0.7f, 0.7f })
                            Box(t, p, x, z, 1.55f + dy, alongX ? new Vector3(1.9f, 0.07f, 0.06f) : new Vector3(0.06f, 0.07f, 1.9f), frame, Frame);
                        foreach (var dc in new[] { -0.93f, 0f, 0.93f })
                            Box(t, p, alongX ? x + dc : x, alongX ? z : z + dc, 1.55f, alongX ? new Vector3(0.06f, 1.45f, 0.06f) : new Vector3(0.06f, 1.45f, 0.06f), frame, Frame);
                        Box(t, p, alongX ? x : x + inward * 0.6f, alongX ? z + inward * 0.6f : z, 0.82f, alongX ? new Vector3(1.95f, 0.04f, 0.2f) : new Vector3(0.2f, 0.04f, 1.95f), CascadeStyle.Lit(ProcTex.Kind.Marble, 0.6f), Color.white); // sill
                    }
                }
                if (Mathf.Abs(r.Z0) < 0.01f) Run(true, 0, r.X0, r.X1, 0.09f);
                if (Mathf.Abs(r.Z1 - size.y) < 0.01f) Run(true, size.y, r.X0, r.X1, -0.09f);
                if (Mathf.Abs(r.X0) < 0.01f) Run(false, 0, r.Z0, r.Z1, 0.09f);
                if (Mathf.Abs(r.X1 - size.x) < 0.01f) Run(false, size.x, r.Z0, r.Z1, -0.09f);
            }
        }

        static void Casing(TwinView t, Transform p, Net.Room r, Vector2? at = null)
        {
            var d = at ?? new Vector2(r.DoorX, r.DoorZ);
            bool alongX = r == null ? false : Mathf.Abs(d.y - r.Z0) < 0.01f || Mathf.Abs(d.y - r.Z1) < 0.01f;
            var wood = CascadeStyle.Lit(ProcTex.Kind.DarkWood, 0.35f);
            foreach (var s in new[] { -0.62f, 0.62f })
                Box(t, p, alongX ? d.x + s : d.x, alongX ? d.y : d.y + s, 1.05f, alongX ? new Vector3(0.08f, 2.1f, 0.2f) : new Vector3(0.2f, 2.1f, 0.08f), wood, Color.white, 1.6f);
            Box(t, p, d.x, d.y, 2.14f, alongX ? new Vector3(1.32f, 0.1f, 0.2f) : new Vector3(0.2f, 0.1f, 1.32f), wood, Color.white, 1.6f);
        }
    }
}

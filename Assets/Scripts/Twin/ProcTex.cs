using System.Collections.Generic;
using UnityEngine;

namespace CbreTwin
{
    /// <summary>
    /// Runtime-generated surface textures (no image assets needed): oak planks, ceramic tile,
    /// carpet, concrete, stone pavers, marble, painted plaster, fabric, grass and a rug weave.
    /// Each is built once and cached; all tile seamlessly.
    /// </summary>
    public static class ProcTex
    {
        public enum Kind { Wood, DarkWood, Tile, LargeTile, Carpet, Concrete, Stone, Marble, Plaster, Fabric, Grass, Rug }

        static readonly Dictionary<Kind, Texture2D> cache = new();

        /// <summary>How many metres one repeat of the texture covers.</summary>
        public static float RepeatMeters(Kind k) => k switch
        {
            Kind.Wood or Kind.DarkWood => 1.6f,
            Kind.Tile => 0.6f,
            Kind.LargeTile => 1.2f,
            Kind.Stone => 1.5f,
            Kind.Grass => 3f,
            Kind.Plaster => 2.5f,
            Kind.Rug => 0f, // stretched over the rug
            _ => 1.5f,
        };

        public static Texture2D Get(Kind k)
        {
            if (cache.TryGetValue(k, out var t) && t != null) return t;
            t = k switch
            {
                Kind.Wood => Planks(new Color(0.78f, 0.62f, 0.45f), 0.10f, 11),
                Kind.DarkWood => Planks(new Color(0.55f, 0.40f, 0.29f), 0.12f, 23),
                Kind.Tile => Tiles(256, 4, new Color(0.93f, 0.92f, 0.90f), new Color(0.72f, 0.70f, 0.67f), 0.03f),
                Kind.LargeTile => Tiles(256, 2, new Color(0.86f, 0.85f, 0.83f), new Color(0.66f, 0.65f, 0.63f), 0.04f),
                Kind.Carpet => Noise(256, new Color(0.80f, 0.77f, 0.73f), 0.07f, 64f, 3),
                Kind.Concrete => Noise(256, new Color(0.70f, 0.69f, 0.67f), 0.10f, 12f, 5),
                Kind.Stone => Pavers(new Color(0.80f, 0.77f, 0.71f)),
                Kind.Marble => Marble(new Color(0.95f, 0.94f, 0.93f)),
                Kind.Plaster => Noise(256, new Color(0.98f, 0.97f, 0.95f), 0.025f, 18f, 7),
                Kind.Fabric => Weave(new Color(0.62f, 0.60f, 0.58f)),
                Kind.Grass => Noise(256, new Color(0.55f, 0.72f, 0.45f), 0.14f, 40f, 9),
                Kind.Rug => Rug(),
                _ => Noise(64, Color.gray, 0.05f, 8f, 1),
            };
            t.wrapMode = k == Kind.Rug ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
            t.filterMode = FilterMode.Trilinear;
            t.anisoLevel = 4;
            t.name = k.ToString();
            cache[k] = t;
            return t;
        }

        static Texture2D New(int size) => new(size, size, TextureFormat.RGBA32, true);

        static float Fbm(float x, float y, int seed)
        {
            float v = 0, a = 0.5f, f = 1;
            for (int i = 0; i < 4; i++)
            {
                v += a * Mathf.PerlinNoise(x * f + seed * 13.1f, y * f + seed * 7.7f);
                a *= 0.5f; f *= 2.03f;
            }
            return v;
        }

        static Texture2D Planks(Color baseC, float variation, int seed)
        {
            const int S = 512, rows = 8;
            var tex = New(S);
            var px = new Color[S * S];
            var rng = new System.Random(seed);
            var tone = new float[rows * 3];
            var offset = new int[rows];
            for (int i = 0; i < tone.Length; i++) tone[i] = (float)(rng.NextDouble() * 2 - 1) * variation;
            for (int i = 0; i < rows; i++) offset[i] = rng.Next(0, S);
            int rowH = S / rows;
            for (int y = 0; y < S; y++)
            {
                int row = y / rowH;
                float inRow = (y % rowH) / (float)rowH;
                for (int x = 0; x < S; x++)
                {
                    int xs = (x + offset[row]) % S;
                    int plank = xs / (S / 3);
                    float t = tone[row * 3 + plank];
                    // long grain lines along the plank with a little waviness
                    float grain = Mathf.PerlinNoise(xs * 0.012f + row * 3.1f, y * 0.35f + plank * 5f);
                    float rings = Mathf.Sin((y * 0.9f + Mathf.PerlinNoise(xs * 0.01f, row) * 18f) * 0.6f) * 0.5f + 0.5f;
                    float shade = 1f + t + (grain - 0.5f) * 0.16f + (rings - 0.5f) * 0.05f;
                    // seams between rows and plank ends
                    bool seam = inRow < 0.025f || (xs % (S / 3)) < 2;
                    if (seam) shade *= 0.72f;
                    var c = baseC * shade;
                    c.a = 1;
                    px[y * S + x] = c;
                }
            }
            tex.SetPixels(px);
            tex.Apply(true);
            return tex;
        }

        static Texture2D Tiles(int S, int count, Color tile, Color grout, float variation)
        {
            var tex = New(S);
            var px = new Color[S * S];
            int cell = S / count;
            var rng = new System.Random(count * 31);
            var tone = new float[count * count];
            for (int i = 0; i < tone.Length; i++) tone[i] = (float)(rng.NextDouble() * 2 - 1) * variation;
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    int cx = x % cell, cy = y % cell;
                    bool g = cx < 3 || cy < 3;
                    float n = (Mathf.PerlinNoise(x * 0.05f, y * 0.05f) - 0.5f) * 0.04f;
                    var c = g ? grout : tile * (1f + tone[(y / cell) * count + (x / cell)] + n);
                    if (!g && (cx < 6 || cy < 6)) c *= 0.97f; // bevel
                    c.a = 1;
                    px[y * S + x] = c;
                }
            tex.SetPixels(px);
            tex.Apply(true);
            return tex;
        }

        static Texture2D Noise(int S, Color baseC, float amount, float scale, int seed)
        {
            var tex = New(S);
            var px = new Color[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    // tileable: blend four offsets of the noise field
                    float u = x / (float)S, v = y / (float)S;
                    float n = Fbm(u * scale, v * scale, seed) * (1 - u) * (1 - v)
                            + Fbm((u - 1) * scale, v * scale, seed) * u * (1 - v)
                            + Fbm(u * scale, (v - 1) * scale, seed) * (1 - u) * v
                            + Fbm((u - 1) * scale, (v - 1) * scale, seed) * u * v;
                    var c = baseC * (1f + (n - 0.5f) * 2f * amount);
                    c.a = 1;
                    px[y * S + x] = c;
                }
            tex.SetPixels(px);
            tex.Apply(true);
            return tex;
        }

        static Texture2D Pavers(Color baseC)
        {
            const int S = 256;
            var tex = New(S);
            var px = new Color[S * S];
            var rng = new System.Random(3);
            var tone = new float[16];
            for (int i = 0; i < tone.Length; i++) tone[i] = (float)(rng.NextDouble() * 2 - 1) * 0.08f;
            for (int y = 0; y < S; y++)
            {
                int row = y / 64;
                for (int x = 0; x < S; x++)
                {
                    int xs = (x + (row % 2) * 64) % S;
                    int col = xs / 128;
                    bool joint = (y % 64) < 3 || (xs % 128) < 3;
                    float n = (Fbm(x * 0.04f, y * 0.04f, 4) - 0.5f) * 0.12f;
                    var c = joint ? baseC * 0.7f : baseC * (1f + tone[(row * 2 + col) % 16] + n);
                    c.a = 1;
                    px[y * S + x] = c;
                }
            }
            tex.SetPixels(px);
            tex.Apply(true);
            return tex;
        }

        static Texture2D Marble(Color baseC)
        {
            const int S = 256;
            var tex = New(S);
            var px = new Color[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float t = Fbm(x * 0.02f, y * 0.02f, 2) * 6f;
                    float vein = Mathf.Pow(Mathf.Abs(Mathf.Sin((x * 0.03f + y * 0.015f + t) * 1.4f)), 0.25f);
                    var c = Color.Lerp(new Color(0.68f, 0.68f, 0.70f), baseC, vein);
                    c.a = 1;
                    px[y * S + x] = c;
                }
            tex.SetPixels(px);
            tex.Apply(true);
            return tex;
        }

        static Texture2D Weave(Color baseC)
        {
            const int S = 128;
            var tex = New(S);
            var px = new Color[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float w = ((x / 2 + y / 2) % 2 == 0) ? 1.04f : 0.96f;
                    float n = (Mathf.PerlinNoise(x * 0.2f, y * 0.2f) - 0.5f) * 0.06f;
                    var c = baseC * (w + n);
                    c.a = 1;
                    px[y * S + x] = c;
                }
            tex.SetPixels(px);
            tex.Apply(true);
            return tex;
        }

        static Texture2D Rug()
        {
            const int S = 256;
            var tex = New(S);
            var px = new Color[S * S];
            var field = new Color(0.86f, 0.79f, 0.70f);
            var band = new Color(0.78f, 0.35f, 0.24f);
            var ink = new Color(0.20f, 0.18f, 0.22f);
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    int edge = Mathf.Min(Mathf.Min(x, S - 1 - x), Mathf.Min(y, S - 1 - y));
                    Color c = edge < 10 ? ink : edge < 22 ? band : edge < 26 ? field * 0.92f : field;
                    if (edge >= 26)
                    {
                        // a soft diamond lattice in the field
                        float d = Mathf.Abs(((x + y) % 48) - 24) + Mathf.Abs(((x - y + 512) % 48) - 24);
                        if (d < 6) c = Color.Lerp(c, band, 0.35f);
                    }
                    c *= 1f + (Mathf.PerlinNoise(x * 0.3f, y * 0.3f) - 0.5f) * 0.08f;
                    c.a = 1;
                    px[y * S + x] = c;
                }
            tex.SetPixels(px);
            tex.Apply(true);
            return tex;
        }
    }
}

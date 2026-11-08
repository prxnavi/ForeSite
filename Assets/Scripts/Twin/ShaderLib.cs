using System.Collections.Generic;
using UnityEngine;

namespace CbreTwin
{
    /// <summary>
    /// All runtime materials are cloned from base materials in Resources/KeystoneMaterials.
    /// A player build (e.g. Meta Quest) only ships shaders that some asset references, so
    /// Shader.Find("Universal Render Pipeline/Lit") returns null there. The base materials
    /// are that reference (and carry the shader variants we use: opaque, transparent,
    /// emissive). They are created by the editor before every build (KeystoneMaterials.cs).
    /// </summary>
    public static class ShaderLib
    {
        public enum Kind { LitOpaque, LitTransparent, LitEmissive, UnlitOpaque, UnlitTransparent, Line }

        public const string Folder = "KeystoneMaterials";
        static readonly Dictionary<Kind, Material> bases = new();

        public static string ShaderName(Kind k) => k switch
        {
            Kind.UnlitOpaque or Kind.UnlitTransparent => "Universal Render Pipeline/Unlit",
            Kind.Line => "Sprites/Default",
            _ => "Universal Render Pipeline/Lit",
        };

        public static Material Create(Kind k)
        {
            if (!bases.TryGetValue(k, out var b) || b == null)
            {
                b = Resources.Load<Material>($"{Folder}/{k}");
                if (b == null)
                {
                    // Editor without the assets yet: build one on the fly.
                    var sh = Shader.Find(ShaderName(k)) ?? Shader.Find("Standard") ?? Shader.Find("Unlit/Color");
                    if (sh == null) { Debug.LogError($"Keystone: no shader for {k}. Run Keystone → Quest → 1 to create the base materials."); sh = Shader.Find("Hidden/InternalErrorShader"); }
                    b = new Material(sh);
                    Configure(b, k);
                }
                bases[k] = b;
            }
            return new Material(b);
        }

        /// <summary>Surface settings for each kind; used when creating the base assets and the fallbacks.</summary>
        public static void Configure(Material m, Kind k)
        {
            bool transparent = k == Kind.LitTransparent || k == Kind.UnlitTransparent;
            if (transparent)
            {
                m.SetFloat("_Surface", 1f);
                m.SetFloat("_Blend", 0f);
                m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                m.SetFloat("_ZWrite", 0f);
                m.SetOverrideTag("RenderType", "Transparent");
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
            if (k == Kind.LitEmissive)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", Color.white);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
        }
    }
}

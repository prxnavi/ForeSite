using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace CbreTwin.EditorTools
{
    /// <summary>
    /// Creates Resources/KeystoneMaterials/*.mat so player builds include the URP shaders
    /// (and the opaque / transparent / emissive variants) the runtime clones from.
    /// Runs on editor load and before every build.
    /// </summary>
    public class KeystoneMaterials : IPreprocessBuildWithReport
    {
        const string Dir = "Assets/Resources/" + ShaderLib.Folder;

        public int callbackOrder => -100;
        public void OnPreprocessBuild(BuildReport report) => Ensure();

        [InitializeOnLoadMethod]
        static void OnLoad() => EditorApplication.delayCall += () => Ensure();

        public static void Ensure()
        {
            // The base materials use URP shaders. In a project on the built-in pipeline there is
            // nothing to create (ShaderLib falls back to Standard at runtime), so do nothing.
            if (UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline == null) return;
            Directory.CreateDirectory(Dir);
            bool created = false;
            foreach (ShaderLib.Kind k in Enum.GetValues(typeof(ShaderLib.Kind)))
            {
                var path = $"{Dir}/{k}.mat";
                if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) continue;
                var shader = Shader.Find(ShaderLib.ShaderName(k));
                if (shader == null) { Debug.LogError($"Keystone: shader '{ShaderLib.ShaderName(k)}' not found"); continue; }
                var m = new Material(shader) { name = k.ToString() };
                ShaderLib.Configure(m, k);
                AssetDatabase.CreateAsset(m, path);
                created = true;
            }
            if (created)
            {
                AssetDatabase.SaveAssets();
                Debug.Log("Keystone: created base materials in " + Dir);
            }
        }
    }
}

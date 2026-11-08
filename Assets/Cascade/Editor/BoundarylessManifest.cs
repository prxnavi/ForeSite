#if UNITY_ANDROID
using System.IO;
using UnityEditor.Android;

namespace Cascade.EditorTools
{
    /// Declares Cascade a boundary-less app on Quest, so the Guardian / room-scale boundary
    /// doesn't draw over the building. Navigation is point-and-zoom, so you never need to walk far.
    class BoundarylessManifest : IPostGenerateGradleAndroidProject
    {
        public int callbackOrder => 100;
        const string Feature = "com.oculus.feature.BOUNDARYLESS_APP";

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            var manifest = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(manifest)) return;
            var xml = File.ReadAllText(manifest);
            if (xml.Contains(Feature)) return;
            int at = xml.IndexOf("<application", System.StringComparison.Ordinal);
            if (at < 0) return;
            xml = xml.Insert(at, $"<uses-feature android:name=\"{Feature}\" android:required=\"true\" />\n  ");
            File.WriteAllText(manifest, xml);
        }
    }
}
#endif

using System.IO;
using UnityEditor;

namespace Atlas.EditorTools
{
    /// <summary>
    /// Lets tools outside Unity request a Quest build: create Builds/build-request
    /// (contents "run" = build + install + launch on the headset, anything else = APK only).
    /// The result is written to Builds/last-build.txt.
    /// </summary>
    [InitializeOnLoad]
    static class AtlasBuildTrigger
    {
        const string Request = "Builds/build-request";
        const string Result = "Builds/last-build.txt";

        static AtlasBuildTrigger() => EditorApplication.update += Poll;

        static double nextCheck;

        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < nextCheck) return;
            nextCheck = EditorApplication.timeSinceStartup + 2.0;
            if (!File.Exists(Request)) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;

            var req = File.ReadAllText(Request).Trim();
            bool run = req.EndsWith("run");
            bool cascade = req.StartsWith("cascade");
            File.Delete(Request);
            if (File.Exists(Result)) File.Delete(Result);

            string msg;
            try { msg = cascade ? (run ? AtlasQuestSetup.CascadeBuildAndRun() : AtlasQuestSetup.CascadeBuildOnly()) : (run ? AtlasQuestSetup.BuildAndRun() : AtlasQuestSetup.BuildOnly()); }
            catch (System.Exception e) { msg = "Build threw: " + e; }
            File.WriteAllText(Result, msg);
        }
    }
}

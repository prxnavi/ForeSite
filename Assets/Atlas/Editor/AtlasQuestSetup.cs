using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.PackageManager.UI;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;

namespace Atlas.EditorTools
{
    /// <summary>
    /// One-click Meta Quest setup for Atlas (OpenXR + Meta OpenXR + XRI + XR Hands).
    /// Run the steps in order from the "Atlas/Quest Setup" menu.
    /// </summary>
    public static class AtlasQuestSetup
    {
        const string QuestScene = "Assets/Scenes/AtlasQuest.unity";
        const string DesktopScene = "Assets/Scenes/Atlas.unity";

        // ------------------------------------------------------------------ 1
        [MenuItem("Atlas/Quest Setup/1. Configure Project for Quest")]
        public static string ConfigureProject()
        {
            var log = new StringBuilder();
            var android = NamedBuildTarget.Android;

            PlayerSettings.companyName = "Atlas Team";
            PlayerSettings.productName = "Atlas";
            PlayerSettings.SetApplicationIdentifier(android, "com.prxnavi.atlas");
            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
            PlayerSettings.Android.forceInternetPermission = true; // SpacetimeDB websocket
            PlayerSettings.SetManagedStrippingLevel(android, ManagedStrippingLevel.Minimal);
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
            PlayerSettings.runInBackground = true;
            log.AppendLine("Player settings: IL2CPP, ARM64, API 32+, Vulkan, internet permission.");

            // XR Plug-in Management: OpenXR on Android
            if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget perTarget) || !perTarget)
            {
                Directory.CreateDirectory("Assets/XR");
                perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(perTarget, "Assets/XR/XRGeneralSettingsPerBuildTarget.asset");
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perTarget, true);
            }
            if (!perTarget.HasSettingsForBuildTarget(BuildTargetGroup.Android)) perTarget.CreateDefaultSettingsForBuildTarget(BuildTargetGroup.Android);
            if (!perTarget.HasManagerSettingsForBuildTarget(BuildTargetGroup.Android)) perTarget.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.Android);
            var general = perTarget.SettingsForBuildTarget(BuildTargetGroup.Android);
            general.InitManagerOnStart = true;
            bool assigned = XRPackageMetadataStore.AssignLoader(general.Manager, "UnityEngine.XR.OpenXR.OpenXRLoader", BuildTargetGroup.Android);
            log.AppendLine($"OpenXR loader on Android: {(assigned || general.Manager.activeLoaders.Any() ? "on" : "FAILED")}");

            // OpenXR features for Quest: support, passthrough, hands, controllers
            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            if (settings == null)
            {
                log.AppendLine("OpenXR settings for Android not found yet (re-run after the editor finishes importing).");
            }
            else
            {
                string[] wanted =
                {
                    "MetaQuestFeature",               // Meta Quest Support
                    "ARSessionFeature",               // Meta Quest: Session
                    "ARCameraFeature",                // Meta Quest: Camera (Passthrough)
                    "HandTracking",                   // XR Hands
                    "MetaHandTrackingAim",            // pinch / aim pose
                    "OculusTouchControllerProfile",
                    "MetaQuestTouchPlusControllerProfile",
                    "HandInteractionProfile",
                };
                foreach (var f in settings.GetFeatures())
                {
                    if (wanted.Contains(f.GetType().Name)) f.enabled = true;
                }
                EditorUtility.SetDirty(settings);
                log.AppendLine("OpenXR features enabled: " +
                    string.Join(", ", settings.GetFeatures().Where(f => f.enabled).Select(f => f.GetType().Name)));
            }

            AssetDatabase.SaveAssets();
            Debug.Log("[Atlas] " + log);
            return log.ToString();
        }

        // ------------------------------------------------------------------ 2
        [MenuItem("Atlas/Quest Setup/2. Import XR Samples (rig + hands)")]
        public static string ImportSamples()
        {
            var log = new StringBuilder();
            Import("com.unity.xr.interaction.toolkit", "Starter Assets", log);
            Import("com.unity.xr.interaction.toolkit", "Hands Interaction Demo", log);
            Import("com.unity.xr.hands", "HandVisualizer", log);
            AssetDatabase.Refresh();
            Debug.Log("[Atlas] " + log);
            return log.ToString();
        }

        static void Import(string package, string sampleName, StringBuilder log)
        {
            var sample = Sample.FindByPackage(package, null).FirstOrDefault(s => s.displayName == sampleName);
            if (string.IsNullOrEmpty(sample.displayName)) { log.AppendLine($"Sample not found: {package} / {sampleName}"); return; }
            if (sample.isImported) { log.AppendLine($"Already imported: {sampleName}"); return; }
            bool ok = sample.Import(Sample.ImportOptions.OverridePreviousImports | Sample.ImportOptions.HideImportWindow);
            log.AppendLine($"{(ok ? "Imported" : "FAILED")}: {sampleName}");
        }

        // ------------------------------------------------------------------ 3
        [MenuItem("Atlas/Quest Setup/3. Create Quest Scene")]
        public static string CreateQuestScene()
        {
            var rigGuid = AssetDatabase.FindAssets("XR Origin Hands (XR Rig) t:Prefab").FirstOrDefault()
                          ?? AssetDatabase.FindAssets("XR Origin (XR Rig) t:Prefab").FirstOrDefault();
            if (rigGuid == null) return "No XR Origin prefab found. Run step 2 first and wait for it to import.";
            var rigPath = AssetDatabase.GUIDToAssetPath(rigGuid);

            var scene = EditorSceneManager.OpenScene(DesktopScene, OpenSceneMode.Single);
            foreach (var go in scene.GetRootGameObjects())
                if (go.GetComponent<Camera>()) Object.DestroyImmediate(go); // desktop camera

            var rig = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(rigPath));
            rig.transform.position = Vector3.zero;

            // Passthrough: AR Session + ARCameraManager on the XR camera, transparent clear.
            new GameObject("AR Session", typeof(UnityEngine.XR.ARFoundation.ARSession));
            var cam = rig.GetComponentInChildren<Camera>(true);
            if (cam)
            {
                cam.tag = "MainCamera";
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0, 0, 0, 0);
                cam.nearClipPlane = 0.03f;
                if (!cam.GetComponent<UnityEngine.XR.ARFoundation.ARCameraManager>())
                    cam.gameObject.AddComponent<UnityEngine.XR.ARFoundation.ARCameraManager>();
            }

            var app = Object.FindFirstObjectByType<AtlasApp>();
            if (app) app.viewer = cam ? cam.transform : null;

            EditorSceneManager.SaveScene(scene, QuestScene);
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(QuestScene, true),
                new EditorBuildSettingsScene(DesktopScene, false),
            };
            var msg = $"Created {QuestScene} with {Path.GetFileNameWithoutExtension(rigPath)}, AR Session and passthrough camera.";
            Debug.Log("[Atlas] " + msg);
            return msg;
        }

        // ------------------------------------------------------------------ 4
        [MenuItem("Atlas/Quest Setup/4. Switch Build Target to Android")]
        public static string SwitchToAndroid()
        {
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android) return "Already Android.";
            bool ok = EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            return ok ? "Switched to Android." : "Switch failed (is Android Build Support installed?).";
        }

        // ------------------------------------------------------------------ 5
        [MenuItem("Atlas/Quest Setup/5. Build and Run on Quest")]
        public static string BuildAndRun() => Build(true);

        [MenuItem("Atlas/Quest Setup/Build APK Only")]
        public static string BuildOnly() => Build(false);

        static string Build(bool run)
        {
            Directory.CreateDirectory("Builds");
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { QuestScene },
                locationPathName = "Builds/Atlas.apk",
                target = BuildTarget.Android,
                options = run ? BuildOptions.AutoRunPlayer : BuildOptions.None,
            });
            var s = report.summary;
            var msg = $"Build {s.result}: {s.totalErrors} errors, {s.totalSize / (1024 * 1024)} MB, {s.totalTime:mm\\:ss}. Output: Builds/Atlas.apk";
            Debug.Log("[Atlas] " + msg);
            return msg;
        }
    }
}

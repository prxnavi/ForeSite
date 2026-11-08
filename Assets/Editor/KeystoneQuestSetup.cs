using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;

namespace CbreTwin.EditorTools
{
    /// <summary>
    /// One-click Meta Quest setup: Android build target, Quest player settings,
    /// Oculus XR loader, and a connection config the headset can read.
    /// </summary>
    public static class KeystoneQuestSetup
    {
        const string ConfigPath = "Assets/Resources/keystone_config.json";
        const string OculusLoader = "Unity.XR.Oculus.OculusLoader";

        [MenuItem("Keystone/Quest/1. Configure project for Meta Quest", priority = 1)]
        public static void Configure()
        {
            // Android support installed while the editor was open isn't loaded until Unity restarts;
            // switching now leaves packages (e.g. XR Hands) unable to compile.
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android))
            {
                EditorUtility.DisplayDialog("Keystone", "Android Build Support isn't loaded in this editor session.\n\nIf you just installed it, quit and reopen Unity, then run this again. Otherwise add it in Unity Hub → Installs → Add modules.", "OK");
                return;
            }
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android &&
                !EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android))
            {
                EditorUtility.DisplayDialog("Keystone", "Couldn't switch to Android. Install Android Build Support for this Unity version in Unity Hub, then try again.", "OK");
                return;
            }

            var android = NamedBuildTarget.Android;
            PlayerSettings.companyName = "Keystone";
            PlayerSettings.productName = "Keystone";
            PlayerSettings.SetApplicationIdentifier(android, "com.keystone.twin");
            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = (AndroidSdkVersions)32;
            PlayerSettings.Android.forceInternetPermission = true;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed; // ws:// to a laptop on the LAN

            // XR Plug-in Management → Oculus loader for Android
            if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget perTarget) || perTarget == null)
            {
                Directory.CreateDirectory("Assets/XR");
                perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(perTarget, "Assets/XR/XRGeneralSettingsPerBuildTarget.asset");
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perTarget, true);
            }
            if (!perTarget.HasManagerSettingsForBuildTarget(BuildTargetGroup.Android))
                perTarget.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.Android);
            var general = perTarget.SettingsForBuildTarget(BuildTargetGroup.Android);
            general.InitManagerOnStart = true;
            var assigned = XRPackageMetadataStore.AssignLoader(perTarget.ManagerSettingsForBuildTarget(BuildTargetGroup.Android), OculusLoader, BuildTargetGroup.Android);
            EditorUtility.SetDirty(perTarget);
            AssetDatabase.SaveAssets();

            if (!File.Exists(ConfigPath)) WriteConfig(TwinConnection.DefaultServer);

            EditorUtility.DisplayDialog("Keystone",
                (assigned ? "Project is set up for Meta Quest (Android, IL2CPP ARM64, Vulkan, Oculus XR loader).\n\n" : "Android settings applied, but the Oculus XR loader couldn't be assigned automatically. Enable it in Project Settings → XR Plug-in Management → Android.\n\n") +
                "Next: Keystone → Quest → 2. Point headset at this Mac (or Maincloud), then 3. Build and run.", "OK");
        }

        [MenuItem("Keystone/Quest/2. Point headset at this Mac (Wi-Fi)", priority = 2)]
        public static void UseLan()
        {
            var url = LanServer();
            WriteConfig(url);
            EditorUtility.DisplayDialog("Keystone",
                $"The headset will connect to {url}.\n\nStart SpacetimeDB so other devices can reach it:\n  spacetime start --listen-addr 0.0.0.0:3000\n\nThe Quest and this Mac must be on the same Wi-Fi.", "OK");
        }

        [MenuItem("Keystone/Quest/2b. Point headset at Maincloud", priority = 3)]
        public static void UseMaincloud()
        {
            WriteConfig("https://maincloud.spacetimedb.com");
            EditorUtility.DisplayDialog("Keystone", "The headset will connect to Maincloud. Make sure the database name in the app's server settings matches the one you published (it's copied into the config).", "OK");
        }

        [MenuItem("Keystone/Quest/3. Build and run on headset", priority = 4)]
        public static void BuildAndRun()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android) Configure();
            if (!File.Exists(ConfigPath)) WriteConfig(TwinConnection.DefaultServer);
            Directory.CreateDirectory("Builds");
            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            if (scenes.Length == 0) scenes = new[] { "Assets/Scenes/SampleScene.unity" };
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = "Builds/Keystone.apk",
                target = BuildTarget.Android,
                options = BuildOptions.AutoRunPlayer,
            });
            Debug.Log($"Keystone Quest build: {report.summary.result} ({report.summary.totalSize / (1024 * 1024)} MB)");
        }

        /// <summary>
        /// Headless build for the command line:
        /// Unity -batchmode -quit -projectPath . -buildTarget Android -executeMethod CbreTwin.EditorTools.KeystoneQuestSetup.CommandLineBuild
        /// Produces Builds/Keystone.apk (Maincloud config) and exits non-zero on failure.
        /// </summary>
        public static void CommandLineBuild()
        {
            Configure();
            WriteConfig(TwinConnection.DefaultServer);
            Directory.CreateDirectory("Builds");
            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            if (scenes.Length == 0) scenes = new[] { "Assets/Scenes/SampleScene.unity" };
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = "Builds/Keystone.apk",
                target = BuildTarget.Android,
                options = BuildOptions.None,
            });
            Debug.Log($"Keystone Quest build: {report.summary.result} · {report.summary.totalErrors} errors · {report.summary.totalSize / (1024 * 1024)} MB");
            if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
        }

        static void WriteConfig(string server)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            var cfg = new TwinConnection.AppConfig
            {
                server = server,
                database = server.Contains("maincloud") ? TwinConnection.DefaultDb : PlayerPrefs.GetString("twin.db", TwinConnection.DefaultDb),
                witToken = PlayerPrefs.GetString("keystone.wit_token", ""),
                playerName = "Quest visitor",
            };
            File.WriteAllText(ConfigPath, JsonUtility.ToJson(cfg, true));
            AssetDatabase.ImportAsset(ConfigPath);
            Debug.Log($"Keystone: wrote {ConfigPath} → {server} / {cfg.database} (voice {(string.IsNullOrEmpty(cfg.witToken) ? "off: no Wit.ai token saved yet" : "on")})");
        }

        static string LanServer()
        {
            var ip = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254"));
            return $"http://{(ip != null ? ip.ToString() : "127.0.0.1")}:3000";
        }
    }
}

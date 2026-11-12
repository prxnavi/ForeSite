using UnityEngine;
using UnityEngine.Rendering;

namespace CbreTwin
{
    /// <summary>
    /// Zero-setup entry point: press Play in the CbreTwin scene and the whole twin app
    /// (connection, 3D builder, camera, UI) is created at runtime. It stays out of every
    /// other scene in the project, so it never interferes with the other apps.
    /// </summary>
    public static class TwinBootstrap
    {
        public const string SceneName = "CbreTwin";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != SceneName) return;
            if (Object.FindAnyObjectByType<TwinUI>() != null) return;

            var cam = Camera.main;
            if (cam == null)
            {
                var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
                cam = camGo.AddComponent<Camera>();
            }
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Palette.Sky3D;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 500f;
            cam.fieldOfView = 45f;

            // Soft, bright lighting for a light-themed product look
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = Palette.Hex("#F4F8FF");
            RenderSettings.ambientEquatorColor = Palette.Hex("#E9E4F5");
            RenderSettings.ambientGroundColor = Palette.Hex("#C9D7C3");
            RenderSettings.fog = false;
            var sun = Object.FindAnyObjectByType<Light>();
            if (sun == null || sun.type != LightType.Directional)
            {
                sun = new GameObject("Sun").AddComponent<Light>();
                sun.type = LightType.Directional;
            }
            sun.color = Palette.Hex("#FFF4E5");
            sun.intensity = 1.25f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.55f;
            sun.transform.rotation = Quaternion.Euler(52f, -35f, 0f);

            var app = new GameObject("CBRE Digital Twin");
            Object.DontDestroyOnLoad(app);
            app.AddComponent<TwinConnection>();
            var builder = new GameObject("Twin Builder").AddComponent<BlueprintBuilder>();
            builder.transform.SetParent(app.transform, false);

            // Meta Quest: a headset is rendering, so run the immersive app instead of the desktop UI.
            if (Keystone.XR.XRHub.HeadsetActive)
            {
                cam.gameObject.SetActive(false);
                var xr = Keystone.XR.XRHub.Rig;
                var quest = app.AddComponent<QuestApp>();
                quest.Init(builder, xr);
                var vrPresence = new GameObject("Teammates").AddComponent<PresenceSync>();
                vrPresence.transform.SetParent(app.transform, false);
                vrPresence.Builder = builder;
                vrPresence.PoseSource = () => xr.Head != null ? (xr.Head.position, xr.Head.eulerAngles.y) : (Vector3.zero, 0f);
                return;
            }

            var rig = app.AddComponent<CameraRig>();
            rig.Cam = cam;
            var presence = new GameObject("Teammates").AddComponent<PresenceSync>();
            presence.transform.SetParent(app.transform, false);
            presence.Rig = rig;
            presence.Builder = builder;
            var ui = app.AddComponent<TwinUI>();
            ui.Init(builder, rig, presence);
        }
    }
}

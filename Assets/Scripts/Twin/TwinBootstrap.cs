using UnityEngine;
using UnityEngine.Rendering;

namespace CbreTwin
{
    /// <summary>
    /// Zero-setup entry point: press Play in any scene and the whole twin app
    /// (connection, 3D builder, camera, UI) is created at runtime.
    /// </summary>
    public static class TwinBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
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

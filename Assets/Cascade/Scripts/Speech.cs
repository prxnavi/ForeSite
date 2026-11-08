using UnityEngine;

namespace Cascade
{
    /// Spoken answers. Android (Quest): the system TextToSpeech engine. Editor: logged only.
    public static class Speech
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        static AndroidJavaObject tts;
        static bool ready;
        static string pending;

        class InitListener : AndroidJavaProxy
        {
            public InitListener() : base("android.speech.tts.TextToSpeech$OnInitListener") { }
            public void onInit(int status)
            {
                ready = status == 0;
                if (ready) { try { tts.Call<int>("setSpeechRate", 1.2f); } catch { } }
                if (ready && pending != null) { var p = pending; pending = null; Say(p); }
            }
        }

        static void Init()
        {
            if (tts != null) return;
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                tts = new AndroidJavaObject("android.speech.tts.TextToSpeech", activity, new InitListener());
            }
            catch (System.Exception e) { Debug.LogWarning("[Cascade] TTS unavailable: " + e.Message); }
        }
#endif

        public static void Say(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            Debug.Log("[Cascade] 🔊 " + text);
#if UNITY_ANDROID && !UNITY_EDITOR
            Init();
            if (!ready) { pending = text; return; }
            try { tts.Call<int>("speak", text, 0, null, "cascade"); }
            catch (System.Exception e) { Debug.LogWarning("[Cascade] TTS failed: " + e.Message); }
#endif
        }

        public static void Stop()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try { tts?.Call<int>("stop"); } catch { }
#endif
        }
    }
}

using Meta.WitAi.Configuration;
using Meta.WitAi.Data.Configuration;
using Oculus.Voice.Dictation;
using Oculus.Voice.Dictation.Configuration;
using UnityEngine;

namespace Keystone.Voice
{
    /// <summary>
    /// Speech-to-text through the Meta Voice SDK (Wit.ai dictation).
    /// Works in the editor, desktop players and on Meta Quest.
    /// The Wit configuration is created at runtime from a client access token,
    /// so no editor-side setup asset is required.
    /// </summary>
    public class MetaVoiceBackend : MonoBehaviour, IVoiceBackend
    {
        AppDictationExperience dictation;
        WitConfiguration config;
        bool listening;
        string lastPartial = "";

        public string Name => "Meta Voice SDK";
        public bool IsConfigured => dictation != null;
        public bool IsListening => listening;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install()
        {
            var go = new GameObject("Meta Voice");
            DontDestroyOnLoad(go);
            VoiceHub.Register(go.AddComponent<MetaVoiceBackend>());
        }

        public void Configure(string clientToken)
        {
            if (string.IsNullOrWhiteSpace(clientToken)) return;
            if (dictation != null) Destroy(dictation.gameObject);

            config = ScriptableObject.CreateInstance<WitConfiguration>();
            config.SetClientAccessToken(clientToken.Trim());

            var host = new GameObject("Wit Dictation");
            host.SetActive(false);                     // configure before OnEnable initialises dictation
            host.transform.SetParent(transform, false);
            dictation = host.AddComponent<AppDictationExperience>();
            dictation.RuntimeDictationConfiguration = new WitDictationRuntimeConfiguration
            {
                witConfiguration = config,
                dictationConfiguration = new DictationConfiguration(),
                maxRecordingTime = 30f,
                minKeepAliveTimeInSeconds = 3f,
                minTranscriptionKeepAliveTimeInSeconds = 1.5f,
            };

            var ev = dictation.DictationEvents;
            ev.OnStartListening.AddListener(() => { listening = true; lastPartial = ""; VoiceHub.RaiseListening(true); });
            ev.OnStoppedListening.AddListener(() =>
            {
                listening = false;
                VoiceHub.RaiseListening(false);
            });
            ev.OnPartialTranscription.AddListener(t => { lastPartial = t; VoiceHub.RaisePartial(t); });
            ev.OnFullTranscription.AddListener(t => { lastPartial = ""; if (!string.IsNullOrWhiteSpace(t)) VoiceHub.RaiseFinal(t); });
            ev.OnMicLevelChanged.AddListener(VoiceHub.RaiseLevel);
            ev.OnError.AddListener((type, message) => VoiceHub.RaiseFailed(string.IsNullOrEmpty(message) ? type : message));
            host.SetActive(true);
        }

        public void Begin()
        {
            if (dictation == null || listening) return;
            dictation.Activate();
        }

        public void End()
        {
            if (dictation == null) return;
            dictation.Deactivate();
        }
    }
}

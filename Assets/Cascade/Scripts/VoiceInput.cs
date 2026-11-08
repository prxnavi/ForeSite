using System;
using System.Text.RegularExpressions;
using UnityEngine;
using Whisper;
using Whisper.Utils;

namespace Cascade
{
    /// <summary>
    /// Hold-to-talk speech input. Records the headset mic and transcribes on-device with
    /// Whisper (whisper.unity, model in StreamingAssets/Whisper). No network, no key.
    /// </summary>
    public class VoiceInput : MonoBehaviour
    {
        public enum State { Loading, Ready, Listening, Transcribing, Unavailable }

        public State Current { get; private set; } = State.Loading;
        public float Level { get; private set; }
        public string LastText { get; private set; } = "";
        public event Action<string> Transcribed;

        WhisperManager whisper;
        MicrophoneRecord mic;
        float startedAt;

        const string Model = "Whisper/ggml-base.en.bin";
        const string Prompt = "Cascade, Harbor Point. Chiller 1, Chiller 2, Air Handler 2, Server Room 300, Suite 300, Suite 400, " +
                              "Northwind Analytics, Harbor Health. Anything I should worry about? Approve the plan. Take me there.";

        void Awake()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
                UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);
#endif
            // Configure before the manager's own Awake runs (it loads the model on awake).
            var go = new GameObject("Whisper");
            go.SetActive(false);
            go.transform.SetParent(transform, false);
            whisper = go.AddComponent<WhisperManager>();
            whisper.ModelPath = Model;
            whisper.IsModelPathInStreamingAssets = true;
            whisper.language = "en";
            whisper.initialPrompt = Prompt;
            whisper.noContext = true;
            whisper.singleSegment = false;
            mic = go.AddComponent<MicrophoneRecord>();
            mic.echo = false;
            mic.useVad = false;
            mic.vadStop = false;
            mic.maxLengthSec = 20;
            mic.frequency = 16000;
            mic.OnRecordStop += OnRecordStop;
            mic.OnChunkReady += c =>
            {
                float sum = 0;
                foreach (var s in c.Data) sum += s * s;
                Level = Mathf.Clamp01(Mathf.Sqrt(sum / Mathf.Max(1, c.Data.Length)) * 8f);
            };
            go.SetActive(true);
        }

        void Update()
        {
            if (Current == State.Loading && whisper)
            {
                if (whisper.IsLoaded) Current = State.Ready;
                else if (!whisper.IsLoading && Time.time > 20f) Current = State.Unavailable;
            }
            if (Current != State.Listening) Level = Mathf.MoveTowards(Level, 0, Time.deltaTime * 3f);
        }

        public void Begin()
        {
            if (Current != State.Ready) return;
            if (Microphone.devices.Length == 0) { LastText = "No microphone found"; return; }
            Speech.Stop();
            Current = State.Listening;
            startedAt = Time.time;
            mic.StartRecord();
        }

        public void End()
        {
            if (Current != State.Listening) return;
            if (Time.time - startedAt < 0.35f) { mic.StopRecord(); Current = State.Ready; return; } // accidental tap
            Current = State.Transcribing;
            mic.StopRecord();
        }

        async void OnRecordStop(AudioChunk audio)
        {
            if (Current != State.Transcribing) return;
            try
            {
                var res = await whisper.GetTextAsync(audio.Data, audio.Frequency, audio.Channels);
                var text = Clean(res?.Result ?? "");
                LastText = text;
                Current = State.Ready;
                if (text.Length > 1) Transcribed?.Invoke(text);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Cascade] transcription failed: " + e.Message);
                LastText = "Didn't catch that";
                Current = State.Ready;
            }
        }

        static string Clean(string s)
        {
            s = Regex.Replace(s, @"\[[^\]]*\]|\([^\)]*\)", " "); // [BLANK_AUDIO], (wind)
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }
    }
}

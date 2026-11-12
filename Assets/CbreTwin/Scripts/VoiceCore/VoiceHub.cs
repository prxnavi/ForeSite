using System;

namespace Keystone.Voice
{
    /// <summary>A speech-to-text engine (the Meta Voice SDK backend registers itself here).</summary>
    public interface IVoiceBackend
    {
        string Name { get; }
        bool IsConfigured { get; }
        bool IsListening { get; }
        void Configure(string clientToken);
        void Begin();
        void End();
    }

    /// <summary>
    /// Decouples the app from the voice SDK: the UI talks to VoiceHub, and the
    /// Meta assembly (compiled only when com.meta.xr.sdk.voice is installed)
    /// plugs in as the backend at start-up.
    /// </summary>
    public static class VoiceHub
    {
        public static IVoiceBackend Backend { get; private set; }

        public static event Action<string> Partial;
        public static event Action<string> Final;
        public static event Action<string> Failed;
        public static event Action<bool> ListeningChanged;
        public static event Action<float> Level;

        public static bool Available => Backend != null;
        public static bool Ready => Backend != null && Backend.IsConfigured;
        public static bool Listening => Backend != null && Backend.IsListening;

        public static void Register(IVoiceBackend backend) => Backend = backend;

        public static void Configure(string token) => Backend?.Configure(token);

        public static void Begin()
        {
            if (Backend == null) { Failed?.Invoke("Voice is unavailable: the Meta Voice SDK package is not installed."); return; }
            if (!Backend.IsConfigured) { Failed?.Invoke("Add a Wit.ai client token in Agent → Voice settings."); return; }
            Backend.Begin();
        }

        public static void End() => Backend?.End();

        public static void RaisePartial(string t) => Partial?.Invoke(t);
        public static void RaiseFinal(string t) => Final?.Invoke(t);
        public static void RaiseFailed(string e) => Failed?.Invoke(e);
        public static void RaiseListening(bool on) => ListeningChanged?.Invoke(on);
        public static void RaiseLevel(float l) => Level?.Invoke(l);
    }
}

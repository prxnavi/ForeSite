using System;
using System.Collections.Generic;
using System.Linq;
using Cascade.Net;
using SpacetimeDB;
using UnityEngine;

namespace Cascade
{
    /// <summary>
    /// The only link to the backend: the published SpacetimeDB module "cascade-prxnavi"
    /// (Server/cascade). Views read the client cache directly (conn.Db) and rebuild when
    /// Version changes; methods here are thin reducer calls.
    /// Bindings: spacetime generate --lang csharp --namespace Cascade.Net --out-dir Assets/Cascade/Generated
    /// </summary>
    public class CascadeNet
    {
        public DbConnection Conn { get; private set; }
        public RemoteTables Db => Conn?.Db;
        public bool Ready { get; private set; }
        public bool Connected { get; private set; }
        public Identity? Me { get; private set; }
        public string Status { get; private set; } = "connecting";

        /// Bumped whenever any subscribed row changes.
        public int Version { get; private set; }
        /// Scene commands as they arrive (live only, after the initial sync).
        public event Action<SceneCmd> SceneCommand;

        readonly string uri, database;
        string name = "Guest", role = "manager";

        public CascadeNet(string uri, string database) { this.uri = uri; this.database = database; }

        public void Connect(string displayName, string startRole)
        {
            name = displayName; role = startRole;
            Conn = DbConnection.Builder()
                .WithUri(uri)
                .WithDatabaseName(database)
                .WithToken(AuthToken.Token)
                .OnConnect(OnConnected)
                .OnConnectError(e => { Status = "offline"; Debug.LogError($"[Cascade] connect failed: {e.Message}"); })
                .OnDisconnect((_, e) => { Connected = false; Status = "offline"; Debug.LogWarning($"[Cascade] disconnected {e?.Message}"); })
                .Build();

            var db = Conn.Db;
            Hook(db.Room); Hook(db.Asset); Hook(db.Dependency); Hook(db.WorkOrder); Hook(db.Tenant); Hook(db.Notice);
            Hook(db.Scenario); Hook(db.Investigation); Hook(db.Subtask); Hook(db.Evidence); Hook(db.Hypothesis);
            Hook(db.ActionCandidate); Hook(db.Critique); Hook(db.Decision); Hook(db.Answer); Hook(db.AgentStep);
            Hook(db.User); Hook(db.Timeline); Hook(db.Issue); Hook(db.SensorReading); Hook(db.PastIncident); Hook(db.SceneCommand);
            db.SceneCommand.OnInsert += (_, r) => { if (Ready) Emit(r.Payload); };
            db.Scenario.OnInsert += (_, _) => framesCache.Clear();
            db.Scenario.OnDelete += (_, _) => framesCache.Clear();
        }

        void Hook<R>(RemoteTableHandle<EventContext, R> h) where R : class, SpacetimeDB.BSATN.IStructuralReadWrite, new()
        {
            h.OnInsert += (_, _) => Version++;
            h.OnDelete += (_, _) => Version++;
            h.OnUpdate += (_, _, _) => Version++;
        }

        void OnConnected(DbConnection c, Identity id, string token)
        {
            AuthToken.SaveToken(token);
            Me = id;
            Connected = true;
            Status = "syncing";
            Debug.Log($"[Cascade] Connected to {database}");
            c.Reducers.Join(name, role);
            c.SubscriptionBuilder()
                .OnApplied(_ =>
                {
                    Ready = true;
                    Status = "live";
                    Version++;
                    Debug.Log($"[Cascade] Synced {c.Db.Room.Count} rooms, {c.Db.Asset.Count} assets from SpacetimeDB.");
                    // Replay the visual state (threads, trail, highlights) for late joiners.
                    foreach (var r in c.Db.SceneCommand.Iter().OrderBy(r => r.Id)) Emit(r.Payload);
                })
                .OnError((_, e) => Debug.LogError($"[Cascade] subscription failed: {e.Message}"))
                .SubscribeToAllTables();
        }

        void Emit(string payload)
        {
            SceneCmd cmd = null;
            try { cmd = JsonUtility.FromJson<SceneCmd>(payload); } catch { }
            if (cmd != null) SceneCommand?.Invoke(cmd);
        }

        public void Tick() => Conn?.FrameTick();
        public void Disconnect() { if (Conn != null && Conn.IsActive) Conn.Disconnect(); }

        // ---------------- queries ----------------

        public User MyUser => Ready && Me.HasValue ? Db.User.Identity.Find(Me.Value) : null;
        public string MyRole => MyUser?.Role ?? role;
        public Timeline Timeline => Ready ? Db.Timeline.Id.Find(0) : null;
        public Investigation Investigation => Ready ? Db.Investigation.Iter().OrderByDescending(i => i.Id).FirstOrDefault() : null;
        public Scenario ScenarioFor(string label) => Ready ? Db.Scenario.Iter().Where(s => s.Label == label).OrderByDescending(s => s.Id).FirstOrDefault() : null;

        readonly Dictionary<ulong, FrameSet> framesCache = new();
        public FrameSet Frames(Scenario s)
        {
            if (s == null) return null;
            if (framesCache.TryGetValue(s.Id, out var f)) return f;
            try
            {
                f = JsonUtility.FromJson<FrameSet>(s.FramesJson);
                f.risk = ParseRuns(s.RiskJson);
            }
            catch (Exception e) { Debug.LogWarning("[Cascade] bad frames: " + e.Message); f = null; }
            framesCache[s.Id] = f;
            return f;
        }

        static float[][] ParseRuns(string json)
        {
            if (string.IsNullOrEmpty(json) || json.Length < 4) return Array.Empty<float[]>();
            var inner = json.Trim().Substring(1, json.Trim().Length - 2); // drop outer [ ]
            var parts = inner.Split(new[] { "],[" }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Select(p => p.Trim('[', ']').Split(',')
                .Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray()).ToArray();
        }

        // ---------------- reducers ----------------

        bool Ok() => Connected && Conn != null;
        public void SetRole(string r) { role = r; if (Ok()) Conn.Reducers.SetRole(r); }
        public void Ask(string text) { if (Ok()) Conn.Reducers.Ask(text); }
        public void BuildTwin() { if (Ok()) Conn.Reducers.BuildTwin(); }
        public void SetTime(float hour, string label) { if (Ok()) Conn.Reducers.SetTime(hour, label); }
        public void SendTech(string id) { if (Ok()) Conn.Reducers.SendTech(id); }
        public void Approve(ulong id, bool on) { if (Ok()) Conn.Reducers.ApproveAction(id, on); }
        public void Execute() { if (Ok()) Conn.Reducers.ExecuteAction(); }
        public void ResetDemo() { if (Ok()) Conn.Reducers.ResetDemo(); }
        public void UpdatePosition(Vector2 p, bool present) { if (Ok()) Conn.Reducers.UpdatePosition(p.x, p.y, present); }
    }

    [Serializable] public class SceneCmd { public string kind; public string[] ids; public string target; public float hour; public string label; public string forRole; public string color; }
    [Serializable] public class Frame { public float[] t; public string[] a; public float c; public int ta; public int b; public float h; }
    [Serializable]
    public class FrameSet
    {
        public string[] rooms; public string[] assets; public float start; public float step; public Frame[] frames;
        [NonSerialized] public float[][] risk;

        public Frame At(float hour) => frames == null || frames.Length == 0 ? null
            : frames[Mathf.Clamp(Mathf.RoundToInt(hour / step), 0, frames.Length - 1)];

        public float Temp(string room, float hour)
        {
            int i = Array.IndexOf(rooms, room);
            if (i < 0 || frames == null || frames.Length == 0) return float.NaN;
            float f = Mathf.Clamp(hour / step, 0, frames.Length - 1);
            int a = Mathf.FloorToInt(f), b = Mathf.Min(a + 1, frames.Length - 1);
            return Mathf.Lerp(frames[a].t[i], frames[b].t[i], f - a);
        }

        public string AssetState(string asset, float hour)
        {
            int i = Array.IndexOf(assets, asset);
            var fr = At(hour);
            return i < 0 || fr == null ? null : fr.a[i];
        }
    }
}

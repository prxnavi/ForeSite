using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Db = SpacetimeDB.Types;

namespace Atlas
{
    /// <summary>
    /// IAtlasBackend on top of the published SpacetimeDB module (Server/spacetimedb).
    /// Table callbacks become view events; methods call reducers. Nothing here
    /// computes graph facts: blast radius, paths and the agent run in the module.
    /// Bindings in Assets/Atlas/Generated come from:
    ///   spacetime generate --lang csharp --out-dir ../Assets/Atlas/Generated --module-path spacetimedb
    /// </summary>
    public class SpacetimeAtlasBackend : IAtlasBackend
    {
        public event Action<ArchNode> NodeInserted;
        public event Action<ArchEdge> EdgeInserted;
        public event Action<NodePose> PoseUpdated;
        public event Action<Participant> ParticipantUpdated;
        public event Action<string> SceneCommandInserted;
        public event Action<AgentTask> TaskUpdated;
        public event Action<AgentStep> StepInserted;
        public event Action<AgentStep> StepUpdated;

        /// Raised when the connection state changes (for status text in the UI).
        public event Action<string> StatusChanged;

        public string LocalIdentity { get; private set; } = "";
        public string DisplayName => $"SpacetimeDB · {database}";
        public string AgentLabel => "Agent in SpacetimeDB";
        public bool IsConnected { get; private set; }

        readonly string uri, database;
        Db.DbConnection conn;
        bool ready;               // initial subscription applied
        string pendingName = "Guest";
        Role pendingRole = Role.Engineer;

        public SpacetimeAtlasBackend(string uri, string database)
        {
            this.uri = uri;
            this.database = database;
        }

        public void Connect(string displayName)
        {
            if (conn != null) return;
            if (!string.IsNullOrWhiteSpace(displayName)) pendingName = displayName;

            conn = Db.DbConnection.Builder()
                .WithUri(uri)
                .WithDatabaseName(database)
                .WithToken(SpacetimeDB.AuthToken.Token)
                .OnConnect(OnConnected)
                .OnConnectError(e =>
                {
                    Debug.LogError($"[Atlas] SpacetimeDB connect failed: {e.Message}");
                    StatusChanged?.Invoke("offline");
                })
                .OnDisconnect((_, e) =>
                {
                    IsConnected = false;
                    Debug.LogWarning($"[Atlas] SpacetimeDB disconnected{(e != null ? ": " + e.Message : "")}");
                    StatusChanged?.Invoke("offline");
                })
                .Build();

            RegisterTableCallbacks();
        }

        public void Tick(float dt) => conn?.FrameTick();

        public void Disconnect()
        {
            if (conn != null && conn.IsActive) conn.Disconnect();
        }

        void OnConnected(Db.DbConnection c, SpacetimeDB.Identity identity, string token)
        {
            SpacetimeDB.AuthToken.SaveToken(token);
            LocalIdentity = identity.ToString();
            IsConnected = true;
            Debug.Log($"[Atlas] Connected to {database} as {LocalIdentity.Substring(0, Math.Min(12, LocalIdentity.Length))}…");
            StatusChanged?.Invoke("connected");

            c.Reducers.Join(pendingName, pendingRole.ToString());
            c.SubscriptionBuilder()
                .OnApplied(_ => ReplayInitialState())
                .OnError((_, e) => Debug.LogError($"[Atlas] Subscription failed: {e.Message}"))
                .SubscribeToAllTables();
        }

        // ---------------- tables → view events ----------------

        void RegisterTableCallbacks()
        {
            var db = conn.Db;
            db.Node.OnInsert += (_, r) => { if (ready) NodeInserted?.Invoke(Map(r)); };
            db.Edge.OnInsert += (_, r) => { if (ready) EdgeInserted?.Invoke(Map(r)); };
            db.NodePose.OnInsert += (_, r) => { if (ready) PoseUpdated?.Invoke(Map(r)); };
            db.NodePose.OnUpdate += (_, _, r) => { if (ready) PoseUpdated?.Invoke(Map(r)); };
            db.Participant.OnInsert += (_, r) => { if (ready) ParticipantUpdated?.Invoke(Map(r)); };
            db.Participant.OnUpdate += (_, _, r) => { if (ready) ParticipantUpdated?.Invoke(Map(r)); };
            db.SceneCommand.OnInsert += (_, r) => { if (ready) SceneCommandInserted?.Invoke(r.Payload); };
            db.AgentTask.OnInsert += (_, r) => { if (ready) TaskUpdated?.Invoke(Map(r)); };
            db.AgentTask.OnUpdate += (_, _, r) => { if (ready) TaskUpdated?.Invoke(Map(r)); };
            db.AgentStep.OnInsert += (_, r) => { if (ready) StepInserted?.Invoke(Map(r)); };
            db.AgentStep.OnUpdate += (_, _, r) => { if (ready) StepUpdated?.Invoke(Map(r)); };
        }

        /// Initial rows arrive in no particular order; replay them in dependency order.
        void ReplayInitialState()
        {
            var db = conn.Db;
            foreach (var n in db.Node.Iter()) NodeInserted?.Invoke(Map(n));
            foreach (var e in db.Edge.Iter()) EdgeInserted?.Invoke(Map(e));
            foreach (var p in db.NodePose.Iter()) PoseUpdated?.Invoke(Map(p));
            foreach (var p in db.Participant.Iter()) ParticipantUpdated?.Invoke(Map(p));
            foreach (var c in db.SceneCommand.Iter().OrderBy(c => c.Id)) SceneCommandInserted?.Invoke(c.Payload);

            var latest = db.AgentTask.Iter().OrderByDescending(t => t.Id).FirstOrDefault();
            if (latest != null)
            {
                TaskUpdated?.Invoke(Map(latest));
                foreach (var s in db.AgentStep.Iter().Where(s => s.TaskId == latest.Id).OrderBy(s => s.StepIndex))
                    StepInserted?.Invoke(Map(s));
            }
            ready = true;
            Debug.Log($"[Atlas] Synced {db.Node.Count} pieces, {db.Edge.Count} connections from SpacetimeDB.");
        }

        // ---------------- reducers ----------------

        public void SetRole(Role role)
        {
            pendingRole = role;
            if (IsConnected) conn.Reducers.SetRole(role.ToString());
        }

        public void SelectNode(string nodeId) { if (Ready()) conn.Reducers.SelectNode(nodeId ?? ""); }
        public void MoveNode(string nodeId, Vector2 offset, bool held) { if (Ready()) conn.Reducers.MoveNode(nodeId, offset.x, offset.y, held); }
        public void AskQuestion(string text) { if (Ready() && !string.IsNullOrWhiteSpace(text)) conn.Reducers.AskQuestion(text); }
        public void ShowBlastRadius(string nodeId) { if (Ready()) conn.Reducers.ShowBlastRadius(nodeId); }
        public void ClearScene() { if (Ready()) conn.Reducers.ClearScene(); }

        bool Ready()
        {
            if (IsConnected) return true;
            Debug.LogWarning("[Atlas] Not connected to SpacetimeDB yet.");
            return false;
        }

        // ---------------- row mapping ----------------

        static T ParseEnum<T>(string s, T fallback) where T : struct => Enum.TryParse(s, out T v) ? v : fallback;

        static ArchNode Map(Db.Node r) => new()
        {
            Id = r.Id, Title = r.Title, Feature = r.Feature, Tag = r.Tag, File = r.File,
            Kind = ParseEnum(r.Kind, NodeKind.Program),
            Status = ParseEnum(r.Status, NodeStatus.NotExplored),
            CodePos = new Vector2(r.CodeX, r.CodeZ),
            EngineerSummary = r.EngineerSummary, PmSummary = r.PmSummary, ArchitectSummary = r.ArchitectSummary,
        };

        static ArchEdge Map(Db.Edge r) => new() { From = r.FromNode, To = r.ToNode, Label = r.Label };

        static NodePose Map(Db.NodePose r) => new()
        {
            NodeId = r.NodeId, Offset = new Vector2(r.OffsetX, r.OffsetZ), Held = r.Held, HeldBy = r.HeldBy.ToString(),
        };

        static Participant Map(Db.Participant r) => new()
        {
            Identity = r.Identity.ToString(), Name = r.Name,
            Role = ParseEnum(r.Role, Role.Engineer),
            SelectedNode = string.IsNullOrEmpty(r.SelectedNode) ? null : r.SelectedNode,
        };

        static AgentTask Map(Db.AgentTask r) => new()
        {
            Id = r.Id, Question = r.Question, AskedBy = r.AskedByName,
            AskedRole = ParseEnum(r.AskedRole, Role.Engineer), State = r.State, Answer = r.Answer,
        };

        static AgentStep Map(Db.AgentStep r) => new()
        {
            TaskId = r.TaskId, Index = (int)r.StepIndex, Phase = ParseEnum(r.Phase, StepPhase.Understand), Text = r.Text, Done = r.Done,
        };
    }
}

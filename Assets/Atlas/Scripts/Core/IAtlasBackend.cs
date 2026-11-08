using System;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// The only seam between the headset app and the backend.
    /// Events = SpacetimeDB table subscriptions (conn.Db.X.OnInsert / OnUpdate).
    /// Methods = SpacetimeDB reducers (conn.Reducers.X).
    /// The UI never computes graph facts itself; it renders rows and calls reducers.
    /// </summary>
    public interface IAtlasBackend
    {
        // ---- tables ----
        event Action<ArchNode> NodeInserted;
        event Action<ArchEdge> EdgeInserted;
        event Action<NodePose> PoseUpdated;
        event Action<Participant> ParticipantUpdated;
        event Action<string> SceneCommandInserted;   // raw JSON payload
        event Action<AgentTask> TaskUpdated;
        event Action<AgentStep> StepInserted;
        event Action<AgentStep> StepUpdated;

        string LocalIdentity { get; }
        string DisplayName { get; }   // shown in the UI, e.g. "SpacetimeDB · atlas"
        string AgentLabel { get; }    // e.g. "Nemotron via agent worker"
        bool IsConnected { get; }

        void Connect(string displayName);
        void Tick(float dt);          // SpacetimeDB: conn.FrameTick()

        // ---- reducers ----
        void SetRole(Role role);
        void SelectNode(string nodeId);                         // null clears
        void MoveNode(string nodeId, Vector2 offset, bool held);
        void AskQuestion(string text);                          // inserts agent_task
        void ShowBlastRadius(string nodeId);                    // reducer computes + emits scene_command
        void ClearScene();
    }
}

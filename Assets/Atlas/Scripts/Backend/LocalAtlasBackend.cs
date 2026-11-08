using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// In-memory stand-in for the SpacetimeDB module plus the agent worker,
    /// so the headset UI works end to end before the real backend exists.
    /// Every public method maps to something real:
    ///   SetRole / SelectNode / MoveNode / ClearScene / ShowBlastRadius -> reducers
    ///   TracePath / BlastRadius                                        -> reducer graph helpers
    ///   AskQuestion                                                    -> agent_task row, claimed by the Nemotron worker
    ///   Emit (with validation)                                         -> validate_scene_command reducer
    /// </summary>
    public class LocalAtlasBackend : IAtlasBackend
    {
        public event Action<ArchNode> NodeInserted;
        public event Action<ArchEdge> EdgeInserted;
        public event Action<NodePose> PoseUpdated;
        public event Action<Participant> ParticipantUpdated;
        public event Action<string> SceneCommandInserted;
        public event Action<AgentTask> TaskUpdated;
        public event Action<AgentStep> StepInserted;
        public event Action<AgentStep> StepUpdated;

        public string LocalIdentity => me.Identity;
        public string DisplayName => "Local demo data";
        public string AgentLabel => "Scripted demo agent";
        public bool IsConnected { get; private set; }

        readonly Dictionary<string, ArchNode> nodes = new();
        readonly List<ArchEdge> edges = new();
        readonly Participant me = new() { Identity = "local-user", Name = "You", Role = Role.Engineer };
        ulong nextTaskId = 1;

        // Tiny scheduler standing in for asynchronous agent work.
        readonly List<(float at, Action act)> timeline = new();
        float clock;

        public IReadOnlyCollection<ArchNode> AllNodes => nodes.Values;
        public ArchNode Node(string id) => id != null && nodes.TryGetValue(id, out var n) ? n : null;

        public void Connect(string displayName)
        {
            if (IsConnected) return;
            if (!string.IsNullOrEmpty(displayName)) me.Name = displayName;
            foreach (var n in DemoGraph.Nodes()) nodes[n.Id] = n;
            edges.AddRange(DemoGraph.Edges());
            IsConnected = true;

            foreach (var n in nodes.Values) NodeInserted?.Invoke(n);
            foreach (var e in edges) EdgeInserted?.Invoke(e);
            ParticipantUpdated?.Invoke(me);
        }

        public void Tick(float dt)
        {
            clock += dt;
            while (timeline.Count > 0 && timeline[0].at <= clock)
            {
                var act = timeline[0].act;
                timeline.RemoveAt(0);
                act();
            }
        }

        // ---------------- reducers ----------------

        public void SetRole(Role role)
        {
            me.Role = role;
            ParticipantUpdated?.Invoke(me);
        }

        public void SelectNode(string nodeId)
        {
            me.SelectedNode = Node(nodeId) != null ? nodeId : null;
            ParticipantUpdated?.Invoke(me);
        }

        public void MoveNode(string nodeId, Vector2 offset, bool held)
        {
            if (Node(nodeId) == null) return;
            PoseUpdated?.Invoke(new NodePose { NodeId = nodeId, Offset = offset, Held = held, HeldBy = held ? me.Identity : null });
        }

        public void ShowBlastRadius(string nodeId)
        {
            if (Node(nodeId) == null) return;
            Emit(BlastCommand(nodeId));
        }

        public void ClearScene() => Emit(new SceneCommand { kind = "clear" });

        public void AskQuestion(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            timeline.Clear(); // local only: the newest question wins

            var task = new AgentTask
            {
                Id = nextTaskId++, Question = text.Trim(), AskedBy = me.Name, AskedRole = me.Role, State = "thinking",
            };
            TaskUpdated?.Invoke(task);

            var plan = ScriptedAgent.Plan(this, task, me.SelectedNode);
            float t = 0.35f;
            int index = 0;
            foreach (var (phase, msg) in plan.Steps)
            {
                var step = new AgentStep { TaskId = task.Id, Index = index++, Phase = phase, Text = msg };
                At(t, () => StepInserted?.Invoke(step));
                t += plan.StepSeconds;
                At(t, () => { step.Done = true; StepUpdated?.Invoke(step); });
            }
            At(t + 0.05f, () =>
            {
                foreach (var c in plan.Commands) Emit(c);
                task.State = "done";
                task.Answer = plan.Answer;
                TaskUpdated?.Invoke(task);
            });
        }

        // ---------------- graph helpers (become reducers) ----------------

        /// Shortest directed path between two pieces (BFS over call/data edges).
        public List<string> TracePath(string from, string to)
        {
            var prev = new Dictionary<string, string> { [from] = null };
            var q = new Queue<string>();
            q.Enqueue(from);
            while (q.Count > 0)
            {
                var cur = q.Dequeue();
                if (cur == to) break;
                foreach (var e in edges)
                    if (e.From == cur && !prev.ContainsKey(e.To)) { prev[e.To] = cur; q.Enqueue(e.To); }
            }
            var path = new List<string>();
            if (!prev.ContainsKey(to)) return path;
            for (var c = to; c != null; c = prev[c]) path.Add(c);
            path.Reverse();
            return path;
        }

        /// Everything that depends on a piece: reverse BFS, with hop distance.
        public List<(string id, int depth)> BlastRadius(string id)
        {
            var depth = new Dictionary<string, int> { [id] = 0 };
            var q = new Queue<string>();
            q.Enqueue(id);
            while (q.Count > 0)
            {
                var cur = q.Dequeue();
                foreach (var e in edges)
                    if (e.To == cur && !depth.ContainsKey(e.From)) { depth[e.From] = depth[cur] + 1; q.Enqueue(e.From); }
            }
            return depth.Where(kv => kv.Key != id).OrderBy(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).ToList();
        }

        public SceneCommand BlastCommand(string id)
        {
            var br = BlastRadius(id);
            return new SceneCommand
            {
                kind = "show_blast_radius", origin = id,
                nodes = br.Select(b => b.id).ToArray(),
                levels = br.Select(b => Mathf.Min(b.depth, 3)).ToArray(),
            };
        }

        // ---------------- internals ----------------

        /// Mirrors the validate_scene_command reducer: every named piece must exist.
        void Emit(SceneCommand c)
        {
            var named = new List<string>();
            if (c.nodes != null) named.AddRange(c.nodes);
            if (!string.IsNullOrEmpty(c.origin)) named.Add(c.origin);
            if (!string.IsNullOrEmpty(c.node)) named.Add(c.node);
            var missing = named.Where(n => !nodes.ContainsKey(n)).ToList();
            if (missing.Count > 0)
            {
                Debug.LogWarning($"[Atlas] Rejected scene command '{c.kind}': unknown pieces {string.Join(", ", missing)}");
                return;
            }
            SceneCommandInserted?.Invoke(c.ToJson());
        }

        void At(float delay, Action act)
        {
            float at = clock + delay;
            int i = timeline.Count;
            while (i > 0 && timeline[i - 1].at > at) i--;
            timeline.Insert(i, (at, act));
        }
    }
}

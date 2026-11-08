using System;
using UnityEngine;

namespace Atlas
{
    // Row types that mirror the SpacetimeDB module tables one to one.
    // When the SpacetimeDB SDK bindings are generated, SpacetimeAtlasBackend
    // maps generated rows onto these, so the view layer never changes.

    public enum Role { Engineer, ProductManager, Architect }

    public enum NodeKind { Program, Batch, DataStore, External }

    public enum NodeStatus { NotExplored, Understood, Risk, External }

    public enum StepPhase { Understand, Plan, Investigate, Check, Respond }

    /// table: node
    [Serializable]
    public class ArchNode
    {
        public string Id;          // "PAYPOST"
        public string Title;       // "Post payment"
        public string Feature;     // business feature, used by set_layer business
        public NodeKind Kind;
        public NodeStatus Status;
        public string Tag;         // short descriptor under the title, e.g. "input"
        public string File;        // "PAYPOST.cbl"
        public Vector2 CodePos;    // code-layer layout on the table, meters (x, z)
        public string EngineerSummary;
        public string PmSummary;
        public string ArchitectSummary;

        public string SummaryFor(Role r) => r switch
        {
            Role.ProductManager => PmSummary,
            Role.Architect => ArchitectSummary,
            _ => EngineerSummary,
        };
    }

    /// table: edge
    [Serializable]
    public class ArchEdge
    {
        public string From;
        public string To;
        public string Label;       // "calls", "writes", ...
    }

    /// table: user
    [Serializable]
    public class Participant
    {
        public string Identity;
        public string Name;
        public Role Role;
        public string SelectedNode;
    }

    /// table: node_pose (live grab offsets, synced between headsets)
    [Serializable]
    public class NodePose
    {
        public string NodeId;
        public Vector2 Offset;     // meters from the layout position (x, z)
        public bool Held;
        public string HeldBy;
    }

    /// table: agent_task
    [Serializable]
    public class AgentTask
    {
        public ulong Id;
        public string Question;
        public string AskedBy;
        public Role AskedRole;
        public string State;       // "thinking" | "done"
        public string Answer;
    }

    /// table: agent_step
    [Serializable]
    public class AgentStep
    {
        public ulong TaskId;
        public int Index;
        public StepPhase Phase;
        public string Text;
        public bool Done;
    }

    /// table: scene_command (payload). The JSON shape from the spec:
    /// {"kind":"highlight_path","nodes":["PAYENTRY","PAYVALID"]}
    [Serializable]
    public class SceneCommand
    {
        public string kind;
        public string[] nodes;
        public int[] levels;       // show_blast_radius: risk level per node (1 = direct)
        public string origin;      // show_blast_radius origin
        public string node;        // focus_node
        public string layer;       // set_layer: "code" | "business"
        public string color;       // optional hex for highlight_nodes

        public static SceneCommand Parse(string json) => JsonUtility.FromJson<SceneCommand>(json);
        public string ToJson() => JsonUtility.ToJson(this);
    }

    public static class RoleNames
    {
        public static string Short(Role r) => r switch
        {
            Role.ProductManager => "PM",
            Role.Architect => "Architect",
            _ => "Engineer",
        };
    }
}

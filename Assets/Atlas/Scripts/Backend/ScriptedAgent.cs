using System.Collections.Generic;
using System.Linq;

namespace Atlas
{
    /// <summary>
    /// Keyword-matched stand-in for the Nemotron agent worker. It follows the
    /// spec's five-step loop (understand, plan, investigate, check, respond)
    /// and only answers with facts computed from the graph, so the real worker
    /// can replace it without changing what the headset receives.
    /// </summary>
    public static class ScriptedAgent
    {
        public class Result
        {
            public readonly List<(StepPhase, string)> Steps = new();
            public readonly List<SceneCommand> Commands = new();
            public string Answer = "";
            public float StepSeconds = 0.7f;
            public void Step(StepPhase p, string text) => Steps.Add((p, text));
        }

        public static Result Plan(LocalAtlasBackend db, AgentTask task, string selected)
        {
            string q = task.Question.ToLowerInvariant();
            Role role = task.AskedRole;
            string target = Mentioned(db, q) ?? selected ?? "PAYPOST";

            if (Has(q, "reset", "clear")) return Reset(role);
            if (Has(q, "tell me more")) return Explain(db, role, target);
            if (Has(q, "break", "blast", "impact", "depend", "change")) return Blast(db, role, target);
            if (Has(q, "customer", "business", "feature", "product")) return Layer(db, role, "business");
            if (Has(q, "code view", "engineer view", "programs")) return Layer(db, role, "code");
            if (Has(q, "payment") && Has(q, "process", "flow", "work", "trace", "how")) return PaymentTrace(db, role);
            if (Has(q, "tell me more", "explain", "what does", "what is", "where")) return Explain(db, role, target);
            return Fallback(role);
        }

        static Result PaymentTrace(LocalAtlasBackend db, Role role)
        {
            var r = new Result();
            var path = db.TracePath("PAYENTRY", "ACCTFILE");
            r.Step(StepPhase.Understand, $"{RoleNames.Short(role)} asked how a payment is processed.");
            r.Step(StepPhase.Plan, "Find where payments start, then follow the calls to storage.");
            r.Step(StepPhase.Investigate, "search_code(\"PAYMENT-RECORD\") → entry point PAYENTRY.cbl");
            r.Step(StepPhase.Investigate, $"trace_path(PAYENTRY → ACCTFILE) → {path.Count} pieces");
            r.Step(StepPhase.Check, $"Verified {path.Count} pieces and {path.Count - 1} connections exist in the graph");
            r.Step(StepPhase.Respond, "highlight_path + animate_flow");

            r.Commands.Add(new SceneCommand { kind = "highlight_path", nodes = path.ToArray() });
            r.Commands.Add(new SceneCommand { kind = "animate_flow", nodes = path.ToArray() });
            r.Answer = role switch
            {
                Role.ProductManager => "When a customer pays, we check the payment is allowed, screen it for fraud, then record it so their balance updates.",
                Role.Architect => "One linear chain with an external fraud call. PAYPOST is the coupling point: online and batch both converge there before the ledger write.",
                _ => "PAYENTRY builds the record, PAYVALID checks it against CUSTFILE, PAYPOST screens it with FRAUDCHK and calls LEDGER01, which updates ACCTFILE.",
            };
            return r;
        }

        static Result Blast(LocalAtlasBackend db, Role role, string id)
        {
            var r = new Result();
            var n = db.Node(id);
            var br = db.BlastRadius(id);
            var direct = br.Where(b => b.depth == 1).Select(b => b.id).ToList();
            var features = br.Select(b => db.Node(b.id).Feature).Append(n.Feature).Distinct().ToList();

            r.Step(StepPhase.Understand, $"{RoleNames.Short(role)} wants to know what depends on {n.Id}.");
            r.Step(StepPhase.Plan, "Walk callers upstream and rank them by distance.");
            r.Step(StepPhase.Investigate, $"get_neighbors({n.Id}) → {direct.Count} direct caller(s)");
            r.Step(StepPhase.Investigate, $"blast_radius({n.Id}) → {br.Count} dependent piece(s)");
            r.Step(StepPhase.Check, "Every dependent verified in the graph");
            r.Step(StepPhase.Respond, "show_blast_radius");

            r.Commands.Add(db.BlastCommand(id));
            if (br.Count == 0)
            {
                r.Answer = $"Nothing calls {n.Title}, so changes stay local. Check what it writes to before shipping.";
                return r;
            }
            r.Answer = role switch
            {
                Role.ProductManager => $"Changing {n.Title} would affect {Join(features)}, so every customer who uses them.",
                Role.Architect => $"{n.Title} has {br.Count} dependents across {features.Count} features. Put a boundary in front of it before refactoring.",
                _ => $"{br.Count} dependents. Direct: {Join(direct)}. Add a regression test around {n.File} before you change it.",
            };
            return r;
        }

        static Result Layer(LocalAtlasBackend db, Role role, string layer)
        {
            var r = new Result();
            var features = db.AllNodes.Select(n => n.Feature).Distinct().ToList();
            r.Step(StepPhase.Understand, $"{RoleNames.Short(role)} wants the {layer} view of the system.");
            r.Step(StepPhase.Plan, layer == "business" ? "Group every program by the business feature it serves." : "Lay pieces out by call structure.");
            r.Step(StepPhase.Investigate, $"get_node × {db.AllNodes.Count} → {features.Count} features");
            r.Step(StepPhase.Check, "Every piece maps to exactly one feature");
            r.Step(StepPhase.Respond, $"set_layer({layer})");
            r.Commands.Add(new SceneCommand { kind = "clear" });
            r.Commands.Add(new SceneCommand { kind = "set_layer", layer = layer });
            r.Answer = layer == "code"
                ? "Back to the code view: programs, batch jobs and data stores."
                : role == Role.ProductManager
                    ? $"It does {features.Count} things for customers: {Join(features.Select(f => f.ToLowerInvariant()).ToList())}."
                    : $"Regrouped into {features.Count} business features. Payments and billing meet at the account files.";
            return r;
        }

        static Result Explain(LocalAtlasBackend db, Role role, string id)
        {
            var r = new Result();
            var n = db.Node(id);
            r.Step(StepPhase.Understand, $"{RoleNames.Short(role)} is asking about {n.Id}.");
            r.Step(StepPhase.Investigate, $"get_node({n.Id}) → {n.Feature}");
            r.Step(StepPhase.Investigate, $"read_file({n.File})");
            r.Step(StepPhase.Check, "Summary matches the source");
            r.Step(StepPhase.Respond, $"focus_node({n.Id})");
            r.Commands.Add(new SceneCommand { kind = "focus_node", node = n.Id });
            r.Answer = n.SummaryFor(role);
            return r;
        }

        static Result Reset(Role role)
        {
            var r = new Result { StepSeconds = 0.35f };
            r.Step(StepPhase.Understand, "Reset requested.");
            r.Step(StepPhase.Respond, "clear");
            r.Commands.Add(new SceneCommand { kind = "clear" });
            r.Answer = "Back to the full map.";
            return r;
        }

        static Result Fallback(Role role)
        {
            var r = new Result { StepSeconds = 0.5f };
            r.Step(StepPhase.Understand, $"{RoleNames.Short(role)} asked something I can't map to the graph yet.");
            r.Step(StepPhase.Respond, "No scene change");
            r.Answer = "I can trace a flow, show what breaks if something changes, or regroup the map by business feature.";
            return r;
        }

        static string Mentioned(LocalAtlasBackend db, string q)
        {
            foreach (var n in db.AllNodes.OrderByDescending(n => n.Title.Length))
                if (q.Contains(n.Id.ToLowerInvariant()) || q.Contains(n.Title.ToLowerInvariant())) return n.Id;
            return null;
        }

        static bool Has(string q, params string[] words) => words.Any(q.Contains);

        static string Join(List<string> items) => items.Count switch
        {
            0 => "nothing",
            1 => items[0],
            _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
        };
    }
}

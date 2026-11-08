using System.Collections.Generic;
using UnityEngine;

namespace Atlas
{
    /// Pre-built graph of the demo COBOL payment system (CardDemo-style).
    /// In production this is loaded into the SpacetimeDB `node`/`edge` tables
    /// by the load_graph reducer; this copy is only for the local backend.
    public static class DemoGraph
    {
        const float C0 = -0.36f, C1 = -0.12f, C2 = 0.12f, C3 = 0.36f;
        const float Back = 0.17f, Mid = 0f, Front = -0.17f;

        public static List<ArchNode> Nodes() => new()
        {
            N("SETTLE01", "Batch settlement", "Settle & post", NodeKind.Batch, NodeStatus.NotExplored, "nightly batch", "SETTLE01.jcl", C0, Back,
              "Nightly JCL job that replays the day's queued payments through PAYENTRY. Shares the payment-reference duplicate guard with the online path.",
              "Runs every night to settle the day's payments with the bank.",
              "Second entry into the payment core. Any new boundary must keep batch and API sharing the same idempotency key."),
            N("PAYENTRY", "Payment entry", "Take payments", NodeKind.Program, NodeStatus.Understood, "input", "PAYENTRY.cbl", C0, Mid,
              "Online entry point. Reads the payment request from the CICS screen, builds PAYMENT-RECORD and hands it to PAYVALID. Nothing is written here.",
              "Where a customer's payment first arrives, from the website or a branch teller. Nothing is charged yet.",
              "Thin adapter between CICS and the payment core. Safe to replace with an API gateway without touching the core."),
            N("PAYVALID", "Validate payment", "Take payments", NodeKind.Program, NodeStatus.Understood, "rules", "PAYVALID.cbl", C1, Mid,
              "Checks amount, account status and card limits by reading CUSTFILE, then sets PAYMENT-VALID and calls PAYPOST. Pure rules, easy to unit test.",
              "Makes sure a payment is allowed: the account is open, the amount is sensible, the card is under its limit.",
              "Mostly stateless rules with one read dependency. The cleanest extraction candidate in the payment path."),
            N("PAYPOST", "Post payment", "Take payments", NodeKind.Program, NodeStatus.Risk, "elevated risk", "PAYPOST.cbl", C2, Mid,
              "Checks the duplicate flag, calls FRAUDCHK, then writes to LEDGER01. A shared WS-DUPLICATE flag crosses paragraphs, and batch and online both enter here.",
              "The moment a payment becomes real. If this goes wrong, a customer can be charged twice or not at all.",
              "The coupling hotspot: online and batch converge here, with an external fraud call before the ledger write. Change it last, behind a test harness."),
            N("FRAUDCHK", "Fraud gateway", "Fraud protection", NodeKind.External, NodeStatus.External, "external · inspect next", "vendor API", C2, Back,
              "Synchronous CALL to the vendor fraud API. Times out after 3 seconds; on timeout PAYPOST holds the payment for review.",
              "An outside service that flags suspicious payments before we record them.",
              "External dependency on the critical path. Its latency and outages set PAYPOST's availability."),
            N("CUSTFILE", "Customer file", "Customer accounts", NodeKind.DataStore, NodeStatus.NotExplored, "shared data", "CUSTFILE (VSAM)", C1, Front,
              "VSAM KSDS keyed by customer id. Read by PAYVALID; the record layout lives in CUSTREC.cpy.",
              "Who our customers are: names, addresses and account status.",
              "Shared data contract. Changing its layout touches every program that copies CUSTREC.cpy."),
            N("LEDGER01", "Ledger writer", "Settle & post", NodeKind.Program, NodeStatus.Risk, "high complexity", "LEDGER01.cbl", C2, Front,
              "Writes double-entry postings and updates ACCTFILE balances. About 1,800 lines with three GO TO loops; the riskiest program to change.",
              "Keeps the official record of every payment and updates customer balances.",
              "System of record. Keep its write contract stable; wrap it rather than rewrite it."),
            N("ACCTFILE", "Account master", "Customer accounts", NodeKind.DataStore, NodeStatus.NotExplored, "balances", "ACCTFILE (VSAM)", C3, Front,
              "VSAM file holding balances and credit limits. Written by LEDGER01, read by STMTGEN.",
              "Each customer's current balance and credit limit.",
              "Hub data store: every feature that reads balances is coupled through this file."),
            N("STMTGEN", "Statements", "Billing", NodeKind.Batch, NodeStatus.NotExplored, "monthly batch", "STMTGEN.cbl", C3, Mid,
              "Monthly batch that reads ACCTFILE and the interest results to print customer statements.",
              "Produces the monthly bill each customer receives.",
              "Read-only consumer of balances. A good first service to extract for billing."),
            N("INTCALC", "Interest calc", "Billing", NodeKind.Program, NodeStatus.NotExplored, "rules", "INTCALC.cbl", C3, Back,
              "Computes daily interest per account; results feed STMTGEN.",
              "Works out how much interest each customer owes.",
              "Pure calculation; easy to port once its inputs are made explicit."),
        };

        public static List<ArchEdge> Edges() => new()
        {
            E("SETTLE01", "PAYENTRY", "replays"),
            E("PAYENTRY", "PAYVALID", "validates"),
            E("PAYVALID", "PAYPOST", "calls"),
            E("PAYVALID", "CUSTFILE", "reads"),
            E("PAYPOST", "FRAUDCHK", "screens"),
            E("PAYPOST", "LEDGER01", "writes"),
            E("LEDGER01", "ACCTFILE", "updates"),
            E("INTCALC", "STMTGEN", "feeds"),
            E("STMTGEN", "ACCTFILE", "reads"),
        };

        static ArchNode N(string id, string title, string feature, NodeKind kind, NodeStatus status, string tag, string file,
            float x, float z, string eng, string pm, string arch) => new()
        {
            Id = id, Title = title, Feature = feature, Kind = kind, Status = status, Tag = tag, File = file,
            CodePos = new Vector2(x, z), EngineerSummary = eng, PmSummary = pm, ArchitectSummary = arch,
        };

        static ArchEdge E(string from, string to, string label) => new() { From = from, To = to, Label = label };
    }
}

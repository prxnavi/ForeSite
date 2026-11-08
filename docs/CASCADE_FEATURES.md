# Cascade

**A walk-in digital twin that reasons about buildings.** Put on a Meta Quest and step inside the building. A team of eight NVIDIA Nemotron agents investigates the building in front of you, using a structural engine, the building's blueprint and live data. The whole backend, agents included, runs inside SpacetimeDB.

---

## The numbers

Every number below was measured on the live system: the SpacetimeDB database `cascade-prxnavi` on Maincloud. The headset's **Metrics** panel reads them live, and they're also served at `GET /route/metrics`.

| | |
|---|---|
| **15 / 15 vs 11 / 15** | Nemotron *with* Cascade's SpacetimeDB tools vs the **same model** given the full blueprint but no tools. The agent system, not just the model, makes the reasoning right. |
| **4× faster** | Per question with tools (5.4 s median) vs without (~24 s, and 3+ minutes on beam sizing, where the tool-less model gave no answer at all). |
| **56 s / 21 s** | A full eight-agent investigation, from question to a fact-checked spoken decision (equipment risk / wall change). Down from 146 s and 80 s in the previous design. |
| **11 model calls** | Per investigation, down from 58: the database gathers, computes and verifies, and the model only reasons. |
| **2 / 2** | Investigations that reached the correct conclusion (graded automatically by the system). |
| **100%** | Spoken answers that passed the claim verifier: every number, name and load-bearing statement traced to the data. |
| **15 / 15 vs 7 / 15** | Blueprint-aware reasoning vs a floor-plan-only baseline. |
| **8 / 8** | Load-bearing walls found. The plan-only baseline finds 6 and misses exactly the two that matter. |
| **100%** | Evidence citing a real source row. Invalid claims are rejected before they reach the team. |
| **~38 ms** | Median time from a reducer call to the live update on every connected headset (p95 48 ms). |
| **~1,700 / s** | Full 24-hour cascade simulations per second, run inside the database. |
| **17 ms** | A 50-run Monte Carlo risk estimate, in a single transaction. |
| **3.7 ms** | Every wall in the building sized for removal, plus all load paths. |
| **133 / 133** | Nemotron calls completed. Median latency 1.2 s. |

---

## Features and the technology behind them

### 1. Walk-in digital twin, in mixed reality
*Unity 6 · OpenXR · Meta Quest 3 / 3S · XR Interaction Toolkit · XR Hands · AR Foundation*

- **Walk through the building at full scale.** You're surrounded by solid walls, ceilings with light panels, door signs and floor finishes. Floors shift from cool to amber to red as heat spreads through the simulation.
- **Tabletop Overview in passthrough.** The same building shrinks to a model on your real table. Passthrough is drawn with Meta's OpenXR composition layers.
- **"Take me there."** One tap teleports you to the issue, wall or asset in question, facing it.
- **X-ray and Structure views.** X-ray turns walls to glass to reveal pipes and ducts. Structure view colors every wall by how close it is to its load capacity.
- **Hand tracking and controllers** both work. Panels follow you lazily and keep the middle of your view clear.

### 2. Blueprint → building knowledge graph
*SpacetimeDB tables and reducers · HTTP `POST /blueprint` · CubiCasa5K-style plan annotations + structural framing sheet*

- **One-step blueprint ingest.** A plan (walls, rooms, doors) plus its framing sheet (joist spans, roof loads, rooftop equipment) becomes a knowledge graph in **a single ACID transaction**.
- **The graph:** walls, roof bays, rooftop point loads, rooms and the foundation, linked by `supports`, `carries`, `bounds` and `separates_fire` edges. Agents query it like an engineer reading a drawing set.
- **Live load paths.** Ask what holds something up and the graph traces it to the foundation: *cooling tower → mechanical roof bay → east wall → footing*.

### 3. Structural reasoning engine
*TypeScript reducers running next to the data*

- **Tributary load analysis.** Each roof bay's area load is distributed onto the walls that carry it, and rooftop equipment loads are split between supports by the lever rule. Every wall gets a load (kN/m), a capacity and a utilization.
- **Cracking reduces capacity.** Inspection findings lower a wall's capacity. The chiller-pad wall goes from 69% to 82% utilized.
- **"What if we remove this wall?"** The engine sizes a steel beam against bending and deflection (L/360), picking the lightest adequate W-shape. It also computes the post loads and checks fire separation, exterior walls, damage and what runs inside the wall. The answer is one of five verdicts: *go*, *go with beam*, *not now*, *not allowed* or *not feasible*, each with cost, schedule and the reasons.
- **Crack-growth forecast.** Chiller vibration drives a crack-growth estimate: *0.4 mm → 1.5 mm repair limit in about 21 weeks*.

### 4. Nemotron multi-agent team, reasoning pipeline v2
*NVIDIA Nemotron 3.5 Super (120B, reasoning) + Nemotron 3 Nano · tool calling · run inside SpacetimeDB procedures*

- **Eight agents:** Planner, Signal Analyst, Maintenance Historian, Lease Analyst, **Structural Engineer**, Simulation Engineer, Critic and Narrator.
- **The model reasons, the database verifies.** Each stage is one structured Nemotron call bracketed by deterministic work in SpacetimeDB: tools are pre-run into a briefing, outputs are checked, and simulations are exact.

  | Stage | Agent | What happens |
  |---|---|---|
  | Plan | Planner (Super) | One structured call over a database briefing |
  | Gather | 4 specialists in parallel (Nano) | Each turns pre-run tool results into evidence. Findings whose numbers aren't in the data are rejected and retried. |
  | Hypothesize | Planner (Super, reasoning) | Distinct root causes that must cite evidence ids; duplicates are merged |
  | Simulate / compare | Sim Engineer | The cascade engine runs exactly: every action re-simulated and ranked |
  | Critique | Critic (Super, reasoning) | Judges a **database-computed audit** (source independence, contradictions, unverified assumptions). Send-backs become deterministic checks. |
  | Decide | Planner (Super) | A structured decision validated against the evidence and the engine's verdict |
  | Narrate | Narrator (Super) | Writes from a fact sheet, then the **claim verifier** checks every number, tenant and load-bearing claim. One retry, then a verified template. |
- **The database is the agent runtime.** The tool-calling loop runs *inside* SpacetimeDB procedures (`ctx.http.fetch`). Every tool call is a short transaction against the blackboard, so there's no separate agent server.
- **Blackboard coordination.** Agents never talk to each other directly. They post subtasks, evidence, hypotheses, action candidates, critiques and decisions as rows, and every headset watches them appear live.
- **Specialists work in parallel.** The database pre-fetches each specialist's briefing so it can act in one turn.
- **A Critic that pushes back.** In live runs it independently asked whether Chiller 1 could carry the load during the repair, then approved once the simulation proved it could.
- **Grounding that can't be skipped.** Evidence must cite an existing row, ids are validated, and prices come from the engine rather than the model. Bad claims are rejected before they reach the blackboard (1 of 138 tool calls was caught in testing).
- **Reasoning mode only where it pays off.** The Planner's hypotheses and the Critic think step by step; specialists run on the faster Nano model.
- **Answers for each role, fact-checked before they're spoken.** The Narrator writes a short answer for the technician (where to go, what to check) and for the manager (risk, cost, recommendation). The claim verifier rejects any number that isn't in the data, any tenant who isn't involved, and any "not load-bearing" said about a bearing wall.
- **Grades its own work.** Every finished investigation is scored automatically (right root cause and plan, or the right wall verdict) and timed. Results land on the **Metrics** panel.
- **Never stalls.** If a model call fails or times out, that phase falls back to a deterministic step and the investigation still finishes.

### 5. Cascade simulation and impact
*Deterministic reducers · Monte Carlo with the database's seeded RNG*

- **24-hour forecast in 10-minute steps.** A chiller failure spreads through air handlers to rooms and tenants, with backup capacity and load shifting modeled.
- **50 randomized futures** give the outage probability and its likely window, drawn as a "risk cloud" on the timeline.
- **Impact math:** downtime cost per tenant, uptime-clause penalties, emergency vs planned repair costs.
- **Before and after.** Approve the plan and a follow-up simulation verifies the outage is avoided (example: **$244k → $4.8k**).

### 6. SpacetimeDB is the whole backend

| SpacetimeDB capability | How Cascade uses it |
|---|---|
| Logic runs inside the database | Structural engine, cascade simulation, evaluation grader and the Nemotron agent loop all run next to the data |
| ACID reducers | Blueprint ingest, approvals and work orders each commit all-or-nothing |
| Procedures with outbound HTTP | Every Nemotron call happens from inside the database |
| Scheduled reducers | Sensor replay, the anomaly watchdog, agent phases and the evaluation runner |
| Module-served HTTP | `GET /metrics`, `GET /structure`, `GET /health`, `POST /sensor`, `POST /blueprint` |
| Live subscriptions | Every agent step, wall proposal and metric streams to both headsets, ~38 ms median |
| Event tables | Team pings and the latency probe: broadcast to subscribers, never stored |
| Row-level security | Private field notes reach only their author |
| Identity | Each headset has its own identity; only the owner can set the NVIDIA key or record metrics |
| Private tables | The API key and agent plans never leave the database |

### 7. A proactive watchdog
- A scheduled reducer scores every sensor against its baseline and **opens an investigation on its own** when something drifts. Nobody has to ask first.
- Sensor data streams in over HTTP (`POST /sensor`) at a median of about 44 ms per reading.

### 8. Inspection findings where they physically are
- **Structural cracks** run across the wall from the chiller pad. A **water stain** spreads on the server-room ceiling, with drips falling. A **corroded pipe joint** drips in the corridor. **Alarm rings** pulse around failing equipment.
- Each finding has a severity pin with its source (inspection report, ticket or sensor), **Take me there**, and **Ask the team**.

### 9. Voice and spoken answers
*whisper.unity (whisper.cpp) on-device · Android TextToSpeech*

- **Hold to talk.** Speech is transcribed **on the headset itself**: no cloud and no key, and it works offline. Silence is trimmed and the model only processes as much audio as you spoke.
- **Voice commands:** "approve the plan", "take me there", "show the structure", "x-ray", "metrics". Anything else goes to the agent team as a question.
- Answers are spoken aloud, tailored to whether you're the technician or the manager.

### 10. Two headsets, one building: SpacetimeDB multiplayer
- **Any number of Quests join the same database**, each with its own identity and name. Investigations, wall proposals, approvals, the timeline and positions sync to every headset in about 38 ms.
- **Technician** walks the building life-size. **Manager** sees the tabletop overview, approves plans and sends the technician to rooms. Other people appear as floor rings with name tags, updated 10 times a second.
- **Ping team.** Flag any asset, wall, room or finding and a light beacon appears on every headset, with "Quest 7F2A flagged Server Room 300" spoken aloud. It's delivered over a SpacetimeDB **event table**: instant and never stored.
- **Field notes the agents read.** Say "note: rack B intake is warm" and it's pinned there for the team, and the Historian picks it up as evidence in the next investigation.
- **Private notes enforced by the database.** "Private note …" is only ever sent to its author's headset, through SpacetimeDB **row-level security**.
- **Approval gate:** only the manager can turn proposals into work orders and tenant notices.

### 11. A built-in evaluation harness
- **15 ground-truth cases** (wall classification, wall removal, load paths), graded automatically inside the database.
- **Four reasoners on the same questions:** the floor-plan-only baseline, the framing-aware engine, Nemotron **without** tools (ablation) and Nemotron **with** Cascade's tools.

  | Reasoner | Accuracy |
  |---|---|
  | Nemotron + SpacetimeDB tools | **15/15** |
  | Framing-aware engine | 15/15 |
  | Same Nemotron, blueprint in prompt, no tools | 11/15 |
  | Floor-plan-only guess | 7/15 |
- Every tool call and model call is logged with latency and tokens. The results feed the **Metrics** panel.

---

## Architecture

```
 Meta Quest (Unity 6, OpenXR)            NVIDIA Nemotron (build.nvidia.com)
   walk-in twin · tabletop · voice            ▲ tool-calling, reasoning mode
          │  live subscriptions (~38 ms)      │ ctx.http.fetch
          ▼                                   │
 ┌──────────────────────── SpacetimeDB  (module: cascade-prxnavi) ──────────────────────┐
 │  Blueprint → knowledge graph   (element, element_edge, wall)                          │
 │  Structural engine             (tributary loads · beam sizing · code checks)          │
 │  Cascade simulation            (24 h · 50-run Monte Carlo)                            │
 │  Agent blackboard              (investigation · subtask · evidence · hypothesis ·     │
 │                                 action_candidate · critique · decision · answer)      │
 │  8-agent Nemotron runtime      (scheduled procedures, phase machine, fallbacks)       │
 │  Eval harness + metrics        (eval_case · eval_result · tool_call · llm_call)       │
 │  HTTP API                      (/metrics · /structure · /sensor · /blueprint)         │
 └───────────────────────────────────────────────────────────────────────────────────────┘
```

## Tech stack

| Layer | Technology |
|---|---|
| Headset | Meta Quest 3 / 3S, Unity 6000.6, OpenXR, Meta OpenXR (passthrough, composition layers), XR Interaction Toolkit, XR Hands, AR Foundation |
| Rendering and UI | Built-in pipeline, custom unlit shader, world-space uGUI + TextMeshPro, runtime-generated geometry |
| Backend | SpacetimeDB 2.14 (TypeScript module on Maincloud), C# client SDK |
| AI | NVIDIA Nemotron 3.5 Super 120B (reasoning), Nemotron 3 Nano, OpenAI-compatible tool calling |
| Voice | whisper.unity (whisper.cpp, base.en) on-device; Android TextToSpeech |
| Benchmarks | In-database evaluation harness; a Node TypeScript client for latency measurement |

// Cascade — SpacetimeDB module (the whole backend).
//
// One building graph fused from five sources (plan, sensors, work orders, asset
// registry, leases), a cascade simulation, and an agent blackboard. Headsets and
// agent workers are all clients of these tables and reducers.
//
// The built-in investigation below runs the spec's 10-step flow as a team of
// agents (Planner, Signal Analyst, Historian, Lease Analyst, Sim Engineer, Critic,
// Narrator). Every number it says comes from a real table read or simulation run.
// Nemotron (optional) rewrites the Narrator's answers when an NVIDIA key is set.

import { schema, table, t, SenderError, Router, SyncResponse, type InferSchema, type ReducerCtx, type ProcedureCtx } from 'spacetimedb/server';
import { ScheduleAt, TimeDuration } from 'spacetimedb';

// ================================================================ tables

// ---- building
const room = table({ name: 'room', public: true }, {
  id: t.string().primaryKey(),
  name: t.string(),
  kind: t.string(), // office | server | common | mech | corridor
  x0: t.f32(), z0: t.f32(), x1: t.f32(), z1: t.f32(), // meters on the floor plan
  doorX: t.f32(), doorZ: t.f32(), // door onto the corridor
  tenantId: t.string(),
  demand: t.f32(), // cooling demand, tons
  startTemp: t.f32(),
  limitTemp: t.f32(),
  warmRate: t.f32(), // °F per hour with zero cooling
});

const asset = table({ name: 'asset', public: true }, {
  id: t.string().primaryKey(),
  name: t.string(),
  kind: t.string(), // panel | chiller | ahu
  roomId: t.string(),
  x: t.f32(), z: t.f32(),
  installYear: t.u32(),
  expectedLife: t.u32(),
  capacity: t.f32(), // tons (chillers)
  reading: t.f32(), // live sensor value
  readingUnit: t.string(),
  readingLabel: t.string(),
  baseline: t.f32(),
  health: t.string(), // ok | watch | critical | repair
});

const dependency = table({ name: 'dependency', public: true }, {
  id: t.u64().primaryKey().autoInc(),
  fromId: t.string(), // asset
  toId: t.string(), // asset or room
  kind: t.string(), // power | chilled_water | air
});

// ---- operations data
const sensorReading = table({ name: 'sensor_reading', public: true }, {
  id: t.u64().primaryKey().autoInc(),
  assetId: t.string().index('btree'),
  hoursAgo: t.f32(),
  value: t.f32(),
});

const workOrder = table({ name: 'work_order', public: true }, {
  id: t.string().primaryKey(),
  assetId: t.string(),
  title: t.string(),
  status: t.string(), // open | closed | scheduled
  ageDays: t.u32(),
  createdBy: t.string(), // cmms | cascade
  cost: t.f32(),
});

const tenant = table({ name: 'tenant', public: true }, {
  id: t.string().primaryKey(),
  name: t.string(),
  business: t.string(),
  costPerHour: t.f32(),
  allowedDowntimeHours: t.f32(),
  penalty: t.f32(),
  clause: t.string(),
});

const pastIncident = table({ name: 'past_incident', public: true }, {
  id: t.string().primaryKey(),
  assetId: t.string(),
  summary: t.string(),
  hoursDown: t.f32(),
  cost: t.f32(),
});

// Inspection findings you can see in the walk-through (structural, water, mechanical...).
const issue = table({ name: 'issue', public: true }, {
  id: t.string().primaryKey(),
  kind: t.string(), // structural | water | mechanical | electrical
  severity: t.string(), // low | medium | high | critical
  title: t.string(),
  detail: t.string(),
  source: t.string(),
  roomId: t.string(),
  assetId: t.string(),
  surface: t.string(), // wall | ceiling | floor | pipe | equipment
  x: t.f32(), y: t.f32(), z: t.f32(), // plan meters + height
  nx: t.f32(), nz: t.f32(), // which way the surface faces (walls)
  status: t.string(), // open | scheduled | resolved
});

// Walls as structural elements (load-bearing vs partition, load, condition, what runs inside).
const wall = table({ name: 'wall', public: true }, {
  id: t.string().primaryKey(),
  name: t.string(),
  x0: t.f32(), z0: t.f32(), x1: t.f32(), z1: t.f32(),
  kind: t.string(), // exterior | bearing | partition
  material: t.string(),
  utilization: t.f32(), // share of capacity in use (0..1)
  carries: t.string(),
  health: t.string(), // ok | watch | damaged
  services: t.string(), // what runs inside it
  proposal: t.string(), // '' | remove
  proposalState: t.string(), // '' | evaluating | recommended | not_recommended | approved
  loadKnm: t.f32(), // line load from the framing analysis (kN/m)
  capacityKnm: t.f32(), // allowable line load (kN/m), reduced if cracked
  fireRated: t.bool(),
  planOnlyBearing: t.bool(), // what the floor plan alone suggests
  planOnlyWhy: t.string(),
});

// ---- blueprint knowledge graph
const blueprint = table({ name: 'blueprint', public: true }, {
  id: t.string().primaryKey(), name: t.string(), source: t.string(), json: t.string(), loadedAt: t.timestamp(),
});
const element = table({ name: 'element', public: true }, {
  id: t.string().primaryKey(),
  kind: t.string(), // wall | bay | point_load | room | foundation
  label: t.string(),
  x0: t.f32(), z0: t.f32(), x1: t.f32(), z1: t.f32(),
  value: t.f32(), unit: t.string(), detail: t.string(),
});
const elementEdge = table({ name: 'element_edge', public: true }, {
  id: t.u64().primaryKey().autoInc(),
  fromId: t.string().index('btree'),
  toId: t.string().index('btree'),
  rel: t.string(), // supports | carries | bounds | separates_fire
  value: t.f32(),
});

// ---- evaluation + metrics
const evalCase = table({ name: 'eval_case', public: true }, {
  id: t.string().primaryKey(), question: t.string(), kind: t.string(), subject: t.string(), expected: t.string(),
});
const evalResult = table({ name: 'eval_result', public: true }, {
  id: t.u64().primaryKey().autoInc(), runId: t.string(), caseId: t.string(), mode: t.string(),
  answer: t.string(), correct: t.bool(), latencyMs: t.f32(), toolCalls: t.u32(), grounded: t.bool(),
});
const metric = table({ name: 'metric', public: true }, {
  key: t.string().primaryKey(), label: t.string(), value: t.f64(), unit: t.string(), detail: t.string(), group: t.string(), at: t.timestamp(),
});
const toolCall = table({ name: 'tool_call', public: true }, {
  id: t.u64().primaryKey().autoInc(), invId: t.u64(), agent: t.string(), tool: t.string(), ok: t.bool(), error: t.string(), model: t.string(), ms: t.f32(),
});
const llmCall = table({ name: 'llm_call', public: true }, {
  id: t.u64().primaryKey().autoInc(), invId: t.u64(), agent: t.string(), model: t.string(), ms: t.f32(),
  promptTokens: t.u32(), completionTokens: t.u32(), ok: t.bool(),
});
// Event table: rows are broadcast to subscribers and never stored (latency probe).
const benchEcho = table({ name: 'bench_echo', public: true, event: true }, {
  seq: t.u32(), at: t.timestamp(),
});

// ---- collaboration
// Pings are an event table: broadcast to every headset instantly, never stored.
const ping = table({ name: 'ping', public: true, event: true }, {
  fromName: t.string(), fromRole: t.string(), targetId: t.string(), x: t.f32(), z: t.f32(), kind: t.string(), label: t.string(),
});
// Field notes from people on site. Private notes are hidden from everyone but their author by row-level security.
const fieldNote = table({ name: 'field_note', public: true }, {
  id: t.u64().primaryKey().autoInc(),
  author: t.identity(),
  authorName: t.string(),
  role: t.string(),
  targetId: t.string(),
  x: t.f32(), z: t.f32(),
  text: t.string(),
  isPrivate: t.bool(),
  at: t.timestamp(),
});

const notice = table({ name: 'notice', public: true }, {
  id: t.u64().primaryKey().autoInc(),
  tenantId: t.string(),
  text: t.string(),
});

// ---- simulation
const scenario = table({ name: 'scenario', public: true }, {
  id: t.u64().primaryKey().autoInc(),
  invId: t.u64(),
  label: t.string(), // do_nothing | with_plan
  title: t.string(),
  verified: t.bool(), // follow-up run after the plan executed
  outageChance: t.f32(), // 0..1 across 50 runs
  outageHour: t.f32(), // hours after start of first outage (-1 none)
  impact: t.f32(), // dollars
  downtimeHours: t.f32(),
  tenantsAffected: t.u32(),
  breaches: t.u32(),
  framesJson: t.string(),
  riskJson: t.string(), // server-room temperature per frame for each of 50 runs
});

// ---- agent blackboard
const investigation = table({ name: 'investigation', public: true }, {
  id: t.u64().primaryKey().autoInc(),
  trigger: t.string(),
  source: t.string(), // ask | watchdog
  askedBy: t.string(),
  status: t.string(), // investigating | awaiting_approval | executing | resolved
  createdAt: t.timestamp(),
  mode: t.string(), // nemotron | scripted
  phase: t.string(), // current phase of the Nemotron team
  phasesDone: t.string(), // comma list
  critiqueRounds: t.u32(),
  pending: t.string(), // revision phases still running (comma list)
  kind: t.string(), // cascade | structural_change
  subject: t.string(), // wall id for structural changes
});

const subtask = table({ name: 'subtask', public: true }, {
  id: t.u64().primaryKey().autoInc(),
  invId: t.u64(),
  agent: t.string(),
  goal: t.string(),
  dependsOn: t.string(),
  status: t.string(), // waiting | ready | claimed | done | reopened
});

const evidence = table({ name: 'evidence', public: true }, {
  id: t.u64().primaryKey().autoInc(),
  invId: t.u64(),
  agent: t.string(),
  fact: t.string(),
  sourceTable: t.string(),
  sourceRow: t.string(),
  confidence: t.f32(),
  hypothesisId: t.u64(),
  supports: t.bool(),
  aboutAsset: t.string(),
});

// Many-to-many: which evidence supports or contradicts which hypothesis.
const hypothesisEvidence = table({ name: 'hypothesis_evidence', public: true }, {
  id: t.u64().primaryKey().autoInc(),
  invId: t.u64(),
  hypothesisId: t.u64(),
  evidenceId: t.u64(),
  supports: t.bool(),
});

const hypothesis = table({ name: 'hypothesis', public: true }, {
  id: t.u64().primaryKey().autoInc(),
  invId: t.u64(),
  assetId: t.string(),
  text: t.string(),
  likelihood: t.f32(),
});

const actionCandidate = table({ name: 'action_candidate', public: true }, {
  id: t.u64().primaryKey().autoInc(),
  invId: t.u64(),
  kind: t.string(), // work_order | load_shift | tenant_notice | portable_cooling
  target: t.string(),
  title: t.string(),
  detail: t.string(),
  cost: t.f32(),
  saved: t.f32(), // dollars of expected impact avoided (from simulation)
  rank: t.u32(),
  recommended: t.bool(),
  status: t.string(), // proposed | approved | done
});

const critique = table({ name: 'critique', public: true }, {
  id: t.u64().primaryKey().autoInc(),
  invId: t.u64(),
  verdict: t.string(), // revise | approve
  text: t.string(),
});

const decision = table({ name: 'decision', public: true }, {
  id: t.u64().primaryKey().autoInc(),
  invId: t.u64(),
  rootCause: t.string(),
  likelihood: t.f32(),
  summary: t.string(),
});

const answer = table({ name: 'answer', public: true }, {
  id: t.u64().primaryKey().autoInc(),
  invId: t.u64(),
  role: t.string(), // technician | manager
  text: t.string(),
  byNemotron: t.bool(),
});

const agentStep = table({ name: 'agent_step', public: true }, {
  id: t.u64().primaryKey().autoInc(),
  invId: t.u64(),
  seq: t.u32(),
  agent: t.string(), // planner | signal | historian | lease | simulation | critic | narrator
  tool: t.string(),
  why: t.string(),
  result: t.string(),
  target: t.string(), // asset/room id the agent is looking at ("" = none)
  status: t.string(), // running | done | revise | error | thinking
  model: t.string(), // which model produced it ("" = scripted)
});

// Nemotron status, readable by headsets (never contains the key).
const aiStatus = table({ name: 'ai_status', public: true }, {
  id: t.u32().primaryKey(),
  enabled: t.bool(),
  modelLarge: t.string(),
  modelSmall: t.string(),
  message: t.string(),
});

// ---- output + people
const sceneCommand = table({ name: 'scene_command', public: true }, {
  id: t.u64().primaryKey().autoInc(),
  payload: t.string(),
});

const user = table({ name: 'user', public: true }, {
  identity: t.identity().primaryKey(),
  name: t.string(),
  role: t.string(), // technician | manager
  x: t.f32(), z: t.f32(), // position on the floor plan (life-size mode)
  present: t.bool(), // standing inside the life-size twin
  online: t.bool(),
});

const timeline = table({ name: 'timeline', public: true }, {
  id: t.u32().primaryKey(),
  hour: t.f32(), // 0..24 after the start clock
  label: t.string(), // which scenario is shown
  built: t.bool(), // twin has been built from the plan
});

// ---- private
const agentPlan = table({ name: 'agent_plan' }, {
  invId: t.u64().primaryKey(),
  cursor: t.u32(),
  opsJson: t.string(),
});
const agentTick = table({ name: 'agent_tick' }, {
  scheduledId: t.u64().primaryKey().autoInc(),
  scheduledAt: t.scheduleAt(),
  invId: t.u64(),
});
const replayTick = table({ name: 'replay_tick' }, {
  scheduledId: t.u64().primaryKey().autoInc(),
  scheduledAt: t.scheduleAt(),
});
const plantState = table({ name: 'plant_state' }, {
  id: t.u32().primaryKey(),
  ticks: t.u32(),
  watchdogFired: t.bool(),
});
const secret = table({ name: 'secret' }, {
  key: t.string().primaryKey(),
  value: t.string(),
});
const owner = table({ name: 'owner' }, {
  id: t.u32().primaryKey(),
  identity: t.identity(),
});
const agentJob = table({ name: 'agent_job' }, {
  scheduledId: t.u64().primaryKey().autoInc(),
  scheduledAt: t.scheduleAt(),
  invId: t.u64(),
  phase: t.string(),
  note: t.string(),
});
const nemotronCheckJob = table({ name: 'nemotron_check_job' }, {
  scheduledId: t.u64().primaryKey().autoInc(),
  scheduledAt: t.scheduleAt(),
});
const evalJob = table({ name: 'eval_job' }, {
  scheduledId: t.u64().primaryKey().autoInc(),
  scheduledAt: t.scheduleAt(),
  caseIds: t.string(),
});
const narrateJob = table({ name: 'narrate_job' }, {
  scheduledId: t.u64().primaryKey().autoInc(),
  scheduledAt: t.scheduleAt(),
  invId: t.u64(),
});

const spacetimedb = schema({
  room, asset, dependency, sensorReading, workOrder, tenant, pastIncident, notice, issue, wall,
  blueprint, element, elementEdge, evalCase, evalResult, metric, toolCall, llmCall, benchEcho, evalJob, ping, fieldNote,
  scenario, investigation, subtask, evidence, hypothesisEvidence, hypothesis, actionCandidate, critique, decision, answer, agentStep,
  aiStatus, sceneCommand, user, timeline, agentPlan, agentTick, replayTick, plantState, secret, owner, agentJob, nemotronCheckJob, narrateJob,
});
export default spacetimedb;

type Ctx = ReducerCtx<InferSchema<typeof spacetimedb>>;

// ================================================================ demo building
// Harbor Point, Floor 3. 24 m × 16 m; a corridor runs along z = 7..9.

const START_CLOCK = 14; // Tuesday 14:00
const VIB_LIMIT = 7.1; // mm/s, vibration alarm limit for the chillers

type RoomSeed = [string, string, string, number, number, number, number, number, number, string, number, number, number, number];
const ROOMS: RoomSeed[] = [
  // id, name, kind, x0, z0, x1, z1, doorX, doorZ, tenant, demand, start, limit, warmRate
  ['MECH', 'Mechanical', 'mech', 0, 0, 5, 7, 3.0, 7, '', 0, 75, 999, 0],
  ['CORR', 'Corridor', 'corridor', 0, 7, 24, 9, 0, 8, '', 0, 72, 999, 0],
  ['LOBBY', 'Lobby', 'common', 0, 9, 5, 16, 2.5, 9, '', 10, 72, 86, 1.2],
  ['S100', 'Suite 100', 'office', 5, 9, 12, 16, 8.5, 9, 'T-BL', 25, 72, 85, 1.5],
  ['S200', 'Suite 200', 'office', 12, 9, 18, 16, 15, 9, 'T-KD', 20, 72, 85, 1.5],
  ['COMMONS', 'Commons', 'common', 18, 9, 24, 16, 21, 9, '', 10, 72, 86, 1.2],
  ['S300', 'Suite 300', 'office', 5, 0, 14, 7, 9.5, 7, 'T-NW', 25, 72, 85, 1.6],
  ['SRV300', 'Server Room 300', 'server', 14, 0, 18, 7, 16, 7, 'T-NW', 20, 68, 80, 9],
  ['S400', 'Suite 400', 'office', 18, 0, 24, 7, 21, 7, 'T-HH', 20, 72, 84, 1.4],
];

type AssetSeed = [string, string, string, string, number, number, number, number, number, number, string, string, number];
const ASSETS: AssetSeed[] = [
  // id, name, kind, room, x, z, installYear, life, capacity, reading, unit, label, baseline
  ['EP-1', 'Electrical Panel 1', 'panel', 'MECH', 1.0, 1.2, 2015, 30, 0, 61, '%', 'load', 60],
  ['CH-1', 'Chiller 1', 'chiller', 'MECH', 1.4, 4.4, 2019, 20, 100, 2.3, 'mm/s', 'vibration', 2.3],
  ['CH-2', 'Chiller 2', 'chiller', 'MECH', 3.6, 4.4, 2012, 15, 100, 5.0, 'mm/s', 'vibration', 2.4],
  ['AHU-1', 'Air Handler 1', 'ahu', 'LOBBY', 2.5, 13.5, 2016, 20, 0, 54, '°F', 'supply air', 54],
  ['AHU-2', 'Air Handler 2', 'ahu', 'S300', 12.6, 1.4, 2016, 20, 0, 55, '°F', 'supply air', 54],
  ['AHU-3', 'Air Handler 3', 'ahu', 'S400', 22.6, 1.4, 2016, 20, 0, 54, '°F', 'supply air', 54],
];

const DEPS: [string, string, string][] = [
  ['EP-1', 'CH-1', 'power'], ['EP-1', 'CH-2', 'power'],
  ['CH-1', 'AHU-1', 'chilled_water'], ['CH-2', 'AHU-2', 'chilled_water'], ['CH-2', 'AHU-3', 'chilled_water'],
  ['AHU-1', 'LOBBY', 'air'], ['AHU-1', 'S100', 'air'], ['AHU-1', 'S200', 'air'], ['AHU-1', 'COMMONS', 'air'],
  ['AHU-2', 'S300', 'air'], ['AHU-2', 'SRV300', 'air'], ['AHU-3', 'S400', 'air'],
];

const TENANTS: [string, string, string, number, number, number, string][] = [
  ['T-NW', 'Northwind Analytics', 'Data analytics, on-site servers', 18000, 2, 50000, '99.7% monthly uptime; 2 h of downtime allowed, then a $50,000 penalty.'],
  ['T-BL', 'Brightline Legal', 'Law firm', 2500, 8, 0, 'Standard office lease; no uptime clause.'],
  ['T-KD', 'Kestrel Design', 'Design studio', 1200, 8, 0, 'Standard office lease; no uptime clause.'],
  ['T-HH', 'Harbor Health Clinic', 'Outpatient clinic', 6000, 4, 15000, 'Clinic comfort clause: 4 h above 84°F allowed, then $15,000.'],
];

const WORK_ORDERS: [string, string, string, string, number][] = [
  ['WO-4471', 'CH-2', 'Compressor noise, tenant complaint', 'open', 9],
  ['WO-4502', 'CH-2', 'Bearing hum at high load', 'open', 4],
  ['WO-4310', 'AHU-2', 'Replace filter bank', 'closed', 31],
  ['WO-4288', 'CH-1', 'Annual service', 'closed', 64],
  ['WO-4415', 'EP-1', 'Thermal scan', 'closed', 22],
];

const INCIDENTS: [string, string, string, number, number][] = [
  ['INC-2021-07', 'CH-1', 'Chiller bearing seized on a hot afternoon; 9 h without full cooling, emergency repair.', 9, 41000],
  ['INC-2023-02', 'AHU-3', 'Supply fan belt failure; 2 h of reduced airflow in Suite 400.', 2, 1800],
];

type IssueSeed = [string, string, string, string, string, string, string, string, string, number, number, number, number, number];
const ISSUES: IssueSeed[] = [
  // id, kind, severity, title, detail, source, room, asset, surface, x, y, z, nx, nz
  ['ISS-1', 'mechanical', 'critical', 'Chiller 2 bearing vibration', 'Vibration 5.0 mm/s and rising (normal 2.4). Two noise tickets were never linked to it.', 'Vibration sensor · WO-4471 · WO-4502', 'MECH', 'CH-2', 'equipment', 3.6, 1.3, 4.4, 0, 1],
  ['ISS-2', 'structural', 'high', 'Cracks from the chiller pad', 'Hairline cracks run from Chiller 2 anchor bolts into the east wall. Pattern matches years of vibration.', 'Structural inspection, Sept 2025', 'MECH', 'CH-2', 'wall', 4.92, 1.0, 5.9, -1, 0],
  ['ISS-3', 'water', 'high', 'Condensate leak over server racks', 'Air Handler 2 drain pan overflows when it runs hard; ceiling tiles above rack row B are stained.', 'Tenant photo · WO-4310 notes', 'SRV300', 'AHU-2', 'ceiling', 16, 2.68, 3.4, 0, 0],
  ['ISS-4', 'mechanical', 'medium', 'Corroded chilled-water joint', 'Flange on the chilled-water line to Air Handler 2 is corroding; the insulation is wet.', 'Facilities walk-through, Oct 2026', 'CORR', 'AHU-2', 'pipe', 9, 2.45, 7.6, -1, 0],
  ['ISS-5', 'electrical', 'low', 'Panel breaker running warm', 'Breaker 14 on EP-1 reads 9°F above its neighbors at 61% load.', 'Thermal scan WO-4415', 'MECH', 'EP-1', 'equipment', 1.0, 1.5, 1.2, 0, 1],
  ['ISS-6', 'structural', 'low', 'Settlement crack in the slab', '1.5 mm crack across the floor slab; monitored and stable since 2024.', 'Structural inspection, Sept 2025', 'S400', '', 'floor', 21, 0.0, 3.6, 0, 1],
];

function clearTable<T>(rows: Iterable<T>, del: (r: T) => void) { for (const r of [...rows]) del(r); }

function seedBuilding(ctx: Ctx) {
  const db = ctx.db;
  clearTable(db.room.iter(), r => db.room.id.delete(r.id));
  clearTable(db.asset.iter(), r => db.asset.id.delete(r.id));
  clearTable(db.dependency.iter(), r => db.dependency.id.delete(r.id));
  clearTable(db.sensorReading.iter(), r => db.sensorReading.id.delete(r.id));
  clearTable(db.workOrder.iter(), r => db.workOrder.id.delete(r.id));
  clearTable(db.tenant.iter(), r => db.tenant.id.delete(r.id));
  clearTable(db.pastIncident.iter(), r => db.pastIncident.id.delete(r.id));
  clearTable(db.notice.iter(), r => db.notice.id.delete(r.id));
  clearTable(db.issue.iter(), r => db.issue.id.delete(r.id));

  for (const [id, name, kind, x0, z0, x1, z1, doorX, doorZ, tenantId, demand, startTemp, limitTemp, warmRate] of ROOMS)
    db.room.insert({ id, name, kind, x0, z0, x1, z1, doorX, doorZ, tenantId, demand, startTemp, limitTemp, warmRate });
  for (const [id, name, kind, roomId, x, z, installYear, expectedLife, capacity, reading, readingUnit, readingLabel, baseline] of ASSETS)
    db.asset.insert({ id, name, kind, roomId, x, z, installYear, expectedLife, capacity, reading, readingUnit, readingLabel, baseline,
      health: 'ok' });
  for (const [fromId, toId, kind] of DEPS) db.dependency.insert({ id: 0n, fromId, toId, kind });
  for (const [id, name, business, costPerHour, allowedDowntimeHours, penalty, clause] of TENANTS)
    db.tenant.insert({ id, name, business, costPerHour, allowedDowntimeHours, penalty, clause });
  for (const [id, assetId, title, status, ageDays] of WORK_ORDERS)
    db.workOrder.insert({ id, assetId, title, status, ageDays, createdBy: 'cmms', cost: 0 });
  for (const [id, assetId, summary, hoursDown, cost] of INCIDENTS)
    db.pastIncident.insert({ id, assetId, summary, hoursDown, cost });

  // 24 h of vibration history (every 30 min). CH-2 starts climbing 8 h ago.
  // Shape follows replayed BDG2-style meter data; the anomaly is injected on top.
  for (let i = 48; i >= 1; i--) {
    const h = i * 0.5;
    const wobble = (k: number) => Math.sin(h * 1.7 + k) * 0.12 + Math.sin(h * 0.53 + k * 2) * 0.08;
    db.sensorReading.insert({ id: 0n, assetId: 'CH-1', hoursAgo: h, value: 2.3 + wobble(1) });
    const climb = h < 8 ? (8 - h) * 0.32 : 0;
    db.sensorReading.insert({ id: 0n, assetId: 'CH-2', hoursAgo: h, value: 2.4 + wobble(2) + climb });
  }
  // after rooms and assets exist: inspection findings, then the blueprint graph + structural analysis
  for (const [id, kind, severity, title, detail, source, roomId, assetId, surface, x, y, z, nx, nz] of ISSUES)
    db.issue.insert({ id, kind, severity, title, detail, source, roomId, assetId, surface, x, y, z, nx, nz, status: 'open' });
  seedWalls(ctx);
}

// ================================================================ simulation
// 10-minute ticks for 24 hours (144 ticks). Frames are kept every 30 minutes.

const TICK_H = 1 / 6;
const TICKS = 144;
const FRAME_EVERY = 3;
const EMERGENCY_REPAIR_H = 8;
const EMERGENCY_REPAIR_COST = 38000;
const PLANNED_REPAIR_COST = 4800;
const PLANNED_START = 1.5, PLANNED_END = 4.5; // 15:30–18:30
const PORTABLE_TONS = 15;

type Plan = { repair: boolean; loadShift: boolean; notice: boolean; portable: boolean };
const NO_PLAN: Plan = { repair: false, loadShift: false, notice: false, portable: false };

type Building = {
  rooms: { id: string; tenantId: string; demand: number; startTemp: number; limitTemp: number; warmRate: number; kind: string }[];
  chillerOf: Map<string, string>; // room → chiller feeding it
  cap: Map<string, number>;
  tenants: Map<string, { cost: number; allowed: number; penalty: number }>;
};

function loadBuilding(ctx: Ctx): Building {
  const ahuChiller = new Map<string, string>();
  const chillerOf = new Map<string, string>();
  const deps = [...ctx.db.dependency.iter()];
  for (const d of deps) if (d.kind === 'chilled_water') ahuChiller.set(d.toId, d.fromId);
  for (const d of deps) if (d.kind === 'air') chillerOf.set(d.toId, ahuChiller.get(d.fromId) ?? '');
  const cap = new Map<string, number>();
  for (const a of ctx.db.asset.iter()) if (a.kind === 'chiller') cap.set(a.id, a.capacity);
  const tenants = new Map<string, { cost: number; allowed: number; penalty: number }>();
  for (const tn of ctx.db.tenant.iter()) tenants.set(tn.id, { cost: tn.costPerHour, allowed: tn.allowedDowntimeHours, penalty: tn.penalty });
  const rooms = [...ctx.db.room.iter()].filter(r => r.demand > 0)
    .map(r => ({ id: r.id, tenantId: r.tenantId, demand: r.demand, startTemp: r.startTemp, limitTemp: r.limitTemp, warmRate: r.warmRate, kind: r.kind }));
  rooms.sort((a, b) => a.id.localeCompare(b.id));
  return { rooms, chillerOf, cap, tenants };
}

type SimResult = {
  frames: { t: number[]; a: string[]; c: number; ta: number; b: number; h: number }[];
  serverTemps: number[];
  impact: number; downtimeHours: number; tenantsAffected: number; breaches: number;
  outageHour: number; failed: boolean;
};

const SIM_ASSETS = ['CH-1', 'CH-2', 'AHU-1', 'AHU-2', 'AHU-3'];

/** One run of the cascade rules. failHour < 0 means CH-2 never fails. */
function simulate(b: Building, plan: Plan, failHour: number, heatScale = 1): SimResult {
  const temps = new Map(b.rooms.map(r => [r.id, r.startTemp]));
  const downH = new Map<string, number>();
  let repairCost = 0, failed = false, outageHour = -1;
  let cost = 0, breaches = 0;
  const frames: SimResult['frames'] = [];
  const serverTemps: number[] = [];

  // A planned repair before the predicted failure prevents it.
  const willFail = failHour >= 0 && !(plan.repair && failHour >= PLANNED_START);
  if (plan.repair) repairCost += PLANNED_REPAIR_COST;

  const roomsOf = (ch: string) => b.rooms.filter(r => b.chillerOf.get(r.id) === ch);

  for (let tick = 0; tick <= TICKS; tick++) {
    const hour = tick * TICK_H;
    const inPlanned = plan.repair && hour >= PLANNED_START && hour < PLANNED_END;
    const inFailure = willFail && hour >= failHour && hour < failHour + EMERGENCY_REPAIR_H;
    if (willFail && hour >= failHour && !failed) { failed = true; repairCost += EMERGENCY_REPAIR_COST; }
    const ch2Off = inPlanned || inFailure;

    // supply per room
    const supply = new Map<string, number>();
    const ch1Rooms = roomsOf('CH-1'), ch2Rooms = roomsOf('CH-2');
    const ch1Load = ch1Rooms.reduce((s, r) => s + r.demand, 0);
    for (const r of ch1Rooms) supply.set(r.id, r.demand);
    if (!ch2Off) for (const r of ch2Rooms) supply.set(r.id, r.demand);
    else {
      // Backup: CH-1 carries what it can. A load shift sends it to the server room first.
      let spare = Math.max(0, (b.cap.get('CH-1') ?? 0) - ch1Load);
      const order = plan.loadShift ? [...ch2Rooms].sort((x, y) => (x.kind === 'server' ? -1 : y.kind === 'server' ? 1 : 0)) : ch2Rooms;
      if (plan.loadShift) {
        for (const r of order) { const g = Math.min(spare, r.demand); supply.set(r.id, g); spare -= g; }
      } else {
        const need = ch2Rooms.reduce((s, r) => s + r.demand, 0);
        for (const r of ch2Rooms) supply.set(r.id, spare * r.demand / need);
      }
    }
    if (plan.portable) { const srv = b.rooms.find(r => r.kind === 'server'); if (srv) supply.set(srv.id, (supply.get(srv.id) ?? 0) + PORTABLE_TONS); }

    // temperatures and downtime (skip integrating on tick 0 so frame 0 is "now")
    const downTenants = new Set<string>();
    for (const r of b.rooms) {
      let temp = temps.get(r.id)!;
      if (tick > 0) {
        const cov = Math.min(1, (supply.get(r.id) ?? 0) / r.demand);
        if (cov >= 0.999) temp = Math.max(r.startTemp, temp - (r.kind === 'server' ? 6 : 2.5) * TICK_H);
        else temp += r.warmRate * heatScale * (1 - cov) * TICK_H;
        temps.set(r.id, temp);
      }
      if (temp > r.limitTemp && r.tenantId) downTenants.add(r.tenantId);
    }
    if (tick > 0) for (const tn of downTenants) {
      downH.set(tn, (downH.get(tn) ?? 0) + TICK_H);
      cost += (b.tenants.get(tn)?.cost ?? 0) * TICK_H;
      if (outageHour < 0) outageHour = hour;
    }
    breaches = 0;
    let penalties = 0;
    for (const [tn, h] of downH) {
      const info = b.tenants.get(tn);
      if (info && info.penalty > 0 && h > info.allowed) { breaches++; penalties += info.penalty * (plan.notice ? 0.5 : 1); }
    }

    const srv = b.rooms.find(r => r.kind === 'server');
    if (tick % FRAME_EVERY === 0) {
      const fixedNow = plan.repair ? hour >= PLANNED_END : willFail && hour >= failHour + EMERGENCY_REPAIR_H;
      const ch2State = inFailure ? 'failed' : inPlanned ? 'repair' : fixedNow || failHour < 0 ? 'ok' : 'degraded';
      const starved = ch2Off;
      frames.push({
        t: b.rooms.map(r => Math.round(temps.get(r.id)! * 10) / 10),
        a: [
          'ok', ch2State,
          'ok', starved ? 'starved' : 'ok', starved ? 'starved' : 'ok',
        ],
        c: Math.round(cost + penalties + (failed || plan.repair ? repairCost : 0)),
        ta: downH.size, b: breaches,
        h: Math.round([...downH.values()].reduce((s, h) => s + h, 0) * 10) / 10,
      });
      if (srv) serverTemps.push(Math.round(temps.get(srv.id)! * 10) / 10);
    }
    if (tick === TICKS) cost += penalties;
  }

  const total = cost + repairCost;
  return {
    frames, serverTemps, impact: Math.round(total),
    downtimeHours: Math.round([...downH.values()].reduce((s, h) => s + h, 0) * 10) / 10,
    tenantsAffected: downH.size, breaches, outageHour, failed,
  };
}

/** Hours until CH-2 crosses its vibration limit, from a least-squares trend on the last 8 h. */
function forecastThreshold(ctx: Ctx, assetId: string): { slope: number; hours: number; now: number } {
  const pts = [...ctx.db.sensorReading.assetId.filter(assetId)].filter(r => r.hoursAgo <= 8);
  const a = ctx.db.asset.id.find(assetId)!;
  const xs = pts.map(p => -p.hoursAgo).concat([0]);
  const ys = pts.map(p => p.value).concat([a.reading]);
  const n = xs.length, mx = xs.reduce((s, v) => s + v, 0) / n, my = ys.reduce((s, v) => s + v, 0) / n;
  let num = 0, den = 0;
  for (let i = 0; i < n; i++) { num += (xs[i] - mx) * (ys[i] - my); den += (xs[i] - mx) ** 2; }
  const slope = den > 0 ? num / den : 0;
  const hours = slope > 0.01 ? Math.max(0.5, (VIB_LIMIT - a.reading) / slope) : -1;
  return { slope, hours, now: a.reading };
}

/** How unusual the latest reading is: z-score against the first 16 h of history. */
function anomalyScore(ctx: Ctx, assetId: string): { z: number; mean: number } {
  const hist = [...ctx.db.sensorReading.assetId.filter(assetId)].filter(r => r.hoursAgo > 8).map(r => r.value);
  const a = ctx.db.asset.id.find(assetId)!;
  if (hist.length < 4) return { z: 0, mean: a.baseline };
  const mean = hist.reduce((s, v) => s + v, 0) / hist.length;
  const sd = Math.max(0.3, Math.sqrt(hist.reduce((s, v) => s + (v - mean) ** 2, 0) / hist.length));
  return { z: (a.reading - mean) / sd, mean };
}

function failureChance(ctx: Ctx, assetId: string): number {
  const a = ctx.db.asset.id.find(assetId)!;
  const z = anomalyScore(ctx, assetId).z;
  const openWos = [...ctx.db.workOrder.iter()].filter(w => w.assetId === assetId && w.status === 'open').length;
  const age = 2026 - a.installYear;
  const s = -3.2 + 0.18 * Math.min(z, 20) + 0.55 * openWos + 2.2 * (age / a.expectedLife);
  return 1 / (1 + Math.exp(-s));
}

/** 50 randomized runs: failure timing and heat rates vary. Deterministic via ctx.random. */
function riskEstimate(ctx: Ctx, b: Building, plan: Plan, failMean: number, pFail: number) {
  const runs: number[][] = [];
  let outages = 0, impactSum = 0;
  const outageHours: number[] = [];
  for (let i = 0; i < 50; i++) {
    const g = (ctx.random() + ctx.random() + ctx.random() - 1.5) * 2; // ≈ N(0,1)
    const fh = ctx.random() < pFail ? Math.min(22, Math.max(0.5, failMean + g * 1.6)) : -1;
    const r = simulate(b, plan, fh, 0.75 + ctx.random() * 0.55);
    runs.push(r.serverTemps);
    impactSum += r.impact;
    if (r.outageHour >= 0) { outages++; outageHours.push(r.outageHour); }
  }
  outageHours.sort((x, y) => x - y);
  return {
    runs, chance: outages / 50, expected: impactSum / 50,
    early: outageHours.length ? outageHours[Math.floor(outageHours.length * 0.2)] : -1,
    late: outageHours.length ? outageHours[Math.floor(outageHours.length * 0.8)] : -1,
  };
}

function clock(hour: number): string {
  const total = Math.round((START_CLOCK + hour) * 60);
  const h = Math.floor(total / 60) % 24, m = total % 60;
  return `${h.toString().padStart(2, '0')}:${m.toString().padStart(2, '0')}`;
}
const money = (v: number) => v >= 1000 ? `$${(v / 1000).toFixed(v >= 100000 ? 0 : 1)}k` : `$${Math.round(v)}`;

function saveScenario(ctx: Ctx, invId: bigint, label: string, title: string, verified: boolean, b: Building,
  main: SimResult, risk: { runs: number[][]; chance: number } | null) {
  for (const s of [...ctx.db.scenario.iter()]) if (s.label === label) ctx.db.scenario.id.delete(s.id);
  return ctx.db.scenario.insert({
    id: 0n, invId, label, title, verified,
    outageChance: risk ? risk.chance : (main.outageHour >= 0 ? 1 : 0),
    outageHour: main.outageHour, impact: main.impact, downtimeHours: main.downtimeHours,
    tenantsAffected: main.tenantsAffected, breaches: main.breaches,
    framesJson: JSON.stringify({ rooms: b.rooms.map(r => r.id), assets: SIM_ASSETS, start: START_CLOCK, step: 0.5, frames: main.frames }),
    riskJson: JSON.stringify(risk ? risk.runs : []),
  });
}

// ================================================================ scene commands

const KINDS = new Set(['highlight', 'thread', 'guide_trail', 'set_time', 'focus', 'clear', 'pulse']);
function emit(ctx: Ctx, cmd: { kind: string; ids?: string[]; target?: string; hour?: number; label?: string; forRole?: string; color?: string }) {
  if (!KINDS.has(cmd.kind)) throw new SenderError(`unknown scene command '${cmd.kind}'`);
  const ids = [...(cmd.ids ?? []), ...(cmd.target ? [cmd.target] : [])];
  const bad = ids.filter(i => !ctx.db.asset.id.find(i) && !ctx.db.room.id.find(i));
  if (bad.length) throw new SenderError(`scene command names unknown ids: ${bad.join(', ')}`);
  ctx.db.sceneCommand.insert({ id: 0n, payload: JSON.stringify(cmd) });
  const rows = [...ctx.db.sceneCommand.iter()].sort((a, b) => (a.id < b.id ? -1 : 1));
  for (const r of rows.slice(0, Math.max(0, rows.length - 25))) ctx.db.sceneCommand.id.delete(r.id);
}

function setTimeline(ctx: Ctx, patch: Partial<{ hour: number; label: string; built: boolean }>) {
  const cur = ctx.db.timeline.id.find(0) ?? { id: 0, hour: 0, label: 'do_nothing', built: false };
  const row = { ...cur, ...patch };
  if (ctx.db.timeline.id.find(0)) ctx.db.timeline.id.update(row); else ctx.db.timeline.insert(row);
}

// ================================================================ the investigation
// The plan is a list of ops. One op runs per agent tick so people can watch.
// Each op does its real work (table reads, simulation) when it runs.

type Op = { agent: string; tool: string; why: string; target?: string };

const OPS: Op[] = [
  { agent: 'planner', tool: 'get_investigation', why: 'Reading what was asked' },
  { agent: 'planner', tool: 'get_building_summary', why: 'Learning the floor, assets and tenants' },
  { agent: 'planner', tool: 'create_subtasks', why: 'Splitting the work across the team' },
  { agent: 'signal', tool: 'detect_anomaly', why: 'Scoring every asset against its normal', target: 'CH-2' },
  { agent: 'planner', tool: 'post_hypothesis', why: 'Naming possible root causes', target: 'CH-2' },
  { agent: 'signal', tool: 'forecast_threshold', why: 'When does Chiller 2 cross its limit?', target: 'CH-2' },
  { agent: 'historian', tool: 'get_asset', why: 'How old is Chiller 2?', target: 'CH-2' },
  { agent: 'historian', tool: 'get_work_orders', why: 'Any tickets on Chiller 2?', target: 'CH-2' },
  { agent: 'historian', tool: 'search_past_incidents', why: 'Has a chiller failed like this before?', target: 'CH-1' },
  { agent: 'lease', tool: 'get_dependents', why: 'What does Chiller 2 keep cool?', target: 'AHU-2' },
  { agent: 'lease', tool: 'get_lease_terms', why: "What does downtime cost Suite 300's tenant?", target: 'SRV300' },
  { agent: 'signal', tool: 'check_sensor_health', why: 'Could the vibration sensor itself be wrong?', target: 'CH-2' },
  { agent: 'simulation', tool: 'simulate_cascade', why: 'What happens tonight if we do nothing?', target: 'SRV300' },
  { agent: 'simulation', tool: 'run_risk_estimate', why: 'How likely is the outage? 50 runs', target: 'SRV300' },
  { agent: 'planner', tool: 'post_action_candidate', why: 'Proposing fixes', target: 'CH-2' },
  { agent: 'simulation', tool: 'compare_scenarios', why: 'Testing every fix in the simulation', target: 'SRV300' },
  { agent: 'critic', tool: 'post_critique', why: 'Checking the plan for unchecked assumptions', target: 'CH-1' },
  { agent: 'simulation', tool: 'simulate_cascade', why: 'Can Chiller 1 carry the server room during repair?', target: 'CH-1' },
  { agent: 'critic', tool: 'post_critique', why: 'Re-checking with the new evidence', target: 'CH-1' },
  { agent: 'planner', tool: 'write_decision', why: 'Writing the decision and evidence chain' },
  { agent: 'narrator', tool: 'post_answer', why: 'Explaining it to each person', target: 'CH-2' },
];

const STEP_MICROS = 750_000n;
// The scripted cascade team: OPS plus the Structural Engineer (codes 21, 22) after the Historian.
const CASCADE_ORDER = [0, 1, 2, 3, 4, 5, 6, 7, 8, 21, 22, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20];
const DONE_MICROS = 300_000n;

function schedule(ctx: Ctx, invId: bigint, micros: bigint) {
  ctx.db.agentTick.insert({ scheduledId: 0n, scheduledAt: ScheduleAt.time(ctx.timestamp.microsSinceUnixEpoch + micros), invId });
}

function clearBlackboard(ctx: Ctx) {
  const db = ctx.db;
  clearTable(db.investigation.iter(), r => db.investigation.id.delete(r.id));
  clearTable(db.subtask.iter(), r => db.subtask.id.delete(r.id));
  clearTable(db.evidence.iter(), r => db.evidence.id.delete(r.id));
  clearTable(db.hypothesis.iter(), r => db.hypothesis.id.delete(r.id));
  clearTable(db.actionCandidate.iter(), r => db.actionCandidate.id.delete(r.id));
  clearTable(db.critique.iter(), r => db.critique.id.delete(r.id));
  clearTable(db.decision.iter(), r => db.decision.id.delete(r.id));
  clearTable(db.answer.iter(), r => db.answer.id.delete(r.id));
  clearTable(db.agentStep.iter(), r => db.agentStep.id.delete(r.id));
  clearTable(db.agentPlan.iter(), r => db.agentPlan.invId.delete(r.invId));
  clearTable(db.hypothesisEvidence.iter(), r => db.hypothesisEvidence.id.delete(r.id));
  clearTable(db.agentJob.iter(), r => db.agentJob.scheduledId.delete(r.scheduledId));
  clearTable(db.scenario.iter(), r => db.scenario.id.delete(r.id));
}

function openInvestigation(ctx: Ctx, trigger: string, source: string, askedBy: string, kind = 'cascade', subject = '') {
  clearBlackboard(ctx);
  if (kind !== 'structural_change') for (const w of [...ctx.db.wall.iter()]) if (w.proposal) ctx.db.wall.id.update({ ...w, proposal: '', proposalState: '' });
  const ai = nemotronReady(ctx);
  const inv = ctx.db.investigation.insert({ id: 0n, trigger, source, askedBy, status: 'investigating', createdAt: ctx.timestamp,
    mode: ai ? 'nemotron' : 'scripted', phase: ai ? 'plan' : '', phasesDone: '', critiqueRounds: 0, pending: '', kind, subject });
  emit(ctx, { kind: 'clear' });
  setTimeline(ctx, { hour: 0, label: 'do_nothing' });
  if (ai) scheduleJob(ctx, inv.id, 'plan', '', 200_000n);
  else {
    ctx.db.agentPlan.insert({ invId: inv.id, cursor: 0, opsJson: kind });
    schedule(ctx, inv.id, 400_000n);
  }
  return inv;
}

/** Likelihood update: equal start, evidence moves it by confidence, rescale to 100%. */
function relink(ctx: Ctx, invId: bigint) {
  const hs = [...ctx.db.hypothesis.iter()].filter(h => h.invId === invId);
  if (!hs.length) return;
  const score = new Map(hs.map(h => [h.id, 1]));
  for (const e of ctx.db.evidence.iter()) {
    if (e.invId !== invId || e.hypothesisId === 0n || !score.has(e.hypothesisId)) continue;
    const s = score.get(e.hypothesisId)!;
    score.set(e.hypothesisId, e.supports ? s * (1 + 0.3 * e.confidence) : s * (1 - 0.35 * e.confidence));
  }
  for (const l of ctx.db.hypothesisEvidence.iter()) {
    if (l.invId !== invId || !score.has(l.hypothesisId)) continue;
    const e = ctx.db.evidence.id.find(l.evidenceId);
    if (!e || e.hypothesisId === l.hypothesisId) continue; // counted above
    const s = score.get(l.hypothesisId)!;
    score.set(l.hypothesisId, l.supports ? s * (1 + 0.3 * e.confidence) : s * (1 - 0.35 * e.confidence));
  }
  const total = [...score.values()].reduce((s, v) => s + v, 0);
  for (const h of hs) ctx.db.hypothesis.id.update({ ...h, likelihood: score.get(h.id)! / total });
}

function hyp(ctx: Ctx, invId: bigint, assetId: string): bigint {
  return [...ctx.db.hypothesis.iter()].find(h => h.invId === invId && h.assetId === assetId)?.id ?? 0n;
}

function postEvidence(ctx: Ctx, invId: bigint, agent: string, fact: string, sourceTable: string, sourceRow: string,
  confidence: number, hypAsset: string, supports: boolean) {
  // Evidence must cite a row that exists.
  const exists =
    sourceTable === 'asset' ? !!ctx.db.asset.id.find(sourceRow) :
    sourceTable === 'room' ? !!ctx.db.room.id.find(sourceRow) :
    sourceTable === 'work_order' ? !!ctx.db.workOrder.id.find(sourceRow) :
    sourceTable === 'tenant' ? !!ctx.db.tenant.id.find(sourceRow) :
    sourceTable === 'past_incident' ? !!ctx.db.pastIncident.id.find(sourceRow) :
    sourceTable === 'issue' ? !!ctx.db.issue.id.find(sourceRow) :
    sourceTable === 'wall' ? !!ctx.db.wall.id.find(sourceRow) :
    sourceTable === 'field_note' ? (() => { try { const n = ctx.db.fieldNote.id.find(BigInt(sourceRow)); return !!n && !n.isPrivate; } catch { return false; } })() :
    sourceTable === 'sensor_reading' || sourceTable === 'scenario' || sourceTable === 'dependency';
  if (!exists) throw new SenderError(`evidence cites missing row ${sourceTable}/${sourceRow}`);
  const row = ctx.db.evidence.insert({ id: 0n, invId, agent, fact, sourceTable, sourceRow, confidence, hypothesisId: hyp(ctx, invId, hypAsset), supports, aboutAsset: hypAsset });
  relink(ctx, invId);
  return row;
}

function subtaskDone(ctx: Ctx, invId: bigint, agent: string) {
  const st = [...ctx.db.subtask.iter()].find(s => s.invId === invId && s.agent === agent && s.status !== 'done');
  if (st) ctx.db.subtask.id.update({ ...st, status: 'done' });
  // dispatch: anything whose dependencies are done becomes ready
  const all = [...ctx.db.subtask.iter()].filter(s => s.invId === invId);
  for (const s of all) {
    if (s.status !== 'waiting') continue;
    const deps = s.dependsOn ? s.dependsOn.split(',') : [];
    if (deps.every(d => all.find(x => x.agent === d)?.status === 'done')) ctx.db.subtask.id.update({ ...s, status: 'ready' });
  }
}

function claim(ctx: Ctx, invId: bigint, agent: string) {
  const st = [...ctx.db.subtask.iter()].find(s => s.invId === invId && s.agent === agent && (s.status === 'ready' || s.status === 'reopened'));
  if (st) ctx.db.subtask.id.update({ ...st, status: 'claimed' });
}

/** Runs one op for real and returns the one-line result shown in VR. */
function runOp(ctx: Ctx, inv: { id: bigint; trigger: string }, op: Op, index: number): { result: string; status?: string } {
  const db = ctx.db, id = inv.id;
  const b = loadBuilding(ctx);
  switch (index) {
    case 0: return { result: `"${inv.trigger}"` };
    case 1: {
      const rooms = [...db.room.iter()].filter(r => r.kind !== 'corridor').length;
      return { result: `${rooms} rooms · ${db.asset.count()} assets · ${db.tenant.count()} tenants · ${db.workOrder.count()} work orders` };
    }
    case 2: {
      const plan: [string, string, string][] = [
        ['signal', 'Find unusual sensor readings and time to threshold', ''],
        ['historian', 'Check asset age, open tickets and past incidents', ''],
        ['lease', 'Map affected rooms to tenants and their lease terms', ''],
        ['structural', 'Check the load path and damage near the failing equipment', ''],
        ['simulation', 'Simulate the next 24 hours and test fixes', 'signal,historian,lease'],
        ['critic', 'Challenge the evidence and the plan', 'simulation'],
      ];
      for (const [agent, goal, dependsOn] of plan)
        db.subtask.insert({ id: 0n, invId: id, agent, goal, dependsOn, status: dependsOn ? 'waiting' : 'ready' });
      return { result: '6 subtasks · signal, historian, lease, structural in parallel' };
    }
    case 3: {
      claim(ctx, id, 'signal');
      const scores = [...db.asset.iter()].filter(a => a.kind === 'chiller').map(a => ({ a, z: anomalyScore(ctx, a.id) }));
      scores.sort((x, y) => y.z.z - x.z.z);
      const top = scores[0];
      const a = top.a;
      db.asset.id.update({ ...a, health: 'watch' });
      postEvidence(ctx, id, 'signal', `${a.name} vibration ${a.reading.toFixed(1)} mm/s vs normal ${top.z.mean.toFixed(1)} (${top.z.z.toFixed(1)}σ).`,
        'sensor_reading', a.id, 0.8, a.id, true);
      emit(ctx, { kind: 'pulse', ids: [a.id], color: 'warn' });
      return { result: `${a.id} is ${top.z.z.toFixed(1)}σ above normal; ${scores[1].a.id} normal` };
    }
    case 4: {
      const hs: [string, string][] = [
        ['CH-2', 'Chiller 2 bearing is failing; the server room overheats when it trips.'],
        ['AHU-2', 'Air Handler 2 fan is failing and starving Suite 300 of air.'],
        ['SENSOR', 'The Chiller 2 vibration sensor is drifting; nothing is wrong.'],
      ];
      for (const [assetId, text] of hs) db.hypothesis.insert({ id: 0n, invId: id, assetId, text, likelihood: 1 / hs.length });
      // re-link evidence posted so far
      for (const e of [...db.evidence.iter()].filter(e => e.invId === id && e.hypothesisId === 0n))
        db.evidence.id.update({ ...e, hypothesisId: hyp(ctx, id, 'CH-2') });
      relink(ctx, id);
      return { result: '3 hypotheses: CH-2 bearing, AHU-2 fan, sensor drift' };
    }
    case 5: {
      const f = forecastThreshold(ctx, 'CH-2');
      postEvidence(ctx, id, 'signal', `Vibration rising ${f.slope.toFixed(2)} mm/s per hour; crosses ${VIB_LIMIT} mm/s in ${f.hours.toFixed(1)} h (≈${clock(f.hours)}).`,
        'sensor_reading', 'CH-2', 0.7, 'CH-2', true);
      subtaskDone(ctx, id, 'signal');
      return { result: `limit ${VIB_LIMIT} mm/s at ≈${clock(f.hours)} tonight (+${f.hours.toFixed(1)} h)` };
    }
    case 6: {
      claim(ctx, id, 'historian');
      const a = db.asset.id.find('CH-2')!;
      const age = 2026 - a.installYear;
      postEvidence(ctx, id, 'historian', `${a.name} is ${age} years old; expected life ${a.expectedLife} years.`, 'asset', a.id, 0.5, 'CH-2', true);
      return { result: `${age} of ${a.expectedLife} years used (${Math.round(100 * age / a.expectedLife)}%)` };
    }
    case 7: {
      const wos = [...db.workOrder.iter()].filter(w => w.assetId === 'CH-2' && w.status === 'open');
      for (const w of wos)
        postEvidence(ctx, id, 'historian', `${w.id} "${w.title}" open ${w.ageDays} days, never linked to sensor data.`, 'work_order', w.id, 0.6, 'CH-2', true);
      postEvidence(ctx, id, 'historian', `Two independent tickets describe noise: the sensor is not the only signal.`, 'work_order', wos[0]?.id ?? 'WO-4471', 0.7, 'SENSOR', false);
      return { result: `${wos.length} open tickets ignored: ${wos.map(w => w.id).join(', ')}` };
    }
    case 8: {
      const inc = [...db.pastIncident.iter()].filter(i => i.summary.toLowerCase().includes('bearing'));
      if (inc[0]) postEvidence(ctx, id, 'historian', `Similar: ${inc[0].id} ${inc[0].summary}`, 'past_incident', inc[0].id, 0.5, 'CH-2', true);
      const crack = db.issue.id.find('ISS-2');
      if (crack) postEvidence(ctx, id, 'historian', `Inspection found cracks running from the Chiller 2 pad into the wall: the vibration is not new.`, 'issue', crack.id, 0.6, 'CH-2', true);
      subtaskDone(ctx, id, 'historian');
      return { result: inc[0] ? `${inc[0].id}: bearing seized, ${inc[0].hoursDown} h down, ${money(inc[0].cost)}` : 'no similar incidents' };
    }
    case 9: {
      claim(ctx, id, 'lease');
      const deps = [...db.dependency.iter()];
      const ahus = deps.filter(d => d.fromId === 'CH-2' && d.kind === 'chilled_water').map(d => d.toId);
      const rooms = deps.filter(d => ahus.includes(d.fromId)).map(d => d.toId);
      const names = rooms.map(r => db.room.id.find(r)!.name);
      postEvidence(ctx, id, 'lease', `Chiller 2 feeds ${ahus.join(' and ')}, which cool ${names.join(', ')}.`, 'dependency', 'CH-2', 0.9, 'CH-2', true);
      postEvidence(ctx, id, 'lease', `Air Handler 2 supply air is ${db.asset.id.find('AHU-2')!.reading.toFixed(0)}°F, normal: its fan is fine.`, 'asset', 'AHU-2', 0.6, 'AHU-2', false);
      emit(ctx, { kind: 'thread', ids: ['CH-2', ...ahus.slice(0, 1), 'SRV300'] });
      return { result: `CH-2 → ${ahus.join(', ')} → ${names.join(', ')}` };
    }
    case 10: {
      const tn = db.tenant.id.find('T-NW')!;
      postEvidence(ctx, id, 'lease', `${tn.name} (Server Room 300): ${money(tn.costPerHour)}/h downtime; ${tn.clause}`, 'tenant', tn.id, 0.9, 'CH-2', true);
      subtaskDone(ctx, id, 'lease');
      return { result: `${tn.name}: ${money(tn.costPerHour)}/h, ${tn.allowedDowntimeHours} h allowed, ${money(tn.penalty)} penalty` };
    }
    case 11: {
      const ch1 = db.asset.id.find('CH-1')!;
      postEvidence(ctx, id, 'signal', `Same-model sensor on Chiller 1 reads a steady ${ch1.reading.toFixed(1)} mm/s; the CH-2 trend is smooth, not noisy.`,
        'sensor_reading', 'CH-1', 0.6, 'SENSOR', false);
      return { result: 'sensor looks healthy; drift unlikely' };
    }
    case 12: {
      claim(ctx, id, 'simulation');
      const f = forecastThreshold(ctx, 'CH-2');
      const r = simulate(b, NO_PLAN, f.hours);
      saveScenario(ctx, id, 'do_nothing', 'Do nothing', false, b, r, null);
      db.asset.id.update({ ...db.asset.id.find('CH-2')!, health: 'critical' });
      if (r.outageHour >= 0) setTimeline(ctx, { hour: Math.min(24, r.outageHour + 0.5), label: 'do_nothing' });
      emit(ctx, { kind: 'highlight', ids: ['CH-2', 'AHU-2', 'SRV300', 'S300'] });
      postEvidence(ctx, id, 'simulation', `Do nothing: CH-2 trips ${clock(f.hours)}; Server Room 300 passes 80°F at ${clock(r.outageHour)}; ${r.downtimeHours} h down; ${money(r.impact)}.`,
        'scenario', 'do_nothing', 0.8, 'CH-2', true);
      return { result: `server room down at ${clock(r.outageHour)} · ${r.downtimeHours} h · ${money(r.impact)}` };
    }
    case 13: {
      const f = forecastThreshold(ctx, 'CH-2');
      const p = failureChance(ctx, 'CH-2');
      const risk = riskEstimate(ctx, b, NO_PLAN, f.hours, p);
      const main = simulate(b, NO_PLAN, f.hours);
      saveScenario(ctx, id, 'do_nothing', 'Do nothing', false, b, main, risk);
      return { result: `outage in ${Math.round(risk.chance * 50)}/50 runs (${Math.round(risk.chance * 100)}%), most likely ${clock(risk.early)}–${clock(risk.late)}` };
    }
    case 14: {
      const acts: [string, string, string, string, number][] = [
        ['work_order', 'CH-2', 'Repair Chiller 2 bearing today', `Planned repair ${clock(PLANNED_START)}–${clock(PLANNED_END)}; ${money(PLANNED_REPAIR_COST)} vs ${money(EMERGENCY_REPAIR_COST)} emergency.`, PLANNED_REPAIR_COST],
        ['load_shift', 'CH-1', 'Shift server room to Chiller 1', 'Send Chiller 1 spare capacity to Server Room 300 first.', 150],
        ['tenant_notice', 'SRV300', 'Notify Northwind Analytics', 'Advance notice of maintenance; halves the uptime penalty if anything slips.', 0],
        ['portable_cooling', 'SRV300', 'Rent portable cooling', '15-ton unit in Server Room 300 for 24 h.', 1200],
        ['structural_repair', 'MECH', 'Isolate Chiller 2 and inject the wall cracks', 'Vibration isolation pads plus epoxy crack injection in the Mechanical east wall.', 6500],
      ];
      for (const [kind, target, title, detail, cost] of acts)
        db.actionCandidate.insert({ id: 0n, invId: id, kind, target, title, detail, cost, saved: 0, rank: 0, recommended: false, status: 'proposed' });
      return { result: '5 candidates: repair, load shift, notice, cooling, structural repair' };
    }
    case 15: {
      // Re-simulate each action on its own and the recommended combination; rank by dollars saved per dollar.
      const f = forecastThreshold(ctx, 'CH-2');
      const p = failureChance(ctx, 'CH-2');
      const base = riskEstimate(ctx, b, NO_PLAN, f.hours, p).expected;
      const acts = [...db.actionCandidate.iter()].filter(a => a.invId === id);
      const planFor = (kind: string): Plan => ({ ...NO_PLAN, repair: kind === 'work_order', loadShift: kind === 'load_shift', notice: kind === 'tenant_notice', portable: kind === 'portable_cooling' });
      const scored = acts.map(a => {
        const pl = planFor(a.kind);
        // repairs take CH-2 offline for 3 h: test with the load shift that must go with it
        if (pl.repair) pl.loadShift = true;
        if (a.kind === 'structural_repair') { const g = crackGrowth(ctx); return { a, saved: g.emergencyCost - g.plannedCost }; }
        const e = riskEstimate(ctx, b, pl, f.hours, p).expected;
        return { a, saved: Math.max(0, base - e) };
      });
      scored.sort((x, y) => (y.saved / Math.max(100, y.a.cost)) - (x.saved / Math.max(100, x.a.cost)));
      scored.forEach((s, i) => db.actionCandidate.id.update({ ...s.a, saved: Math.round(s.saved), rank: i + 1, recommended: s.a.kind !== 'portable_cooling' }));
      const plan: Plan = { repair: true, loadShift: true, notice: true, portable: false };
      const risk = riskEstimate(ctx, b, plan, f.hours, p);
      const main = simulate(b, plan, f.hours);
      saveScenario(ctx, id, 'with_plan', 'Act now', false, b, main, risk);
      return { result: `plan cuts expected impact ${money(base)} → ${money(risk.expected)}; outage ${Math.round(risk.chance * 100)}%` };
    }
    case 16: {
      claim(ctx, id, 'critic');
      const text = "Is Chiller 1's spare capacity enough to carry the server room while Chiller 2 is down for repair?";
      db.critique.insert({ id: 0n, invId: id, verdict: 'revise', text });
      const st = [...db.subtask.iter()].find(s => s.invId === id && s.agent === 'simulation');
      if (st) db.subtask.id.update({ ...st, status: 'reopened' });
      return { result: `REVISE: ${text}`, status: 'revise' };
    }
    case 17: {
      const ch1 = db.asset.id.find('CH-1')!;
      const ch1Load = b.rooms.filter(r => b.chillerOf.get(r.id) === 'CH-1').reduce((s, r) => s + r.demand, 0);
      const spare = ch1.capacity - ch1Load;
      const srv = b.rooms.find(r => r.kind === 'server')!;
      const plan: Plan = { repair: true, loadShift: true, notice: true, portable: false };
      const r = simulate(b, plan, -1);
      const peak = Math.max(...r.serverTemps);
      postEvidence(ctx, id, 'simulation', `Chiller 1 has ${spare} tons spare; the server room needs ${srv.demand}. During repair it peaks at ${peak.toFixed(0)}°F (limit ${srv.limitTemp}).`,
        'asset', 'CH-1', 0.85, 'CH-2', true);
      subtaskDone(ctx, id, 'simulation');
      return { result: `${spare} tons spare ≥ ${srv.demand} needed · server room peaks ${peak.toFixed(0)}°F` };
    }
    case 18: {
      db.critique.insert({ id: 0n, invId: id, verdict: 'approve', text: 'Every hypothesis has 2+ independent sources and every action was simulated. Approved.' });
      subtaskDone(ctx, id, 'critic');
      return { result: 'APPROVED: evidence is solid' };
    }
    case 19: {
      const top = [...db.hypothesis.iter()].filter(h => h.invId === id).sort((x, y) => y.likelihood - x.likelihood)[0];
      const dn = [...db.scenario.iter()].find(s => s.label === 'do_nothing')!;
      const wp = [...db.scenario.iter()].find(s => s.label === 'with_plan')!;
      db.decision.insert({
        id: 0n, invId: id, rootCause: top.text, likelihood: top.likelihood,
        summary: `Repair Chiller 2 today with the server room on Chiller 1, notify Northwind, and isolate the chiller so the wall cracks stop growing. Outage risk ${Math.round(dn.outageChance * 100)}% → ${Math.round(wp.outageChance * 100)}%, impact ${money(dn.impact)} → ${money(wp.impact)}. Would change our mind: Chiller 1 vibration rising too.`,
      });
      return { result: `root cause ${top.assetId} (${Math.round(top.likelihood * 100)}%)` };
    }
    case 20: {
      const dn = [...db.scenario.iter()].find(s => s.label === 'do_nothing')!;
      const f = forecastThreshold(ctx, 'CH-2');
      db.answer.insert({ id: 0n, invId: id, role: 'technician', byNemotron: false,
        text: `Chiller 2's bearing is failing and will likely trip around ${clock(f.hours)}. Go to the Mechanical room; I've drawn the path. Check the bearing first, then shift the server room to Chiller 1.` });
      db.answer.insert({ id: 0n, invId: id, role: 'manager', byNemotron: false,
        text: `If we do nothing, Server Room 300 overheats tonight: ${dn.downtimeHours.toFixed(1)} hours down and about ${money(dn.impact)}. The same vibration is cracking the load-bearing Mechanical wall. I recommend the ${money(PLANNED_REPAIR_COST)} repair today plus ${money(6500)} to isolate the chiller and seal the cracks; approve to proceed.` });
      emit(ctx, { kind: 'guide_trail', target: 'CH-2', forRole: 'technician' });
      emit(ctx, { kind: 'thread', ids: ['CH-2', 'AHU-2', 'SRV300'] });
      const inv2 = db.investigation.id.find(id)!;
      db.investigation.id.update({ ...inv2, status: 'awaiting_approval' });
      db.narrateJob.insert({ scheduledId: 0n, scheduledAt: ScheduleAt.time(ctx.timestamp.microsSinceUnixEpoch + 100_000n), invId: id });
      return { result: 'answers posted for technician and manager' };
    }
  }
  return { result: '' };
}

export const runAgentTick = spacetimedb.reducer({ onSchedule: agentTick }, { timer: agentTick.rowType }, (ctx, { timer }) => {
  const plan = ctx.db.agentPlan.invId.find(timer.invId);
  const inv = ctx.db.investigation.id.find(timer.invId);
  if (!plan || !inv) return;
  const c = plan.cursor;
  const i = Math.floor(c / 2);
  const wallPlan = plan.opsJson === 'structural_change';
  const order = wallPlan ? WALL_OPS.map((_, k) => 100 + k) : CASCADE_ORDER;
  if (i >= order.length) { ctx.db.agentPlan.invId.delete(plan.invId); return; }
  const code = order[i];
  const op = code >= 100 ? WALL_OPS[code - 100] : code >= 21 ? STRUCT_OPS[code - 21] : OPS[code];
  // wall plans: orbs gather at the room beside the wall in question
  const target = code >= 100 ? (roomsBeside(ctx, wallOf(ctx, inv.id))[0]?.id ?? '') : op.target ?? '';
  if (c % 2 === 0) {
    ctx.db.agentStep.insert({ id: 0n, invId: inv.id, seq: i, agent: op.agent, tool: op.tool, why: op.why, result: '', target, status: 'running', model: '' });
    schedule(ctx, inv.id, STEP_MICROS);
  } else {
    const out = code >= 100 ? runWallOp(ctx, inv.id, code - 100) : code >= 21 ? runStructOp(ctx, inv.id, code - 21) : runOp(ctx, inv, op, code);
    const row = [...ctx.db.agentStep.iter()].find(s => s.invId === inv.id && s.seq === i);
    if (row) ctx.db.agentStep.id.update({ ...row, result: out.result, status: out.status ?? 'done' });
    schedule(ctx, inv.id, DONE_MICROS);
  }
  ctx.db.agentPlan.invId.update({ ...plan, cursor: c + 1 });
});

// ================================================================ blueprint → knowledge graph → structural reasoning
// The plan is ingested once (reducer or HTTP POST /blueprint) and turned into a building
// graph inside the database: walls, roof bays, rooftop loads and rooms as `element` rows,
// linked by `element_edge` rows (bounds, supports, carries, separates). Two reasoners run on
// it, both as plain functions next to the data:
//   1. plan-only inference: what you can tell from a floor plan alone (exterior, continuity)
//   2. framing-aware analysis: tributary loads from the framing sheet → load per wall,
//      utilization, load paths, beam sizing for removals, code checks.
// Ground truth from the as-built drawings scores both (see the eval harness).

type BpWall = { id: string; name: string; a: [number, number]; b: [number, number]; drawn: number; material: string; services: string; fireRated?: boolean; damaged?: boolean };
type BpBay = { id: string; x0: number; z0: number; x1: number; z1: number; span: 'x' | 'z'; deadKPa: number; liveKPa: number; note: string };
type BpLoad = { id: string; label: string; x: number; z: number; kN: number; bay: string };
type Blueprint = { id: string; name: string; source: string; walls: BpWall[]; bays: BpBay[]; pointLoads: BpLoad[]; asBuilt: { bearing: string[] } };

const HARBOR_POINT: Blueprint = {
  id: 'BP-HARBOR-3', name: 'Harbor Point, Floor 3 (top floor)',
  source: 'Plan: CubiCasa5K-style wall/room/door annotations · Framing: structural sheet S-201 · Truth: as-built drawings',
  walls: [
    { id: 'W-EXT-S', name: 'South exterior wall', a: [0, 0], b: [24, 0], drawn: 0.3, material: 'Reinforced CMU', services: '' },
    { id: 'W-EXT-N', name: 'North exterior wall', a: [0, 16], b: [24, 16], drawn: 0.3, material: 'Reinforced CMU', services: '' },
    { id: 'W-EXT-W', name: 'West exterior wall', a: [0, 0], b: [0, 16], drawn: 0.3, material: 'Reinforced CMU', services: '' },
    { id: 'W-EXT-E', name: 'East exterior wall', a: [24, 0], b: [24, 16], drawn: 0.3, material: 'Reinforced CMU', services: '' },
    { id: 'W-SPINE-S', name: 'Corridor south wall', a: [0, 7], b: [24, 7], drawn: 0.15, material: 'Steel stud + CMU core', services: 'Chilled-water main to AHU-2 and AHU-3' },
    { id: 'W-SPINE-N', name: 'Corridor north wall', a: [0, 9], b: [24, 9], drawn: 0.15, material: 'Steel stud + CMU core', services: 'Sprinkler main' },
    { id: 'W-MECH-E', name: 'Mechanical room east wall', a: [5, 0], b: [5, 7], drawn: 0.15, material: 'CMU', services: 'Chilled-water risers to AHU-2', damaged: true },
    { id: 'W-300-SRV', name: 'Suite 300 / Server Room wall', a: [14, 0], b: [14, 7], drawn: 0.15, material: 'Metal stud + 2 layers drywall', services: 'AHU-2 supply duct and 2 data conduits' },
    { id: 'W-SRV-400', name: 'Server Room / Suite 400 wall', a: [18, 0], b: [18, 7], drawn: 0.15, material: 'Metal stud, 1-hour fire rated', services: 'Fire alarm cabling', fireRated: true },
    { id: 'W-LOB-100', name: 'Lobby / Suite 100 wall', a: [5, 9], b: [5, 16], drawn: 0.15, material: 'Metal stud + drywall', services: '' },
    { id: 'W-100-200', name: 'Suite 100 / Suite 200 wall', a: [12, 9], b: [12, 16], drawn: 0.15, material: 'Metal stud + drywall', services: 'Electrical branch circuit' },
    { id: 'W-200-COM', name: 'Suite 200 / Commons wall', a: [18, 9], b: [18, 16], drawn: 0.15, material: 'CMU', services: 'Sprinkler branch line' },
  ],
  // Roof framing (S-201): each bay's joists span one way and rest on the bay edges across the span.
  bays: [
    { id: 'BAY-MECH', x0: 0, z0: 0, x1: 5, z1: 7, span: 'x', deadKPa: 2.0, liveKPa: 1.5, note: 'Joists run east-west under the cooling-tower curb' },
    { id: 'BAY-S', x0: 5, z0: 0, x1: 24, z1: 7, span: 'z', deadKPa: 2.0, liveKPa: 1.5, note: 'Joists run north-south' },
    { id: 'BAY-CORR', x0: 0, z0: 7, x1: 24, z1: 9, span: 'z', deadKPa: 2.0, liveKPa: 1.5, note: 'Short joists over the corridor' },
    { id: 'BAY-NW', x0: 0, z0: 9, x1: 18, z1: 16, span: 'z', deadKPa: 2.0, liveKPa: 1.5, note: 'Joists run north-south' },
    { id: 'BAY-NE', x0: 18, z0: 9, x1: 24, z1: 16, span: 'x', deadKPa: 2.0, liveKPa: 1.5, note: 'Joists run east-west under the rooftop unit' },
  ],
  pointLoads: [
    { id: 'RT-1', label: 'Cooling tower (Chiller 2 heat rejection)', x: 3, z: 3.5, kN: 60, bay: 'BAY-MECH' },
    { id: 'RT-2', label: 'Rooftop unit over Commons', x: 21, z: 12.5, kN: 40, bay: 'BAY-NE' },
  ],
  asBuilt: { bearing: ['W-EXT-S', 'W-EXT-N', 'W-EXT-W', 'W-EXT-E', 'W-SPINE-S', 'W-SPINE-N', 'W-MECH-E', 'W-200-COM'] },
};

// capacity per meter of wall (kN/m), simplified allowable axial line load by assembly
const CAPACITY: Record<string, number> = { 'Reinforced CMU': 40, 'CMU': 30, 'Steel stud + CMU core': 26 };
const PARTITION_CAPACITY = 3; // self weight only
const CRACK_CAPACITY_LOSS = 0.15;
const SPREAD_M = 3; // a rooftop load spreads over ~3 m of its supporting wall

// Wide-flange steel sections: [name, S (cm³), I (cm⁴), kg/m]
const W_SHAPES: [string, number, number, number][] = [
  ['W8x18', 249, 2580, 27], ['W10x26', 454, 5080, 39], ['W12x35', 749, 11900, 52], ['W14x48', 1150, 20100, 71],
  ['W16x57', 1520, 31000, 85], ['W18x71', 2240, 48300, 106], ['W21x83', 2810, 70800, 124], ['W24x104', 4000, 129000, 155],
];
const FY = 345, E_STEEL = 200000; // MPa

const onLine = (w: BpWall, axis: 'x' | 'z', c: number) =>
  axis === 'z' ? Math.abs(w.a[1] - c) < 0.01 && Math.abs(w.b[1] - c) < 0.01 : Math.abs(w.a[0] - c) < 0.01 && Math.abs(w.b[0] - c) < 0.01;
const overlap = (a0: number, a1: number, b0: number, b1: number) => Math.max(0, Math.min(Math.max(a0, a1), Math.max(b0, b1)) - Math.max(Math.min(a0, a1), Math.min(b0, b1)));

type WallLoad = { kNm: number; from: { bay: string; kNm: number }[]; point: { id: string; kN: number }[] };

/** Tributary-area load analysis: each bay's area load goes half to each support edge; point loads split by lever rule. */
function analyzeLoads(bp: Blueprint): Map<string, WallLoad> {
  const out = new Map<string, WallLoad>(bp.walls.map(w => [w.id, { kNm: 0, from: [], point: [] }]));
  for (const bay of bp.bays) {
    const q = bay.deadKPa + bay.liveKPa;
    const span = bay.span === 'z' ? bay.z1 - bay.z0 : bay.x1 - bay.x0;
    const edges = bay.span === 'z' ? [bay.z0, bay.z1] : [bay.x0, bay.x1];
    for (const c of edges) {
      const axis: 'x' | 'z' = bay.span === 'z' ? 'z' : 'x';
      for (const w of bp.walls) {
        if (!onLine(w, axis, c)) continue;
        const ov = axis === 'z' ? overlap(w.a[0], w.b[0], bay.x0, bay.x1) : overlap(w.a[1], w.b[1], bay.z0, bay.z1);
        if (ov < 0.5) continue;
        const kNm = q * span / 2;
        const wl = out.get(w.id)!;
        wl.kNm += kNm; wl.from.push({ bay: bay.id, kNm: Math.round(kNm * 10) / 10 });
      }
    }
    for (const p of bp.pointLoads.filter(p => p.bay === bay.id)) {
      const pos = bay.span === 'z' ? p.z : p.x;
      const [c0, c1] = edges;
      const shares: [number, number][] = [[c0, p.kN * (c1 - pos) / (c1 - c0)], [c1, p.kN * (pos - c0) / (c1 - c0)]];
      for (const [c, kN] of shares) {
        const axis: 'x' | 'z' = bay.span === 'z' ? 'z' : 'x';
        const w = bp.walls.find(w2 => onLine(w2, axis, c) && (axis === 'z' ? overlap(w2.a[0], w2.b[0], p.x - 0.1, p.x + 0.1) : overlap(w2.a[1], w2.b[1], p.z - 0.1, p.z + 0.1)) > 0.1);
        if (!w) continue;
        const wl = out.get(w.id)!;
        wl.kNm += kN / SPREAD_M; wl.point.push({ id: p.id, kN: Math.round(kN * 10) / 10 });
      }
    }
  }
  for (const wl of out.values()) wl.kNm = Math.round(wl.kNm * 10) / 10;
  return out;
}

/** Plan-only guess (what a floor plan alone suggests): exterior walls and long continuous walls bear. */
function planOnlyBearing(bp: Blueprint, w: BpWall): { bearing: boolean; why: string } {
  const len = Math.hypot(w.b[0] - w.a[0], w.b[1] - w.a[1]);
  const xs = bp.walls.flatMap(x => [x.a[0], x.b[0]]), zs = bp.walls.flatMap(x => [x.a[1], x.b[1]]);
  const minX = Math.min(...xs), maxX = Math.max(...xs), minZ = Math.min(...zs), maxZ = Math.max(...zs);
  const exterior = onLine(w, 'x', minX) || onLine(w, 'x', maxX) || onLine(w, 'z', minZ) || onLine(w, 'z', maxZ);
  if (exterior) return { bearing: true, why: 'on the building perimeter' };
  if (len >= 0.8 * Math.max(maxX - minX, maxZ - minZ)) return { bearing: true, why: `runs ${len} m, nearly the full building` };
  if (w.drawn >= 0.25) return { bearing: true, why: 'drawn thick' };
  return { bearing: false, why: `short interior wall (${len} m), drawn like a partition` };
}

function sizeBeam(kNm: number, spanM: number) {
  const M = kNm * spanM * spanM / 8; // kN·m
  const Sreq = M * 1e6 / (0.66 * FY) / 1000; // cm³
  const Ireq = 5 * kNm * Math.pow(spanM * 1000, 3) * 360 / (384 * E_STEEL) / 1e4; // cm⁴, L/360
  const pick = W_SHAPES.find(s => s[1] >= Sreq && s[2] >= Ireq);
  return { M: Math.round(M), Sreq: Math.round(Sreq), Ireq: Math.round(Ireq), R: Math.round(kNm * spanM / 2), shape: pick?.[0] ?? '', kgPerM: pick?.[3] ?? 0 };
}

/** Rebuild the knowledge graph and the derived `wall` table from a blueprint. */
function ingestBlueprint(ctx: Ctx, bp: Blueprint) {
  const db = ctx.db;
  if (db.blueprint.id.find(bp.id)) db.blueprint.id.delete(bp.id);
  db.blueprint.insert({ id: bp.id, name: bp.name, source: bp.source, json: JSON.stringify(bp), loadedAt: ctx.timestamp });
  clearTable(db.element.iter(), r => db.element.id.delete(r.id));
  clearTable(db.elementEdge.iter(), r => db.elementEdge.id.delete(r.id));
  clearTable(db.wall.iter(), r => db.wall.id.delete(r.id));

  const loads = analyzeLoads(bp);
  const rooms = [...db.room.iter()];
  db.element.insert({ id: 'FOUNDATION', kind: 'foundation', label: 'Foundation', x0: 0, z0: 0, x1: 0, z1: 0, value: 0, unit: '', detail: 'Strip footings under bearing walls' });
  for (const bay of bp.bays)
    db.element.insert({ id: bay.id, kind: 'bay', label: bay.note, x0: bay.x0, z0: bay.z0, x1: bay.x1, z1: bay.z1, value: bay.deadKPa + bay.liveKPa, unit: 'kPa', detail: `span ${bay.span}` });
  for (const p of bp.pointLoads) {
    db.element.insert({ id: p.id, kind: 'point_load', label: p.label, x0: p.x, z0: p.z, x1: p.x, z1: p.z, value: p.kN, unit: 'kN', detail: p.bay });
    db.elementEdge.insert({ id: 0n, fromId: p.bay, toId: p.id, rel: 'carries', value: p.kN });
  }
  for (const w of bp.walls) {
    const wl = loads.get(w.id)!;
    const exterior = planOnlyBearing(bp, w).why === 'on the building perimeter';
    const bearing = wl.kNm > 1;
    const baseCap = bearing ? CAPACITY[w.material] ?? 26 : PARTITION_CAPACITY;
    const capacity = w.damaged ? baseCap * (1 - CRACK_CAPACITY_LOSS) : baseCap;
    const utilization = bearing ? wl.kNm / capacity : 0.07;
    const guess = planOnlyBearing(bp, w);
    const carries = bearing
      ? [...wl.from.map(f => bp.bays.find(b => b.id === f.bay)!.note.toLowerCase().replace(/^joists run [a-z-]+ ?/, '').trim() || f.bay), ...wl.point.map(p => bp.pointLoads.find(x => x.id === p.id)!.label.toLowerCase())]
          .filter(x => x).join('; ') || 'roof joists'
      : 'nothing structural (its own weight only)';
    db.element.insert({ id: w.id, kind: 'wall', label: w.name, x0: w.a[0], z0: w.a[1], x1: w.b[0], z1: w.b[1], value: wl.kNm, unit: 'kN/m',
      detail: JSON.stringify({ capacity, plan_only: guess }) });
    db.wall.insert({
      id: w.id, name: w.name, x0: w.a[0], z0: w.a[1], x1: w.b[0], z1: w.b[1],
      kind: exterior ? 'exterior' : bearing ? 'bearing' : 'partition', material: w.material,
      utilization: Math.round(utilization * 100) / 100, carries: carries.charAt(0).toUpperCase() + carries.slice(1),
      health: w.damaged ? 'damaged' : 'ok', services: w.services, proposal: '', proposalState: '',
      loadKnm: wl.kNm, capacityKnm: Math.round(capacity * 10) / 10, fireRated: !!w.fireRated,
      planOnlyBearing: guess.bearing, planOnlyWhy: guess.why,
    });
    // load path: bay/point load → wall → foundation
    for (const f of wl.from) db.elementEdge.insert({ id: 0n, fromId: w.id, toId: f.bay, rel: 'supports', value: f.kNm });
    for (const p of wl.point) db.elementEdge.insert({ id: 0n, fromId: w.id, toId: p.id, rel: 'supports', value: p.kN });
    if (bearing) db.elementEdge.insert({ id: 0n, fromId: 'FOUNDATION', toId: w.id, rel: 'supports', value: wl.kNm });
    // topology: which rooms each wall bounds
    for (const r of roomsBeside(ctx, { x0: w.a[0], z0: w.a[1], x1: w.b[0], z1: w.b[1] }))
      db.elementEdge.insert({ id: 0n, fromId: w.id, toId: r.id, rel: r.kind === 'server' && w.fireRated ? 'separates_fire' : 'bounds', value: 0 });
  }
  for (const r of rooms) db.element.insert({ id: r.id, kind: 'room', label: r.name, x0: r.x0, z0: r.z0, x1: r.x1, z1: r.z1, value: 0, unit: '', detail: r.kind });
}

/** Everything that rests on an element, transitively (what loses support if it goes). */
function dependents(ctx: Ctx, id: string): string[] {
  const seen = new Set<string>(); const q = [id];
  while (q.length) { const c = q.shift()!; for (const e of ctx.db.elementEdge.fromId.filter(c)) if (e.rel === 'supports' && !seen.has(e.toId)) { seen.add(e.toId); q.push(e.toId); } }
  return [...seen];
}

/** What holds an element up, down to the foundation. */
function loadPath(ctx: Ctx, id: string): string[][] {
  const paths: string[][] = [];
  const walk = (node: string, path: string[]) => {
    const sups = [...ctx.db.elementEdge.toId.filter(node)].filter(e => e.rel === 'supports' || e.rel === 'carries').map(e => e.fromId);
    if (!sups.length || node === 'FOUNDATION' || path.length > 6) { paths.push(path); return; }
    for (const s of sups) walk(s, [...path, s]);
  };
  walk(id, [id]);
  return paths;
}

// ================================================================ structure
// Walls as structural elements: what is load-bearing, how loaded it is, what it carries,
// what runs inside it, and its condition. Engineering checks below are simple, explainable
// rules of thumb (span tables, unit costs) so every number the agents say is reproducible.

function seedWalls(ctx: Ctx) {
  ingestBlueprint(ctx, HARBOR_POINT);
  seedEvalCases(ctx);
  clearTable(ctx.db.evalResult.iter(), r => ctx.db.evalResult.id.delete(r.id));
  runDeterministicEval(ctx);
}

const wallLen = (w: { x0: number; z0: number; x1: number; z1: number }) => Math.hypot(w.x1 - w.x0, w.z1 - w.z0);

/** Rooms on either side of a wall. */
function roomsBeside(ctx: Ctx, w: { x0: number; z0: number; x1: number; z1: number }) {
  const vertical = Math.abs(w.x1 - w.x0) < 0.01;
  return [...ctx.db.room.iter()].filter(r => {
    if (vertical) return (Math.abs(r.x0 - w.x0) < 0.01 || Math.abs(r.x1 - w.x0) < 0.01) && r.z1 > Math.min(w.z0, w.z1) + 0.1 && r.z0 < Math.max(w.z0, w.z1) - 0.1;
    return (Math.abs(r.z0 - w.z0) < 0.01 || Math.abs(r.z1 - w.z0) < 0.01) && r.x1 > Math.min(w.x0, w.x1) + 0.1 && r.x0 < Math.max(w.x0, w.x1) - 0.1;
  });
}

type Verdict = 'go' | 'go_with_beam' | 'not_now' | 'not_allowed' | 'not_feasible';
type Removal = { verdict: Verdict; structural: boolean; span: number; beam: string; posts: number; shoringDays: number; days: number;
  cost: number; parts: { kind: string; title: string; detail: string; cost: number }[]; reasons: string[];
  calc: { M: number; Sreq: number; Ireq: number; R: number } | null };
const removalOk = (v: string) => v === 'go' || v === 'go_with_beam';

/** Can this wall come out, and what would it take? Uses the framing analysis, code rules and condition. */
function assessRemoval(ctx: Ctx, wallId: string): Removal {
  const w = ctx.db.wall.id.find(wallId);
  if (!w) throw new SenderError(`unknown wall '${wallId}'`);
  const span = Math.round(wallLen(w) * 10) / 10;
  const beside = roomsBeside(ctx, w);
  const parts: Removal['parts'] = [];
  const reasons: string[] = [];
  const structural = w.kind !== 'partition';
  let beam = '', posts = 0, shoringDays = 0, days = 2, calc: Removal['calc'] = null;
  let verdict: Verdict = structural ? 'go_with_beam' : 'go';

  parts.push({ kind: 'engineer_review', title: 'Structural engineer review', detail: `Stamped drawings for removing the ${w.name}.`, cost: structural ? 3200 : 900 });
  if (structural) {
    const sb = sizeBeam(w.loadKnm, span);
    calc = { M: sb.M, Sreq: sb.Sreq, Ireq: sb.Ireq, R: sb.R };
    reasons.push(`It carries ${w.loadKnm.toFixed(1)} kN/m, ${Math.round(w.utilization * 100)}% of its ${w.capacityKnm.toFixed(1)} kN/m capacity (${w.carries.toLowerCase()}).`);
    if (sb.shape) {
      beam = sb.shape; posts = 2; shoringDays = 3; days = 10 + Math.ceil(span);
      reasons.push(`A ${span} m opening puts M = ${sb.M} kN·m on a new beam: S ≥ ${sb.Sreq} cm³ and I ≥ ${sb.Ireq} cm⁴ (deflection L/360) → ${beam}, ${sb.R} kN at each post.`);
      parts.push({ kind: 'temporary_shoring', title: 'Temporary shoring', detail: `Shore the roof framing for ${shoringDays} days while the wall comes out.`, cost: 4800 });
      parts.push({ kind: 'install_beam', title: `Install ${beam} steel beam`, detail: `${span} m clear span, ${sb.R} kN per post, new footings.`, cost: Math.round(900 * span + span * sb.kgPerM * 3 + 6500) });
    } else {
      verdict = 'not_feasible';
      reasons.push(`A ${span} m clear span needs M = ${sb.M} kN·m (S ≥ ${sb.Sreq} cm³), beyond any single rolled beam in the table; it would need intermediate columns.`);
    }
  } else {
    parts.push({ kind: 'remove_partition', title: 'Remove partition', detail: `Demolish ${span} m of ${w.material.toLowerCase()}, patch floor and ceiling.`, cost: Math.round(180 * span + 900) });
    reasons.push(`It is a non-structural partition: the framing analysis puts no roof load on it.`);
  }
  if (w.services) {
    days += 2;
    parts.push({ kind: 'reroute_services', title: 'Reroute what runs inside', detail: `Inside the wall: ${w.services.toLowerCase()}; reroute it first.`, cost: 4100 });
    reasons.push(`Inside it: ${w.services.toLowerCase()}.`);
  }
  if (beside.some(r => r.kind === 'server')) {
    parts.push({ kind: 'dust_containment', title: 'Dust containment for the server room', detail: 'Sealed enclosure and negative air while work is next to live racks.', cost: 3500 });
    reasons.push('A live server room is on one side: dust containment is required.');
  }
  if (w.fireRated && beside.some(r => r.kind === 'server')) { verdict = 'not_allowed'; reasons.push('It is the server room\'s 1-hour fire separation; code requires an equivalent rated wall, so it cannot simply come out.'); }
  if (w.kind === 'exterior') { verdict = 'not_now'; reasons.push('Exterior walls hold the facade and lateral bracing; opening one is a separate facade project.'); }
  if (w.health === 'damaged') { verdict = 'not_now'; reasons.push('It is already cracking from Chiller 2 vibration (capacity reduced 15%); repair and isolate the chiller before any change.'); }
  return { verdict, structural, span, beam, posts, shoringDays, days, cost: parts.reduce((x, p) => x + p.cost, 0), parts, reasons, calc };
}

/** Crack growth driven by Chiller 2 vibration (rule of thumb: 0.02 mm/week per mm/s above normal). */
function crackGrowth(ctx: Ctx) {
  const ch2 = ctx.db.asset.id.find('CH-2')!;
  const repaired = ch2.health === 'repair';
  const excess = Math.max(0, ch2.reading - ch2.baseline);
  const now = 0.4, limit = 1.5;
  const perWeek = repaired ? 0.002 : 0.02 * excess;
  const weeks = perWeek > 0.0001 ? (limit - now) / perWeek : 999;
  return { now, limit, perWeek: Math.round(perWeek * 1000) / 1000, weeks: Math.round(weeks), emergencyCost: 28000, plannedCost: 6500 };
}

// ---- scripted ops: structural engineer inside the cascade investigation (OPS indexes 21, 22)
const STRUCT_OPS: Op[] = [
  { agent: 'structural', tool: 'get_structure', why: 'Which walls carry load around Chiller 2?', target: 'MECH' },
  { agent: 'structural', tool: 'assess_crack_growth', why: 'How fast are the pad cracks growing?', target: 'MECH' },
];

function runStructOp(ctx: Ctx, invId: bigint, index: number): { result: string; status?: string } {
  if (index === 0) {
    claim(ctx, invId, 'structural');
    const w = ctx.db.wall.id.find('W-MECH-E')!;
    postEvidence(ctx, invId, 'structural', `The ${w.name} carries ${w.loadKnm.toFixed(1)} kN/m including the cooling tower; cracking cut its capacity to ${w.capacityKnm.toFixed(1)} kN/m, so it now runs at ${Math.round(w.utilization * 100)}%.`, 'wall', w.id, 0.75, 'CH-2', true);
    return { result: `${w.id} ${w.loadKnm.toFixed(1)} kN/m · ${Math.round(w.utilization * 100)}% of cracked capacity` };
  }
  const g = crackGrowth(ctx);
  postEvidence(ctx, invId, 'structural', `Pad cracks are ${g.now} mm wide and growing ${g.perWeek} mm/week with the vibration; they reach the ${g.limit} mm repair limit in about ${g.weeks} weeks (${money(g.emergencyCost)} emergency vs ${money(g.plannedCost)} now).`, 'issue', 'ISS-2', 0.65, 'CH-2', true);
  subtaskDone(ctx, invId, 'structural');
  return { result: `${g.now} → ${g.limit} mm in ~${g.weeks} weeks at ${g.perWeek} mm/wk` };
}

// ---- scripted ops: "what if we remove this wall?"
const WALL_OPS: Op[] = [
  { agent: 'planner', tool: 'get_investigation', why: 'Reading the proposed change' },
  { agent: 'planner', tool: 'create_subtasks', why: 'Splitting the work across the team' },
  { agent: 'structural', tool: 'get_structure', why: 'Is this wall load-bearing?' },
  { agent: 'historian', tool: 'get_inspection_issues', why: 'Any damage in or near this wall?' },
  { agent: 'lease', tool: 'get_dependents', why: 'Who is on either side, and who is disrupted?' },
  { agent: 'structural', tool: 'assess_wall_removal', why: 'What would it take to remove it?' },
  { agent: 'critic', tool: 'post_critique', why: 'Checking the plan for unchecked assumptions' },
  { agent: 'structural', tool: 'check_wall_services', why: 'What runs inside the wall?' },
  { agent: 'critic', tool: 'post_critique', why: 'Re-checking with the new evidence' },
  { agent: 'planner', tool: 'post_action_candidate', why: 'Turning the assessment into a plan' },
  { agent: 'planner', tool: 'write_decision', why: 'Writing the recommendation' },
  { agent: 'narrator', tool: 'post_answer', why: 'Explaining it to each person' },
];

function wallOf(ctx: Ctx, invId: bigint) {
  const inv = ctx.db.investigation.id.find(invId)!;
  return ctx.db.wall.id.find(inv.subject)!;
}

function runWallOp(ctx: Ctx, invId: bigint, index: number): { result: string; status?: string } {
  const db = ctx.db, w = wallOf(ctx, invId);
  const a = assessRemoval(ctx, w.id);
  const beside = roomsBeside(ctx, w);
  switch (index) {
    case 0: return { result: `Remove the ${w.name} (${a.span} m)` };
    case 1: {
      for (const [agent, goal] of [['structural', `Is ${w.id} load-bearing, and what would removing it take?`], ['historian', `Find damage in or near ${w.id}`],
        ['lease', 'Who is disrupted on either side, and for how long?'], ['critic', 'Challenge the plan']] as const)
        db.subtask.insert({ id: 0n, invId, agent, goal, dependsOn: agent === 'critic' ? 'structural' : '', status: agent === 'critic' ? 'waiting' : 'ready' });
      return { result: '4 subtasks · structural, history and tenants in parallel' };
    }
    case 2: {
      claim(ctx, invId, 'structural');
      postEvidence(ctx, invId, 'structural', `${w.name}: ${w.kind === 'partition' ? 'non-structural partition' : w.kind === 'exterior' ? 'exterior bearing wall' : 'load-bearing wall'}, ${w.material.toLowerCase()}, ${Math.round(w.utilization * 100)}% of capacity; carries ${w.carries.toLowerCase()}.`, 'wall', w.id, 0.9, w.id, w.kind === 'partition');
      return { result: `${w.kind === 'partition' ? 'partition' : 'load-bearing'} · ${Math.round(w.utilization * 100)}% · ${w.material}` };
    }
    case 3: {
      claim(ctx, invId, 'historian');
      const near = [...db.issue.iter()].filter(i => i.status === 'open' && beside.some(r => r.id === i.roomId) && i.kind === 'structural');
      const fact = w.health === 'damaged' ? `This wall is already cracking (ISS-2: cracks from the Chiller 2 pad).` : near.length ? `Nearby: ${near.map(i => i.title.toLowerCase()).join(', ')}.` : 'No structural findings in or next to this wall.';
      postEvidence(ctx, invId, 'historian', fact, 'wall', w.id, 0.6, w.id, w.health !== 'damaged');
      subtaskDone(ctx, invId, 'historian');
      return { result: fact };
    }
    case 4: {
      claim(ctx, invId, 'lease');
      const tenants = [...new Set(beside.map(r => r.tenantId).filter(t => t))].map(id => db.tenant.id.find(id)!);
      const fact = `${beside.map(r => r.name).join(' and ')} are on either side${tenants.length ? `; ${tenants.map(t => t.name).join(', ')} would work around ${a.days} days of construction` : ''}.`;
      postEvidence(ctx, invId, 'lease', fact, 'room', beside[0]?.id ?? 'CORR', 0.8, w.id, true);
      subtaskDone(ctx, invId, 'lease');
      emit(ctx, { kind: 'highlight', ids: beside.map(r => r.id) });
      return { result: `${beside.map(r => r.name).join(' + ')} · ${a.days} days of work` };
    }
    case 5: {
      const verdict = a.verdict === 'go' ? 'Can be removed' : a.verdict === 'go_with_beam' ? `Removable with a ${a.beam} beam on ${a.posts} posts`
        : a.verdict === 'not_allowed' ? 'Not allowed by code' : a.verdict === 'not_feasible' ? 'Not feasible as one span' : 'Not recommended now';
      postEvidence(ctx, invId, 'structural', `${verdict}: ${a.reasons[a.calc ? 1 : 0] ?? a.reasons[0]}`, 'wall', w.id, 0.85, w.id, removalOk(a.verdict));
      db.wall.id.update({ ...db.wall.id.find(w.id)!, proposalState: 'evaluating' });
      return { result: `${verdict} · ${money(a.cost)} · ${a.days} days` };
    }
    case 6: {
      claim(ctx, invId, 'critic');
      const text = w.services ? 'Has anyone checked what runs inside the wall before we cut it?' : a.structural ? 'Who carries the load while the wall is out?' : 'Is the wall really non-structural above the ceiling?';
      db.critique.insert({ id: 0n, invId, verdict: 'revise', text });
      return { result: `REVISE: ${text}`, status: 'revise' };
    }
    case 7: {
      const fact = w.services ? `Inside the wall: ${w.services.toLowerCase()}. Rerouting adds ${money(4100)} and 2 days.` : a.structural ? `Temporary shoring for ${a.shoringDays} days carries the load while the beam goes in.` : 'Above the ceiling the partition stops at the deck with a slip track: no load transfer.';
      postEvidence(ctx, invId, 'structural', fact, 'wall', w.id, 0.8, w.id, true);
      subtaskDone(ctx, invId, 'structural');
      return { result: fact };
    }
    case 8: {
      db.critique.insert({ id: 0n, invId, verdict: 'approve', text: 'Load path, services and disruption are covered. Approved.' });
      subtaskDone(ctx, invId, 'critic');
      return { result: 'APPROVED: load path, services and disruption covered' };
    }
    case 9: {
      const rec = removalOk(a.verdict);
      a.parts.forEach((p, i) => db.actionCandidate.insert({ id: 0n, invId, kind: p.kind, target: w.id, title: p.title, detail: p.detail, cost: p.cost, saved: 0, rank: i + 1, recommended: rec, status: 'proposed' }));
      if (!rec && w.health === 'damaged') {
        db.actionCandidate.insert({ id: 0n, invId, kind: 'structural_repair', target: 'MECH', title: 'Isolate Chiller 2 and inject the cracks first', detail: 'Vibration isolation pads plus epoxy crack injection, then re-assess.', cost: 6500, saved: 21500, rank: 0, recommended: true, status: 'proposed' });
      }
      return { result: `${a.parts.length} steps · ${money(a.cost)} total` };
    }
    case 10: {
      const verdict = a.verdict === 'go' ? `Yes: it is a partition. ${money(a.cost)}, ${a.days} days.` : a.verdict === 'go_with_beam' ? `Yes, with a ${a.beam} steel beam on two posts: ${money(a.cost)}, ${a.days} days, engineer stamp required.` : `No. ${a.reasons[a.reasons.length - 1]}`;
      db.decision.insert({ id: 0n, invId, rootCause: `Remove ${w.name}`, likelihood: 1, summary: `${verdict} ${a.reasons.slice(0, 2).join(' ')}` });
      db.wall.id.update({ ...db.wall.id.find(w.id)!, proposalState: removalOk(a.verdict) ? 'recommended' : 'not_recommended' });
      return { result: verdict };
    }
    case 11: {
      const tech = !removalOk(a.verdict)
        ? `Don't open the ${w.name}. ${a.reasons[a.reasons.length - 1]}`
        : `The ${w.name} is ${a.structural ? 'load-bearing' : 'a partition'}. ${w.services ? `Reroute the ${w.services.toLowerCase()} first. ` : ''}${a.structural ? `Shore it for ${a.shoringDays} days, then set the ${a.beam} beam.` : 'Then it can come out in about two days.'}`;
      const mgr = !removalOk(a.verdict)
        ? `Removing the ${w.name} isn't recommended right now: ${a.reasons[a.reasons.length - 1]} Approve the repair plan first.`
        : `We can remove the ${w.name} for about ${money(a.cost)} over ${a.days} days${a.structural ? `, with a ${a.beam} steel beam carrying the load` : ''}. Approve to schedule it.`;
      db.answer.insert({ id: 0n, invId, role: 'technician', byNemotron: false, text: tech });
      db.answer.insert({ id: 0n, invId, role: 'manager', byNemotron: false, text: mgr });
      const inv = db.investigation.id.find(invId)!;
      db.investigation.id.update({ ...inv, status: 'awaiting_approval' });
      return { result: 'answers posted for technician and manager' };
    }
  }
  return { result: '' };
}

/** Open a "what if we change this wall?" investigation. */
export const proposeWallChange = spacetimedb.reducer({ wallId: t.string(), change: t.string() }, (ctx, { wallId, change }) => {
  const w = ctx.db.wall.id.find(wallId);
  if (!w) throw new SenderError(`unknown wall '${wallId}'`);
  if (change !== 'remove') throw new SenderError(`unsupported change '${change}'`);
  clearTable(ctx.db.agentTick.iter(), r => ctx.db.agentTick.scheduledId.delete(r.scheduledId));
  for (const other of [...ctx.db.wall.iter()]) if (other.proposal) ctx.db.wall.id.update({ ...other, proposal: '', proposalState: '' });
  ctx.db.wall.id.update({ ...ctx.db.wall.id.find(wallId)!, proposal: 'remove', proposalState: 'evaluating' });
  openInvestigation(ctx, `What if we remove the ${w.name}?`, 'ask', me(ctx).name, 'structural_change', wallId);
});

/** Free-text questions that ask to remove a wall become structural-change investigations. */
function wallFromText(ctx: Ctx, text: string): string | null {
  const q = text.toLowerCase();
  if (!/(remove|knock|take (out|down)|open up|demolish|tear down)/.test(q) || !/wall/.test(q)) return null;
  let best: string | null = null, score = 0;
  for (const w of ctx.db.wall.iter()) {
    const words = w.name.toLowerCase().replace(/[^a-z0-9 ]/g, ' ').split(/\s+/).filter(x => x.length > 2 && x !== 'wall');
    const s = words.filter(x => q.includes(x)).length;
    if (s > score) { score = s; best = w.id; }
  }
  return score >= 2 ? best : null;
}

// ---------------------------------------------------------------- Nemotron narrator (optional)
// If an NVIDIA API key is stored (INSERT INTO secret VALUES ('nvidia_api_key', '...')), the
// Narrator's answers are rewritten by Nemotron from the decision facts. Otherwise the
// template answers stand.

export const narrate = spacetimedb.procedure({ onSchedule: narrateJob }, { job: narrateJob.rowType }, t.unit(), (ctx, { job }) => {
  const facts = ctx.withTx(tx => {
    const key = tx.db.secret.key.find('nvidia_api_key')?.value ?? '';
    const model = tx.db.secret.key.find('nvidia_model')?.value ?? 'nvidia/llama-3.3-nemotron-super-49b-v1';
    const dec = [...tx.db.decision.iter()].find(d => d.invId === job.invId);
    const ev = [...tx.db.evidence.iter()].filter(e => e.invId === job.invId).map(e => `- ${e.fact}`);
    const ans = [...tx.db.answer.iter()].filter(a => a.invId === job.invId);
    return { key, model, dec, ev, ans };
  });
  if (!facts.key || !facts.dec) return {};
  for (const a of facts.ans) {
    const sys = 'ROLE: NARRATOR for Cascade, a building operations team. Turn the decision into speech for one person. At most 3 short sentences. Use only numbers present in the facts. ' +
      (a.role === 'technician' ? 'Technician: what is wrong, where to go, what to check first.' : 'Manager: the risk, the cost if ignored, the recommended action and its cost.');
    const user = `Decision: ${facts.dec.rootCause} ${facts.dec.summary}\nEvidence:\n${facts.ev.join('\n')}\nDraft: ${a.text}`;
    try {
      const res = ctx.http.fetch('https://integrate.api.nvidia.com/v1/chat/completions', {
        method: 'POST',
        headers: { 'content-type': 'application/json', authorization: `Bearer ${facts.key}` },
        body: JSON.stringify({ model: facts.model, temperature: 0.3, max_tokens: 160,
          messages: [{ role: 'system', content: 'detailed thinking off\n' + sys }, { role: 'user', content: user }] }),
      });
      if (res.status !== 200) continue;
      const text = (JSON.parse(res.text()).choices?.[0]?.message?.content ?? '').trim();
      if (!text) continue;
      ctx.withTx(tx => {
        const row = tx.db.answer.id.find(a.id);
        if (row) tx.db.answer.id.update({ ...row, text, byNemotron: true });
      });
    } catch { /* keep the template answer */ }
  }
  return {};
});

// ================================================================ Nemotron agent team
// Seven agents, each a Nemotron call with its own tools, coordinating only through
// the blackboard tables above. Runs inside SpacetimeDB as scheduled procedures:
// procedures may call HTTP; every tool call is a short transaction against the
// blackboard, validated by the same rules as the reducers. If a phase fails (no key,
// bad output twice, network), the scripted ops for that phase run instead, so the
// demo never stalls.

type PCtx = ProcedureCtx<InferSchema<typeof spacetimedb>>;
type Json = Record<string, unknown>;
type ToolDef = { name: string; description: string; params: Json; required?: string[] };
type Call = { id: string; name: string; args: Json };

const DEFAULT_BASE = 'https://integrate.api.nvidia.com/v1';
const ACTION_KINDS: Record<string, { cost: number; target: string; title: string; detail: string }> = {
  work_order: { cost: PLANNED_REPAIR_COST, target: 'CH-2', title: 'Repair Chiller 2 bearing today', detail: `Planned repair ${'15:30'}–${'18:30'}; $4.8k vs $38k emergency.` },
  load_shift: { cost: 150, target: 'CH-1', title: 'Shift server room to Chiller 1', detail: 'Send Chiller 1 spare capacity to Server Room 300 first.' },
  tenant_notice: { cost: 0, target: 'SRV300', title: 'Notify Northwind Analytics', detail: 'Advance notice of maintenance; halves the uptime penalty if anything slips.' },
  portable_cooling: { cost: 1200, target: 'SRV300', title: 'Rent portable cooling', detail: '15-ton unit in Server Room 300 for 24 h.' },
  structural_repair: { cost: 6500, target: 'MECH', title: 'Isolate Chiller 2 and inject the wall cracks', detail: 'Vibration isolation pads plus epoxy crack injection in the Mechanical east wall.' },
};

function secretOf(ctx: Ctx, key: string) { return ctx.db.secret.key.find(key)?.value ?? ''; }

function nemotronReady(ctx: Ctx): boolean {
  return !!secretOf(ctx, 'nvidia_api_key') && (ctx.db.aiStatus.id.find(0)?.enabled ?? false);
}

function scheduleJob(ctx: Ctx, invId: bigint, phase: string, note: string, micros: bigint) {
  ctx.db.agentJob.insert({ scheduledId: 0n, scheduledAt: ScheduleAt.time(ctx.timestamp.microsSinceUnixEpoch + micros), invId, phase, note });
}

function nextSeq(ctx: Ctx, invId: bigint): number {
  let max = -1;
  for (const s of ctx.db.agentStep.iter()) if (s.invId === invId && s.seq > max) max = s.seq;
  return max + 1;
}

function logStep(ctx: Ctx, invId: bigint, agent: string, tool: string, why: string, result: string, target: string, status: string, model: string) {
  const valid = target && (ctx.db.asset.id.find(target) || ctx.db.room.id.find(target)) ? target : '';
  return ctx.db.agentStep.insert({ id: 0n, invId, seq: nextSeq(ctx, invId), agent, tool, why: why.slice(0, 120), result: result.slice(0, 220), target: valid, status, model });
}

const str = (v: unknown, d = '') => (typeof v === 'string' ? v : v == null ? d : String(v));
const num = (v: unknown, d = 0) => (typeof v === 'number' && isFinite(v) ? v : typeof v === 'string' && isFinite(+v) ? +v : d);
const arr = (v: unknown): unknown[] => (Array.isArray(v) ? v : []);

function planFromActions(actions: unknown[]): Plan {
  const k = new Set(actions.map(a => str(a)));
  return { repair: k.has('work_order'), loadShift: k.has('load_shift') || k.has('work_order'), notice: k.has('tenant_notice'), portable: k.has('portable_cooling') };
}

// ---------------------------------------------------------------- tools

const WALL_KINDS = ['engineer_review', 'temporary_shoring', 'install_beam', 'remove_partition', 'reroute_services', 'dust_containment'];
const W = { why: { type: 'string', description: 'One plain sentence under 15 words: what you are checking. Shown live in VR.' } };
const ASSET = { type: 'string', description: 'Asset id, e.g. CH-2' };
const ACTIONS = { type: 'array', items: { type: 'string', enum: Object.keys(ACTION_KINDS) }, description: 'Actions to apply; empty = do nothing' };

const TOOLS: Record<string, ToolDef> = {
  get_investigation: { name: 'get_investigation', description: 'The trigger, status and subtasks of this investigation.', params: { ...W } },
  get_building_summary: { name: 'get_building_summary', description: 'Rooms, assets, tenants and how cooling flows.', params: { ...W } },
  get_structure: { name: 'get_structure', description: 'Walls with type (exterior, bearing, partition), material, % of capacity used, what they carry, condition and what runs inside. Filter by wall id or room id.', params: { ...W, wall_id: { type: 'string' }, room_id: { type: 'string' } } },
  assess_wall_removal: { name: 'assess_wall_removal', description: 'Engineering check for removing a wall: verdict, beam size, posts, shoring, cost, days and reasons.', params: { ...W, wall_id: { type: 'string' } }, required: ['why', 'wall_id'] },
  assess_crack_growth: { name: 'assess_crack_growth', description: 'How fast the chiller-pad wall cracks are growing with the current vibration, and when they reach the repair limit.', params: { ...W } },
  get_load_path: { name: 'get_load_path', description: 'What holds an element up, down to the foundation (bays, rooftop loads RT-1/RT-2, walls), with loads.', params: { ...W, element_id: { type: 'string' } }, required: ['why', 'element_id'] },
  check_code: { name: 'check_code', description: 'Code checks for changing a wall: fire separation, exterior/lateral role, condition.', params: { ...W, wall_id: { type: 'string' } }, required: ['why', 'wall_id'] },
  check_wall_services: { name: 'check_wall_services', description: 'Ducts, pipes and cables that run inside a wall and must be rerouted before it changes.', params: { ...W, wall_id: { type: 'string' } }, required: ['why', 'wall_id'] },
  post_evidence: { name: 'post_evidence', description: 'Post one finding to the blackboard. Must cite an existing row.', params: { ...W,
    fact: { type: 'string', description: 'One sentence, numbers only from tool results' },
    source_table: { type: 'string', enum: ['asset', 'room', 'work_order', 'tenant', 'past_incident', 'sensor_reading', 'scenario', 'dependency', 'issue', 'wall'] },
    source_row: { type: 'string', description: 'Row id in that table (asset id for sensor_reading/dependency)' },
    confidence: { type: 'number', description: '0..1' },
    about_asset: { type: 'string', description: 'Asset the finding is about' },
    supports: { type: 'boolean', description: 'true if it points to a real problem with about_asset' } },
    required: ['why', 'fact', 'source_table', 'source_row', 'confidence', 'about_asset', 'supports'] },
  complete_subtask: { name: 'complete_subtask', description: 'Finish your subtask with a one-sentence summary.', params: { ...W, summary: { type: 'string' } }, required: ['why', 'summary'] },
  create_subtasks: { name: 'create_subtasks', description: 'Create 4-8 subtasks for the specialists.', params: { ...W,
    subtasks: { type: 'array', items: { type: 'object', properties: {
      agent_type: { type: 'string', enum: ['signal', 'historian', 'lease', 'structural', 'simulation', 'critic'] },
      goal: { type: 'string' }, depends_on: { type: 'array', items: { type: 'string' } } }, required: ['agent_type', 'goal'] } } },
    required: ['why', 'subtasks'] },
  post_hypothesis: { name: 'post_hypothesis', description: 'Name one possible root cause (max 3 per investigation).', params: { ...W,
    asset_id: ASSET, mechanism: { type: 'string', description: 'Failure mechanism in one sentence' },
    supporting_evidence_ids: { type: 'array', items: { type: 'integer' } },
    contradicting_evidence_ids: { type: 'array', items: { type: 'integer' } } }, required: ['why', 'asset_id', 'mechanism'] },
  post_action_candidate: { name: 'post_action_candidate', description: 'Propose one action. Costs come from the price list, not from you.', params: { ...W,
    kind: { type: 'string', enum: [...Object.keys(ACTION_KINDS), ...WALL_KINDS] }, rationale: { type: 'string' } }, required: ['why', 'kind'] },
  write_decision: { name: 'write_decision', description: 'Final decision once the Critic approved.', params: { ...W,
    root_cause_asset: ASSET, summary: { type: 'string', description: 'Root cause, evidence chain, ranked actions' },
    what_would_change_my_mind: { type: 'string' } }, required: ['why', 'root_cause_asset', 'summary'] },
  get_sensor_history: { name: 'get_sensor_history', description: 'Hourly readings for the last 24 h, the live value and the normal baseline.', params: { ...W, asset_id: ASSET }, required: ['why', 'asset_id'] },
  detect_anomaly: { name: 'detect_anomaly', description: 'Score how unusual each chiller reading is (z-score vs normal).', params: { ...W } },
  forecast_threshold: { name: 'forecast_threshold', description: 'Hours until an asset crosses its alarm limit, from the trend.', params: { ...W, asset_id: ASSET }, required: ['why', 'asset_id'] },
  check_sensor_health: { name: 'check_sensor_health', description: 'Is a sensor trustworthy? Compares noise and a sibling sensor.', params: { ...W, asset_id: ASSET }, required: ['why', 'asset_id'] },
  get_asset: { name: 'get_asset', description: 'Asset registry row: age, expected life, capacity, health.', params: { ...W, asset_id: ASSET }, required: ['why', 'asset_id'] },
  get_work_orders: { name: 'get_work_orders', description: 'Open and closed tickets for an asset.', params: { ...W, asset_id: ASSET }, required: ['why', 'asset_id'] },
  get_field_notes: { name: 'get_field_notes', description: 'Observations the people on site have noted (shared notes only).', params: { ...W } },
  get_inspection_issues: { name: 'get_inspection_issues', description: 'Open inspection findings (structural, water, mechanical, electrical), optionally for one asset.', params: { ...W, asset_id: { type: 'string', description: 'optional asset id' } } },
  search_past_incidents: { name: 'search_past_incidents', description: 'Search past incidents by keyword.', params: { ...W, query: { type: 'string' } }, required: ['why', 'query'] },
  get_dependents: { name: 'get_dependents', description: 'Everything an asset feeds: air handlers, rooms, tenants.', params: { ...W, asset_id: ASSET }, required: ['why', 'asset_id'] },
  get_tenants: { name: 'get_tenants', description: 'Tenants, their business and cost of downtime.', params: { ...W } },
  get_lease_terms: { name: 'get_lease_terms', description: 'Uptime clause, allowed downtime and penalty for a tenant.', params: { ...W, tenant_id: { type: 'string' } }, required: ['why', 'tenant_id'] },
  simulate_cascade: { name: 'simulate_cascade', description: 'Run the 24 h cascade rules once. Empty actions = do nothing (saved as the baseline).', params: { ...W, actions: ACTIONS }, required: ['why', 'actions'] },
  run_risk_estimate: { name: 'run_risk_estimate', description: '50 randomized runs: chance and timing of an outage, expected impact.', params: { ...W, actions: ACTIONS }, required: ['why', 'actions'] },
  compare_scenarios: { name: 'compare_scenarios', description: 'Simulate every proposed action against doing nothing; ranks them by $ saved per $ and saves the best plan.', params: { ...W } },
  get_evidence_chain: { name: 'get_evidence_chain', description: 'Hypotheses, evidence, ranked actions, scenarios and past critiques.', params: { ...W } },
  post_critique: { name: 'post_critique', description: 'Approve, or send back with at most 3 specific requests.', params: { ...W,
    verdict: { type: 'string', enum: ['approve', 'revise'] }, summary: { type: 'string' },
    requests: { type: 'array', items: { type: 'object', properties: { agent_type: { type: 'string', enum: ['signal', 'historian', 'lease', 'structural', 'simulation'] }, request: { type: 'string' } }, required: ['agent_type', 'request'] } } },
    required: ['why', 'verdict', 'summary'] },
  post_answer: { name: 'post_answer', description: 'Spoken answer for one person, at most 3 short sentences.', params: { ...W,
    role: { type: 'string', enum: ['technician', 'manager'] }, text: { type: 'string' } }, required: ['why', 'role', 'text'] },
  issue_scene_command: { name: 'issue_scene_command', description: 'Drive both headsets: guide_trail to an asset, highlight or thread a chain of ids, focus one id.', params: { ...W,
    kind: { type: 'string', enum: ['guide_trail', 'highlight', 'thread', 'focus'] }, ids: { type: 'array', items: { type: 'string' } }, target: { type: 'string' } }, required: ['why', 'kind'] },
};

const AGENT_TOOLS: Record<string, string[]> = {
  plan: ['get_investigation', 'get_building_summary', 'create_subtasks'],
  signal: ['get_sensor_history', 'detect_anomaly', 'forecast_threshold', 'check_sensor_health', 'post_evidence', 'complete_subtask'],
  historian: ['get_asset', 'get_work_orders', 'get_inspection_issues', 'search_past_incidents', 'post_evidence', 'complete_subtask'],
  lease: ['get_dependents', 'get_tenants', 'get_lease_terms', 'post_evidence', 'complete_subtask'],
  structural: ['get_structure', 'get_load_path', 'get_inspection_issues', 'assess_crack_growth', 'assess_wall_removal', 'check_code', 'check_wall_services', 'post_evidence', 'complete_subtask'],
  hypothesize: ['get_evidence_chain', 'post_hypothesis'],
  simulate: ['simulate_cascade', 'run_risk_estimate', 'post_evidence', 'complete_subtask'],
  propose: ['get_evidence_chain', 'post_action_candidate'],
  compare: ['compare_scenarios', 'simulate_cascade'],
  critique: ['get_evidence_chain', 'post_critique'],
  decide: ['get_evidence_chain', 'write_decision'],
  narrate: ['get_evidence_chain', 'post_answer', 'issue_scene_command'],
};

function toolTarget(args: Json): string { return str(args.asset_id) || str(args.about_asset) || str(args.target) || str(args.source_row); }

/** Execute one tool call inside a transaction. Returns the JSON result for the model and a one-line summary for VR. */
function execTool(tx: Ctx, invId: bigint, agent: string, call: Call): { out: unknown; line: string } {
  const db = tx.db, a = call.args;
  const b = () => loadBuilding(tx);
  const assetOr = (id: string) => { const r = db.asset.id.find(id); if (!r) throw new Error(`unknown asset '${id}'. Valid: ${[...db.asset.iter()].map(x => x.id).join(', ')}`); return r; };
  const inv = db.investigation.id.find(invId)!;
  switch (call.name) {
    case 'get_investigation': {
      const subs = [...db.subtask.iter()].filter(s => s.invId === invId).map(s => ({ agent: s.agent, goal: s.goal, status: s.status }));
      return { out: { trigger: inv.trigger, source: inv.source, sim_clock: 'Tuesday 14:00', subtasks: subs }, line: `"${inv.trigger}"` };
    }
    case 'get_building_summary': {
      const rooms = [...db.room.iter()].filter(r => r.kind !== 'corridor').map(r => ({ id: r.id, name: r.name, kind: r.kind, tenant: r.tenantId }));
      const assets = [...db.asset.iter()].map(x => ({ id: x.id, name: x.name, kind: x.kind, room: x.roomId }));
      const deps = [...db.dependency.iter()].map(d => `${d.fromId}->${d.toId} (${d.kind})`);
      return { out: { building: 'Harbor Point, Floor 3', rooms, assets, dependencies: deps, tenants: [...db.tenant.iter()].map(x => x.id) },
        line: `${rooms.length} rooms · ${assets.length} assets · ${db.tenant.count()} tenants` };
    }
    case 'create_subtasks': {
      const subs = arr(a.subtasks).slice(0, 8);
      let n = 0;
      for (const sRaw of subs) {
        const sObj = (sRaw ?? {}) as Json;
        const agentType = str(sObj.agent_type);
        if (!['signal', 'historian', 'lease', 'structural', 'simulation', 'critic'].includes(agentType)) continue;
        db.subtask.insert({ id: 0n, invId, agent: agentType, goal: str(sObj.goal).slice(0, 200), dependsOn: arr(sObj.depends_on).map(x => str(x)).join(','),
          status: ['signal', 'historian', 'lease', 'structural'].includes(agentType) ? 'ready' : 'waiting' });
        n++;
      }
      for (const need of parallelGroup(inv.kind))
        if (![...db.subtask.iter()].some(s => s.invId === invId && s.agent === need))
          db.subtask.insert({ id: 0n, invId, agent: need, goal: `Gather ${need} evidence for: ${inv.trigger}`, dependsOn: '', status: 'ready' });
      if (n === 0) throw new Error('no valid subtasks; agent_type must be signal, historian, lease, simulation or critic');
      return { out: { created: n }, line: `${n} subtasks · specialists start in parallel` };
    }
    case 'post_evidence': {
      const about = str(a.about_asset);
      const e = postEvidence(tx, invId, agent, str(a.fact).slice(0, 240), str(a.source_table), str(a.source_row), Math.max(0, Math.min(1, num(a.confidence, 0.5))), about, a.supports !== false);
      return { out: { evidence_id: Number(e.id) }, line: str(a.fact).slice(0, 200) };
    }
    case 'complete_subtask': {
      const role = agent === 'simulation' ? 'simulation' : agent;
      subtaskDone(tx, invId, role);
      return { out: { ok: true }, line: str(a.summary) };
    }
    case 'post_hypothesis': {
      const existing = [...db.hypothesis.iter()].filter(h => h.invId === invId);
      if (existing.length >= 3) throw new Error('already 3 hypotheses');
      const id = str(a.asset_id);
      if (id !== 'SENSOR') assetOr(id);
      const h = db.hypothesis.insert({ id: 0n, invId, assetId: id, text: str(a.mechanism).slice(0, 160), likelihood: 0 });
      const link = (ids: unknown[], supports: boolean) => {
        for (const x of ids) {
          const ev = db.evidence.id.find(BigInt(Math.max(0, Math.floor(num(x, -1)))));
          if (ev && ev.invId === invId) db.hypothesisEvidence.insert({ id: 0n, invId, hypothesisId: h.id, evidenceId: ev.id, supports });
        }
      };
      link(arr(a.supporting_evidence_ids), true);
      link(arr(a.contradicting_evidence_ids), false);
      // evidence about this asset that wasn't linked yet
      for (const ev of [...db.evidence.iter()]) if (ev.invId === invId && ev.hypothesisId === 0n && ev.aboutAsset === id) db.evidence.id.update({ ...ev, hypothesisId: h.id });
      relink(tx, invId);
      return { out: { hypothesis_id: Number(h.id) }, line: `${id}: ${str(a.mechanism)}` };
    }
    case 'post_action_candidate': {
      const kind = str(a.kind);
      if (WALL_KINDS.includes(kind)) {
        // wall-change steps are priced by the structural assessment, never by the model
        const part = inv.subject ? assessRemoval(tx, inv.subject).parts.find(p => p.kind === kind) : undefined;
        if (!part) throw new Error(`${kind} does not apply to this change`);
        if ([...db.actionCandidate.iter()].some(x => x.invId === invId && x.kind === kind)) return { out: { ok: true, note: 'already proposed' }, line: `${part.title} (already proposed)` };
        db.actionCandidate.insert({ id: 0n, invId, kind, target: inv.subject, title: part.title, detail: part.detail, cost: part.cost, saved: 0, rank: Number(db.actionCandidate.count()) + 1,
          recommended: removalOk(assessRemoval(tx, inv.subject).verdict), status: 'proposed' });
        return { out: { ok: true, cost: part.cost }, line: `${part.title} · ${money(part.cost)}` };
      }
      const k = ACTION_KINDS[kind];
      if (!k) throw new Error(`kind must be one of ${Object.keys(ACTION_KINDS).join(', ')}`);
      if ([...db.actionCandidate.iter()].some(x => x.invId === invId && x.kind === kind)) return { out: { ok: true, note: 'already proposed' }, line: `${k.title} (already proposed)` };
      db.actionCandidate.insert({ id: 0n, invId, kind, target: k.target, title: k.title, detail: str(a.rationale) ? str(a.rationale).slice(0, 140) : k.detail, cost: k.cost, saved: 0, rank: 0, recommended: false, status: 'proposed' });
      return { out: { ok: true, cost: k.cost }, line: `${k.title} · ${k.cost > 0 ? money(k.cost) : 'free'}` };
    }
    case 'write_decision': {
      const top = [...db.hypothesis.iter()].filter(h => h.invId === invId).sort((x, y) => y.likelihood - x.likelihood)[0];
      const rootAsset = str(a.root_cause_asset);
      const h = [...db.hypothesis.iter()].find(x => x.invId === invId && x.assetId === rootAsset) ?? top;
      db.decision.insert({ id: 0n, invId, rootCause: h?.text ?? rootAsset, likelihood: h?.likelihood ?? 0,
        summary: `${str(a.summary)}${a.what_would_change_my_mind ? ' Would change our mind: ' + str(a.what_would_change_my_mind) : ''}`.slice(0, 600) });
      return { out: { ok: true }, line: `root cause ${rootAsset} (${Math.round((h?.likelihood ?? 0) * 100)}%)` };
    }
    case 'get_sensor_history': {
      const x = assetOr(str(a.asset_id));
      const pts = [...db.sensorReading.assetId.filter(x.id)].filter(r => r.hoursAgo % 1 === 0).sort((p, q) => q.hoursAgo - p.hoursAgo)
        .map(r => ({ hours_ago: r.hoursAgo, value: Math.round(r.value * 100) / 100 }));
      return { out: { asset: x.id, unit: x.readingUnit, live: x.reading, normal: x.baseline, hourly: pts }, line: `${x.id}: ${x.reading.toFixed(1)} ${x.readingUnit} now, normal ${x.baseline.toFixed(1)}` };
    }
    case 'detect_anomaly': {
      const res = [...db.asset.iter()].filter(x => x.kind === 'chiller').map(x => ({ asset: x.id, z: Math.round(anomalyScore(tx, x.id).z * 10) / 10, live: x.reading }));
      res.sort((p, q) => q.z - p.z);
      const top = db.asset.id.find(res[0].asset)!;
      if (res[0].z > 4 && top.health === 'ok') db.asset.id.update({ ...top, health: 'watch' });
      emit(tx, { kind: 'pulse', ids: [res[0].asset], color: 'warn' });
      return { out: { scores: res, note: 'z > 4 is far outside normal' }, line: res.map(r => `${r.asset} ${r.z}σ`).join(' · ') };
    }
    case 'forecast_threshold': {
      const x = assetOr(str(a.asset_id));
      if (x.readingUnit !== 'mm/s') return { out: { asset: x.id, note: 'no alarm limit configured for this sensor' }, line: `${x.id}: no limit configured` };
      const f = forecastThreshold(tx, x.id);
      return { out: { asset: x.id, limit: VIB_LIMIT, slope_per_hour: Math.round(f.slope * 100) / 100, hours_to_limit: Math.round(f.hours * 10) / 10, eta: f.hours > 0 ? clock(f.hours) : 'none' },
        line: f.hours > 0 ? `${x.id} hits ${VIB_LIMIT} mm/s at ≈${clock(f.hours)} (+${f.hours.toFixed(1)} h)` : `${x.id}: no upward trend` };
    }
    case 'check_sensor_health': {
      const x = assetOr(str(a.asset_id));
      const pts = [...db.sensorReading.assetId.filter(x.id)].filter(r => r.hoursAgo <= 8).map(r => r.value);
      let rough = 0; for (let i = 1; i < pts.length; i++) rough += Math.abs(pts[i] - pts[i - 1]);
      rough = pts.length > 1 ? rough / (pts.length - 1) : 0;
      const sibling = [...db.asset.iter()].find(y => y.kind === x.kind && y.id !== x.id);
      return { out: { asset: x.id, step_noise: Math.round(rough * 100) / 100, sibling: sibling ? { id: sibling.id, live: sibling.reading, normal: sibling.baseline } : null, verdict: rough < 0.6 ? 'smooth trend, sensor looks healthy' : 'noisy, sensor may be faulty' },
        line: rough < 0.6 ? `${x.id} sensor looks healthy (smooth trend)` : `${x.id} sensor is noisy` };
    }
    case 'get_asset': {
      const x = assetOr(str(a.asset_id));
      const age = 2026 - x.installYear;
      return { out: { id: x.id, name: x.name, kind: x.kind, room: x.roomId, installed: x.installYear, age_years: age, expected_life_years: x.expectedLife, capacity_tons: x.capacity, health: x.health },
        line: `${x.name}: ${age} of ${x.expectedLife} years used` };
    }
    case 'get_work_orders': {
      const x = assetOr(str(a.asset_id));
      const w = [...db.workOrder.iter()].filter(o => o.assetId === x.id).map(o => ({ id: o.id, title: o.title, status: o.status, age_days: o.ageDays }));
      const open = w.filter(o => o.status === 'open');
      return { out: { asset: x.id, work_orders: w }, line: `${open.length} open on ${x.id}${open.length ? ': ' + open.map(o => o.id).join(', ') : ''}` };
    }
    case 'get_structure': {
      const wid = str(a.wall_id), rid = str(a.room_id);
      const rows = [...db.wall.iter()].filter(w2 => (!wid || w2.id === wid) && (!rid || roomsBeside(tx, w2).some(r => r.id === rid)));
      if (wid && !rows.length) throw new Error(`unknown wall. Valid: ${[...db.wall.iter()].map(x => x.id).join(', ')}`);
      const out = rows.map(w2 => ({ id: w2.id, name: w2.name, kind: w2.kind, material: w2.material, load_kN_per_m: w2.loadKnm, capacity_kN_per_m: w2.capacityKnm, utilization: w2.utilization, carries: w2.carries, condition: w2.health, fire_rated: w2.fireRated, inside: w2.services, rooms: roomsBeside(tx, w2).map(r => r.id) }));
      return { out: { walls: out }, line: out.slice(0, 3).map(w2 => `${w2.id} ${w2.kind} ${Math.round(w2.utilization * 100)}%`).join(' · ') };
    }
    case 'assess_wall_removal': {
      const r = assessRemoval(tx, str(a.wall_id));
      return { out: r, line: `${r.verdict === 'go' ? 'removable' : r.verdict === 'go_with_beam' ? `needs ${r.beam} beam` : r.verdict.replace('_', ' ')} · ${money(r.cost)} · ${r.days} days` };
    }
    case 'assess_crack_growth': {
      const g = crackGrowth(tx);
      return { out: { issue: 'ISS-2', width_mm: g.now, limit_mm: g.limit, growth_mm_per_week: g.perWeek, weeks_to_limit: g.weeks, emergency_repair_usd: g.emergencyCost, planned_repair_usd: g.plannedCost },
        line: `${g.now} → ${g.limit} mm in ~${g.weeks} weeks` };
    }
    case 'get_load_path': {
      const id = str(a.element_id);
      if (!db.element.id.find(id)) throw new Error(`unknown element. Try a wall id, RT-1, RT-2 or a bay id`);
      const paths = loadPath(tx, id);
      const direct = [...db.elementEdge.toId.filter(id)].filter(e => e.rel === 'supports' || e.rel === 'carries').map(e => ({ by: e.fromId, value: e.value }));
      return { out: { element: id, held_up_by: direct, paths }, line: `${id} ← ${direct.map(d => d.by).join(', ')}` };
    }
    case 'check_code': {
      const w2 = db.wall.id.find(str(a.wall_id));
      if (!w2) throw new Error('unknown wall');
      const r = assessRemoval(tx, w2.id);
      const issues = [w2.fireRated && roomsBeside(tx, w2).some(x => x.kind === 'server') ? 'required 1-hour fire separation for the server room' : '',
        w2.kind === 'exterior' ? 'exterior wall: facade and lateral bracing' : '', w2.health === 'damaged' ? 'cracked: capacity reduced 15%' : ''].filter(x => x);
      return { out: { wall: w2.id, blockers: issues, verdict: r.verdict }, line: issues.length ? issues.join('; ') : 'no code blockers' };
    }
    case 'check_wall_services': {
      const w2 = db.wall.id.find(str(a.wall_id));
      if (!w2) throw new Error('unknown wall');
      return { out: { wall: w2.id, inside: w2.services || 'nothing', reroute_cost_usd: w2.services ? 4100 : 0 }, line: w2.services ? `${w2.services} inside` : 'nothing runs inside' };
    }
    case 'get_field_notes': {
      const notes = [...db.fieldNote.iter()].filter(n => !n.isPrivate).map(n => ({ id: Number(n.id), by: `${n.authorName} (${n.role})`, where: n.targetId || 'on site', text: n.text }));
      return { out: { notes }, line: notes.length ? `${notes.length} field notes: "${notes[notes.length - 1].text.slice(0, 60)}"` : 'no field notes yet' };
    }
    case 'get_inspection_issues': {
      const only = str(a.asset_id);
      const list = [...db.issue.iter()].filter(i => i.status !== 'resolved' && (!only || i.assetId === only))
        .map(i => ({ id: i.id, kind: i.kind, severity: i.severity, title: i.title, detail: i.detail, room: i.roomId, asset: i.assetId, source: i.source }));
      return { out: { issues: list }, line: `${list.length} open findings${list.length ? ': ' + list.map(i => i.title.toLowerCase()).slice(0, 2).join(', ') : ''}` };
    }
    case 'search_past_incidents': {
      const words = str(a.query).toLowerCase().split(/\W+/).filter(w => w.length > 3);
      const hits = [...db.pastIncident.iter()].filter(i => words.some(w => i.summary.toLowerCase().includes(w) || i.assetId.toLowerCase() === w))
        .map(i => ({ id: i.id, asset: i.assetId, summary: i.summary, hours_down: i.hoursDown, cost: i.cost }));
      return { out: { results: hits }, line: hits.length ? `${hits[0].id}: ${hits[0].hours_down} h down, ${money(hits[0].cost)}` : 'no similar incidents' };
    }
    case 'get_dependents': {
      const x = assetOr(str(a.asset_id));
      const deps = [...db.dependency.iter()];
      const seen = new Set<string>([x.id]); const q = [x.id]; const chain: string[] = [];
      while (q.length) { const c = q.shift()!; for (const d of deps) if (d.fromId === c && !seen.has(d.toId)) { seen.add(d.toId); q.push(d.toId); chain.push(`${c}->${d.toId}`); } }
      const rooms = [...seen].map(id => db.room.id.find(id)).filter(r => r).map(r => ({ id: r!.id, name: r!.name, tenant: r!.tenantId, kind: r!.kind }));
      if (rooms.length) emit(tx, { kind: 'thread', ids: [x.id, ...[...seen].filter(id => db.asset.id.find(id) && id !== x.id).slice(0, 1), rooms.find(r => r.kind === 'server')?.id ?? rooms[0].id] });
      return { out: { asset: x.id, chain, rooms }, line: `${x.id} → ${rooms.map(r => r.name).join(', ') || 'nothing'}` };
    }
    case 'get_tenants': {
      const ts = [...db.tenant.iter()].map(x => ({ id: x.id, name: x.name, business: x.business, cost_per_hour: x.costPerHour, rooms: [...db.room.iter()].filter(r => r.tenantId === x.id).map(r => r.id) }));
      return { out: { tenants: ts }, line: ts.map(x => x.name).join(', ') };
    }
    case 'get_lease_terms': {
      const x = db.tenant.id.find(str(a.tenant_id));
      if (!x) throw new Error(`unknown tenant. Valid: ${[...db.tenant.iter()].map(y => y.id).join(', ')}`);
      return { out: { tenant: x.id, name: x.name, cost_per_hour: x.costPerHour, allowed_downtime_hours: x.allowedDowntimeHours, penalty: x.penalty, clause: x.clause },
        line: `${x.name}: ${money(x.costPerHour)}/h, ${x.allowedDowntimeHours} h allowed, ${x.penalty ? money(x.penalty) + ' penalty' : 'no penalty'}` };
    }
    case 'simulate_cascade': {
      const acts = arr(a.actions);
      const plan = acts.length ? planFromActions(acts) : NO_PLAN;
      const f = forecastThreshold(tx, 'CH-2');
      const r = simulate(b(), plan, f.hours);
      const bb = b();
      const ch1Load = bb.rooms.filter(rm => bb.chillerOf.get(rm.id) === 'CH-1').reduce((sum, rm) => sum + rm.demand, 0);
      const srv = bb.rooms.find(rm => rm.kind === 'server')!;
      if (!acts.length) {
        const prev = [...db.scenario.iter()].find(sc => sc.label === 'do_nothing');
        const risk = prev && prev.riskJson.length > 4 ? { runs: JSON.parse(prev.riskJson) as number[][], chance: prev.outageChance } : null;
        saveScenario(tx, invId, 'do_nothing', 'Do nothing', false, bb, r, risk);
        const ch2 = db.asset.id.find('CH-2');
        if (ch2 && r.failed) db.asset.id.update({ ...ch2, health: 'critical' });
        if (r.outageHour >= 0) setTimeline(tx, { hour: Math.min(24, r.outageHour + 0.5), label: 'do_nothing' });
        emit(tx, { kind: 'highlight', ids: ['CH-2', 'AHU-2', 'SRV300'] });
      }
      return { out: { actions: acts, chiller2_fails_at: r.failed ? clock(f.hours) : 'does not fail', server_room_down_at: r.outageHour >= 0 ? clock(r.outageHour) : 'never',
        downtime_hours: r.downtimeHours, impact_usd: r.impact, tenants_affected: r.tenantsAffected, uptime_breaches: r.breaches,
        server_room_peak_f: Math.max(...r.serverTemps), server_room_limit_f: srv.limitTemp, chiller1_spare_tons: (bb.cap.get('CH-1') ?? 0) - ch1Load, server_room_needs_tons: srv.demand },
        line: r.outageHour >= 0 ? `server room down ${clock(r.outageHour)} · ${r.downtimeHours} h · ${money(r.impact)}` : `no outage · peak ${Math.max(...r.serverTemps).toFixed(0)}°F · ${money(r.impact)}` };
    }
    case 'run_risk_estimate': {
      const acts = arr(a.actions);
      const plan = acts.length ? planFromActions(acts) : NO_PLAN;
      const f = forecastThreshold(tx, 'CH-2');
      const risk = riskEstimate(tx, b(), plan, f.hours, failureChance(tx, 'CH-2'));
      if (!acts.length) saveScenario(tx, invId, 'do_nothing', 'Do nothing', false, b(), simulate(b(), NO_PLAN, f.hours), risk);
      return { out: { actions: acts, runs: 50, outage_chance: Math.round(risk.chance * 100) / 100, likely_window: risk.early >= 0 ? `${clock(risk.early)}–${clock(risk.late)}` : 'none', expected_impact_usd: Math.round(risk.expected) },
        line: `outage in ${Math.round(risk.chance * 50)}/50 runs${risk.early >= 0 ? `, ${clock(risk.early)}–${clock(risk.late)}` : ''} · expected ${money(risk.expected)}` };
    }
    case 'compare_scenarios': {
      const acts = [...db.actionCandidate.iter()].filter(x => x.invId === invId);
      if (!acts.length) throw new Error('no action candidates yet');
      const f = forecastThreshold(tx, 'CH-2'), p = failureChance(tx, 'CH-2'), bb = b();
      const base = riskEstimate(tx, bb, NO_PLAN, f.hours, p).expected;
      const scored = acts.map(x => ({ x, saved: Math.max(0, base - riskEstimate(tx, bb, planFromActions([x.kind]), f.hours, p).expected) }));
      scored.sort((m, n) => n.saved / Math.max(100, n.x.cost) - m.saved / Math.max(100, m.x.cost));
      // recommended plan: everything except portable cooling, unless it is still needed
      let kinds = acts.map(x => x.kind).filter(k => k !== 'portable_cooling');
      let risk = riskEstimate(tx, bb, planFromActions(kinds), f.hours, p);
      if (risk.chance > 0.05 && acts.some(x => x.kind === 'portable_cooling')) { kinds = [...kinds, 'portable_cooling']; risk = riskEstimate(tx, bb, planFromActions(kinds), f.hours, p); }
      scored.forEach((s2, i) => db.actionCandidate.id.update({ ...s2.x, saved: Math.round(s2.saved), rank: i + 1, recommended: kinds.includes(s2.x.kind) }));
      saveScenario(tx, invId, 'with_plan', 'Act now', false, bb, simulate(bb, planFromActions(kinds), f.hours), risk);
      return { out: { do_nothing_expected_usd: Math.round(base), ranked: scored.map((s2, i) => ({ rank: i + 1, kind: s2.x.kind, cost: s2.x.cost, saved_usd: Math.round(s2.saved) })),
        recommended_plan: kinds, plan_outage_chance: risk.chance, plan_expected_usd: Math.round(risk.expected) },
        line: `plan cuts ${money(base)} → ${money(risk.expected)} · outage ${Math.round(risk.chance * 100)}%` };
    }
    case 'get_evidence_chain': {
      const hs = [...db.hypothesis.iter()].filter(h => h.invId === invId).map(h => ({ id: Number(h.id), asset: h.assetId, text: h.text, likelihood: Math.round(h.likelihood * 100) / 100 }));
      const ev = [...db.evidence.iter()].filter(e => e.invId === invId).map(e => ({ id: Number(e.id), agent: e.agent, fact: e.fact, source: `${e.sourceTable}/${e.sourceRow}`, about: e.aboutAsset, supports: e.supports, confidence: e.confidence }));
      const acts = [...db.actionCandidate.iter()].filter(x => x.invId === invId).map(x => ({ kind: x.kind, rank: x.rank, cost: x.cost, saved_usd: x.saved, recommended: x.recommended }));
      const sc = [...db.scenario.iter()].map(x => ({ label: x.label, outage_chance: x.outageChance, impact_usd: x.impact, downtime_hours: x.downtimeHours }));
      const cr = [...db.critique.iter()].filter(c => c.invId === invId).map(c => ({ verdict: c.verdict, text: c.text }));
      const tenants = [...db.user.iter()].filter(u => u.online).map(u => ({ name: u.name, role: u.role }));
      return { out: { hypotheses: hs, evidence: ev, actions: acts, scenarios: sc, critiques: cr, people: tenants }, line: `${hs.length} hypotheses · ${ev.length} evidence · ${acts.length} actions` };
    }
    case 'post_critique': {
      const verdict = str(a.verdict) === 'revise' ? 'revise' : 'approve';
      const reqs = arr(a.requests).slice(0, 3).map(r => r as Json).filter(r => ['signal', 'historian', 'lease', 'structural', 'simulation'].includes(str(r.agent_type)));
      const text = verdict === 'revise' && reqs.length ? reqs.map(r => str(r.request)).join(' ') : str(a.summary);
      db.critique.insert({ id: 0n, invId, verdict: verdict === 'revise' && reqs.length ? 'revise' : 'approve', text: text.slice(0, 300) });
      if (verdict === 'revise' && reqs.length && inv.critiqueRounds < 1) {
        for (const r of reqs) {
          const st = [...db.subtask.iter()].find(x => x.invId === invId && x.agent === str(r.agent_type));
          if (st) db.subtask.id.update({ ...st, status: 'reopened', goal: str(r.request).slice(0, 200) });
          else db.subtask.insert({ id: 0n, invId, agent: str(r.agent_type), goal: str(r.request).slice(0, 200), dependsOn: '', status: 'reopened' });
        }
        db.investigation.id.update({ ...db.investigation.id.find(invId)!, pending: reqs.map(r => str(r.agent_type)).join(',') });
        return { out: { ok: true, sent_back_to: reqs.map(r => r.agent_type) }, line: `REVISE: ${text}` };
      }
      return { out: { ok: true }, line: `APPROVED: ${str(a.summary)}` };
    }
    case 'post_answer': {
      const role = str(a.role) === 'technician' ? 'technician' : 'manager';
      for (const old of [...db.answer.iter()]) if (old.invId === invId && old.role === role) db.answer.id.delete(old.id);
      db.answer.insert({ id: 0n, invId, role, text: str(a.text).replace(/\*\*/g, '').slice(0, 420), byNemotron: true });
      return { out: { ok: true }, line: `${role}: ${str(a.text)}` };
    }
    case 'issue_scene_command': {
      const kind = str(a.kind);
      const ids = arr(a.ids).map(x => str(x)).filter(id => db.asset.id.find(id) || db.room.id.find(id));
      const target = str(a.target) || ids[0] || '';
      if (kind === 'guide_trail') emit(tx, { kind, target, forRole: 'technician' });
      else if (kind === 'focus') emit(tx, { kind, target });
      else emit(tx, { kind, ids });
      return { out: { ok: true }, line: `${kind} ${target || ids.join(' → ')}` };
    }
  }
  throw new Error(`unknown tool ${call.name}`);
}

// ---------------------------------------------------------------- Nemotron calls

type AiCfg = { key: string; base: string; large: string; small: string };

function cfgOf(tx: Ctx): AiCfg | null {
  const key = secretOf(tx, 'nvidia_api_key');
  const st = tx.db.aiStatus.id.find(0);
  if (!key || !st?.enabled) return null;
  return { key, base: secretOf(tx, 'nvidia_base_url') || DEFAULT_BASE, large: st.modelLarge, small: st.modelSmall || st.modelLarge };
}

function thinkPrefix(model: string, think: boolean): string {
  if (/nemotron-3/.test(model)) return ''; // Nemotron 3.x: chat_template_kwargs.enable_thinking
  if (/super-49b-v1$/.test(model) || /nemotron-70b|ultra-253b-v1$/.test(model)) return think ? 'detailed thinking on' : 'detailed thinking off';
  return think ? '/think' : '/no_think';
}

type ChatOut = { content: string; reasoning: string; calls: Call[]; raw: Json };

function chat(ctx: PCtx, cfg: AiCfg, model: string, messages: Json[], tools: ToolDef[], think: boolean, meta: { invId: bigint; agent: string } = { invId: 0n, agent: '' }, force?: string): ChatOut {
  const t0 = ctx.withTx(tx => tx.timestamp.microsSinceUnixEpoch);
  const log = (ok: boolean, usage: Json | undefined) => ctx.withTx(tx => {
    const ms = Number(tx.timestamp.microsSinceUnixEpoch - t0) / 1000;
    tx.db.llmCall.insert({ id: 0n, invId: meta.invId, agent: meta.agent, model, ms, ok,
      promptTokens: Math.round(num(usage?.prompt_tokens)), completionTokens: Math.round(num(usage?.completion_tokens)) });
  });
  const body = {
    model, messages, temperature: think ? 0.6 : 0.2, top_p: 0.95, max_tokens: think ? 4096 : 1200,
    ...(/nemotron-3/.test(model) ? { chat_template_kwargs: { enable_thinking: think } } : {}),
    tools: tools.map(td => ({ type: 'function', function: { name: td.name, description: td.description,
      parameters: { type: 'object', properties: td.params, required: td.required ?? ['why'] } } })),
    tool_choice: force ? { type: 'function', function: { name: force } } : 'auto',
  };
  const res = ctx.http.fetch(`${cfg.base}/chat/completions`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', accept: 'application/json', authorization: `Bearer ${cfg.key}` },
    body: JSON.stringify(body),
    timeout: TimeDuration.fromMillis(think ? 45000 : model === cfg.small ? 20000 : 30000),
  });
  const text = res.text();
  if (res.status === 400 && force) return chat(ctx, cfg, model, messages, tools, think, meta); // endpoint without named tool_choice
  if (res.status !== 200) { log(false, undefined); throw new Error(`Nemotron HTTP ${res.status}: ${text.slice(0, 160)}`); }
  const j = JSON.parse(text) as Json;
  log(true, j.usage as Json | undefined);
  const m = ((j.choices as Json[] | undefined)?.[0]?.message ?? {}) as Json;
  let content = str(m.content);
  let reasoning = str(m.reasoning_content) || str(m.reasoning);
  const th = content.match(/<think>([\s\S]*?)<\/think>/);
  if (th) { reasoning = reasoning || th[1]; content = content.replace(th[0], ''); }
  content = content.replace(/<\/?think>/g, '').trim();
  let calls: Call[] = arr(m.tool_calls).map((c, i) => {
    const cj = c as Json; const fn = (cj.function ?? {}) as Json;
    let args: Json = {};
    try { args = typeof fn.arguments === 'string' ? JSON.parse(fn.arguments as string) : (fn.arguments as Json) ?? {}; } catch { args = {}; }
    return { id: str(cj.id) || `call_${i}`, name: str(fn.name), args };
  });
  if (!calls.length) {
    const tc = content.match(/<TOOLCALL>([\s\S]*?)<\/TOOLCALL>/);
    if (tc) {
      try {
        calls = (JSON.parse(tc[1]) as Json[]).map((c, i) => ({ id: `call_${i}`, name: str(c.name), args: (typeof c.arguments === 'string' ? JSON.parse(c.arguments as string) : c.arguments) as Json ?? {} }));
        content = content.replace(tc[0], '').trim();
      } catch { /* ignore */ }
    }
  }
  return { content, reasoning: reasoning.trim(), calls, raw: m };
}

const PREAMBLE = (inv: { id: bigint; trigger: string }, assetIds: string, roomIds: string, budget: number) =>
  `You are part of Cascade, a team of AI agents that keeps the building "Harbor Point, Floor 3" running. ` +
  `The team investigates operational risks, predicts how failures spread, and recommends fixes before tenants are affected.\n` +
  `You work on investigation ${inv.id}: "${inv.trigger}". Simulation clock: Tuesday 14:00.\n` +
  `You share a blackboard with the other agents. You never talk to them directly; you read what they posted and post your own results with your tools.\n` +
  `RULES: You know nothing about the building except what your tools return. Never invent ids, numbers, dates, names or facts; every number must come from a tool result. ` +
  `Valid asset ids: ${assetIds}. Valid room ids: ${roomIds}. Every tool call needs a "why": one plain sentence under 15 words; people watch these live in VR. ` +
  `Every piece of evidence must cite its source table and row id. Stay inside your role. Use at most ${budget} tool calls. Prefer calling several tools at once.`;

const ROLE_PROMPTS: Record<string, string> = {
  plan: 'ROLE: PLANNER. Call get_investigation and get_building_summary, then create_subtasks with 4-6 subtasks: agent_type (signal, historian, lease, structural, simulation, critic), a one-sentence goal, and depends_on. For an equipment risk, simulation depends on the evidence subtasks. For a proposed wall change, the structural engineer leads and there is no simulation subtask.',
  structural: 'ROLE: STRUCTURAL ENGINEER. Check the load path and condition of the walls involved (get_structure), recorded damage (get_inspection_issues) and how fast cracks are growing (assess_crack_growth). For a proposed wall change, run assess_wall_removal and check_wall_services and say plainly whether it is load-bearing, what beam/shoring it needs, what runs inside, the cost and the time. Post 2-4 pieces of evidence (source_table wall or issue), then complete_subtask.',
  signal: 'ROLE: SIGNAL ANALYST. Check sensor history for the chillers. Use detect_anomaly to find unusual readings, forecast_threshold for hours until the limit, and check_sensor_health to rule out a faulty sensor. Post 2-4 pieces of evidence with post_evidence, then complete_subtask.',
  historian: 'ROLE: MAINTENANCE HISTORIAN. Check the suspicious asset\'s age against expected life, its open and recent work orders, get_inspection_issues for physical damage it has already caused, and search_past_incidents for similar failures. Flag tickets that were ignored or repeated. Post 2-4 pieces of evidence, then complete_subtask.',
  lease: 'ROLE: TENANT AND LEASE ANALYST. Use get_dependents to find every room the suspicious chiller serves, then get_tenants and get_lease_terms. Report who is affected, their cost of downtime per hour, uptime clauses and penalties. Post 2-4 pieces of evidence, then complete_subtask.',
  hypothesize: 'ROLE: PLANNER. Read get_evidence_chain, then call post_hypothesis for up to 3 root causes (one may be "SENSOR" for a faulty sensor). Each names an asset, the failure mechanism in one sentence, and the evidence ids that support or contradict it.',
  simulate: 'ROLE: SIMULATION ENGINEER. Run simulate_cascade with no actions (the do-nothing baseline) and run_risk_estimate with no actions. Post the outage time, downtime and impact as evidence (source_table scenario, source_row do_nothing), then complete_subtask.',
  propose: 'ROLE: PLANNER. Read get_evidence_chain, then call post_action_candidate for 3-5 actions. For an equipment risk: work_order (planned repair), load_shift, tenant_notice, portable_cooling, structural_repair (stop vibration damage to walls). For a proposed wall change: engineer_review, temporary_shoring and install_beam (load-bearing), remove_partition (partitions), reroute_services, dust_containment; only the steps the assessment says apply.',
  compare: 'ROLE: SIMULATION ENGINEER. Call compare_scenarios to test every proposed action against doing nothing. If a repair takes a chiller offline, also simulate_cascade with the recommended plan to check the server room stays under its limit.',
  critique: 'ROLE: CRITIC. Find what the team got wrong before a person acts on it. Call get_evidence_chain, then check: 1) every hypothesis has 2+ independent sources; 2) contradicting evidence was not ignored; 3) no action rests on an unchecked assumption, for example whether Chiller 1 has enough spare capacity during a repair; 4) every action was verified by simulation; 5) impact numbers come from tool results. Then post_critique: "approve", or "revise" with at most 3 specific requests, each naming an agent_type. Be strict but fair.',
  decide: 'ROLE: PLANNER. The Critic approved. Call get_evidence_chain, then write_decision with the root cause, the evidence chain in order, the recommended actions in ranked order with costs, and what would change your mind.',
  narrate: 'ROLE: NARRATOR. Turn the decision into speech. Call get_evidence_chain, then post_answer twice, at most 3 short sentences each, plain spoken English, no markdown: technician = what is wrong, where to go, what to check first; manager = the risk, the cost if ignored, the recommended action and its cost, and ask them to approve. Then issue_scene_command guide_trail to the root-cause asset and a thread from it to the affected room.',
};

const PHASE_AGENT: Record<string, string> = {
  plan: 'planner', signal: 'signal', historian: 'historian', lease: 'lease', structural: 'structural', hypothesize: 'planner', simulate: 'simulation',
  propose: 'planner', compare: 'simulation', critique: 'critic', decide: 'planner', narrate: 'narrator',
};
const THINKERS = new Set(['hypothesize', 'critique']); // reasoning mode only where judgment matters
const LARGE_PHASES = new Set(['plan', 'hypothesize', 'critique', 'decide', 'propose']);

/** The database pre-runs the read tools a specialist always needs, logs them as that agent's steps, and hands
 *  the results over in the prompt: one model turn instead of three. */
function brief(tx: Ctx, invId: bigint, role: string, kind: string, subject: string, model: string): string {
  const top = [...tx.db.asset.iter()].filter(a => a.kind === 'chiller').sort((a, b) => anomalyScore(tx, b.id).z - anomalyScore(tx, a.id).z)[0]?.id ?? 'CH-2';
  const plan: [string, Json][] =
    role === 'signal' ? [['detect_anomaly', {}], ['forecast_threshold', { asset_id: top }], ['check_sensor_health', { asset_id: top }]]
    : role === 'historian' ? [['get_asset', { asset_id: top }], ['get_work_orders', { asset_id: top }], ['get_inspection_issues', kind === 'structural_change' ? {} : { asset_id: top }], ['get_field_notes', {}]]
    : role === 'lease' ? (kind === 'structural_change' ? [['get_tenants', {}]] : [['get_dependents', { asset_id: top }], ['get_tenants', {}]])
    : role === 'structural' ? (kind === 'structural_change'
        ? [['get_structure', { wall_id: subject }], ['assess_wall_removal', { wall_id: subject }], ['check_code', { wall_id: subject }], ['check_wall_services', { wall_id: subject }]]
        : [['get_structure', { room_id: tx.db.asset.id.find(top)?.roomId ?? 'MECH' }], ['assess_crack_growth', {}], ['get_load_path', { element_id: 'RT-1' }]])
    : [];
  const out: string[] = [];
  for (const [name, args] of plan) {
    try {
      const r = execTool(tx, invId, role, { id: name, name, args });
      logStep(tx, invId, role, name, `Pre-fetched: ${name.replace(/_/g, ' ')}`, r.line, toolTarget(args), 'done', model);
      tx.db.toolCall.insert({ id: 0n, invId, agent: role, tool: name, ok: true, error: 'prefetch', model, ms: 0 });
      out.push(`${name}(${JSON.stringify(args)}) → ${JSON.stringify(r.out).slice(0, 1200)}`);
    } catch (e) { out.push(`${name} failed: ${String(e).slice(0, 120)}`); }
  }
  return out.join('\n');
}

/** One agent's tool-calling loop. Returns true if the phase's goal was met. */
function runAgent(ctx: PCtx, cfg: AiCfg, invId: bigint, phase: string, note: string): boolean {
  const base = phase.split(':')[0];
  // revisions go to the specialist the Critic named, not the planner
  const agent = base === 'revise' ? (note.split('|')[0] || 'planner') : PHASE_AGENT[base] ?? 'planner';
  const think = THINKERS.has(base);
  const model = LARGE_PHASES.has(base) ? cfg.large : cfg.small;
  const toolNames = base === 'revise' ? (AGENT_TOOLS[note.split('|')[0]] ?? AGENT_TOOLS.signal) : AGENT_TOOLS[base];
  const tools = toolNames.map(n => TOOLS[n]);
  const setup = ctx.withTx(tx => {
    const inv = tx.db.investigation.id.find(invId);
    if (!inv) return null;
    const assets = [...tx.db.asset.iter()].map(x => x.id).join(', ');
    const rooms = [...tx.db.room.iter()].map(x => x.id).join(', ');
    const sub = [...tx.db.subtask.iter()].find(s => s.invId === invId && s.agent === agent && s.status !== 'done');
    if (sub && (sub.status === 'ready' || sub.status === 'reopened')) tx.db.subtask.id.update({ ...sub, status: 'claimed' });
    const done = [...tx.db.subtask.iter()].filter(s => s.invId === invId && s.status === 'done').map(s => `${s.agent}: ${s.goal}`).join('\n');
    const briefing = brief(tx, invId, base === 'revise' ? note.split('|')[0] : base, inv.kind, inv.subject, model);
    return { inv: { id: inv.id, trigger: inv.trigger }, assets, rooms, goal: sub?.goal ?? '', done, kind: inv.kind, subject: inv.subject, briefing };
  });
  if (!setup) return false;

  const rolePrompt = base === 'revise'
    ? `${ROLE_PROMPTS[note.split('|')[0] === 'simulation' ? 'compare' : note.split('|')[0]] ?? ''}\nTHE CRITIC SENT WORK BACK: ${note.split('|')[1] ?? ''}\nAnswer exactly that request with tools, post evidence, then complete_subtask.`
    : ROLE_PROMPTS[base];
  const system = `${thinkPrefix(model, think)}\n${PREAMBLE(setup.inv, setup.assets, setup.rooms, 8)}\n\n${rolePrompt}`;
  const user = `${setup.kind === 'structural_change' ? `INVESTIGATION TYPE: proposed structural change, remove wall ${setup.subject}.\n` : 'INVESTIGATION TYPE: operational risk.\n'}YOUR SUBTASK: ${setup.goal || setup.inv.trigger}\nCONTEXT FROM FINISHED SUBTASKS:\n${setup.done || '(none yet)'}\n${setup.briefing ? `BRIEFING (already read from the database for you; cite it, do not re-fetch):\n${setup.briefing}\nPost your evidence and complete_subtask now, in as few turns as possible; call more tools only if something is missing.` : 'Start now by calling tools.'}`;
  const messages: Json[] = [{ role: 'system', content: system }, { role: 'user', content: user }];

  let calls = 0, nudged = false;
  const seen = new Set<string>();
  for (let round = 0; round < 5 && calls < 10; round++) {
    const thinking = ctx.withTx(tx => logStep(tx, invId, agent, 'thinking', think ? 'Reasoning…' : 'Choosing tools…', '', '', 'thinking', model));
    let out: ChatOut;
    try { out = chat(ctx, cfg, model, messages, tools, think, { invId, agent }); }
    catch (e) {
      ctx.withTx(tx => { const r = tx.db.agentStep.id.find(thinking.id); if (r) tx.db.agentStep.id.update({ ...r, tool: 'nemotron', why: 'Model call failed; falling back', result: String(e).slice(0, 200), status: 'error' }); });
      return false;
    }
    ctx.withTx(tx => {
      const r = tx.db.agentStep.id.find(thinking.id);
      if (!r) return;
      const firstLine = (out.reasoning || '').replace(/\s+/g, ' ').trim();
      if (firstLine) tx.db.agentStep.id.update({ ...r, tool: 'reasoning', why: firstLine.slice(0, 120), result: firstLine.slice(120, 340), status: 'done' });
      else tx.db.agentStep.id.delete(r.id);
    });
    if (!out.calls.length) {
      if (goalMet(ctx, invId, base, note) || nudged) break;
      nudged = true;
      messages.push({ role: 'assistant', content: out.content || '(no tool call)' });
      messages.push({ role: 'user', content: 'You must act through your tools now. Call the tools needed to finish your job.' });
      continue;
    }
    messages.push({ role: 'assistant', content: out.content || null, tool_calls: out.calls.map(c => ({ id: c.id, type: 'function', function: { name: c.name, arguments: JSON.stringify(c.args) } })) });
    for (const c of out.calls) {
      calls++;
      let result: unknown;
      const sig = c.name + JSON.stringify({ ...c.args, why: undefined });
      const repeat = seen.has(sig);
      seen.add(sig);
      if (repeat && !['post_evidence', 'post_hypothesis', 'post_action_candidate', 'post_answer'].includes(c.name))
        result = { note: 'You already called this with the same arguments; use that result. Next: post your findings with post_evidence, then complete_subtask.' };
      else if (!toolNames.includes(c.name)) result = { error: `tool ${c.name} is not available to you` };
      else {
        try {
          result = ctx.withTx(tx => {
            const r = execTool(tx, invId, agent, c);
            const status = c.name === 'post_critique' && str(r.line).startsWith('REVISE') ? 'revise' : 'done';
            logStep(tx, invId, agent, c.name, str(c.args.why) || c.name.replace(/_/g, ' '), r.line, toolTarget(c.args), status, model);
            tx.db.toolCall.insert({ id: 0n, invId, agent, tool: c.name, ok: true, error: '', model, ms: 0 });
            return r.out;
          });
        } catch (e) {
          const msg = String(e instanceof Error ? e.message : e);
          ctx.withTx(tx => { logStep(tx, invId, agent, c.name, str(c.args.why) || c.name, `rejected: ${msg}`, toolTarget(c.args), 'error', model);
            tx.db.toolCall.insert({ id: 0n, invId, agent, tool: c.name, ok: false, error: msg.slice(0, 200), model, ms: 0 }); });
          result = { error: msg };
        }
      }
      messages.push({ role: 'tool', tool_call_id: c.id, name: c.name, content: JSON.stringify(result).slice(0, 3500) });
    }
    if (goalMet(ctx, invId, base, note) && ['plan', 'hypothesize', 'critique', 'decide'].includes(base)) break;
  }
  return goalMet(ctx, invId, base, note);
}

/** Specialists that work in parallel after the plan. */
function parallelGroup(kind: string): string[] {
  return kind === 'structural_change' ? ['structural', 'historian', 'lease'] : ['signal', 'historian', 'lease', 'structural'];
}

function goalMet(ctx: PCtx, invId: bigint, base: string, note: string): boolean {
  return ctx.withTx(tx => {
    const db = tx.db;
    const mine = <T extends { invId: bigint }>(it: Iterable<T>) => [...it].filter(x => x.invId === invId);
    switch (base) {
      case 'plan': return mine(db.subtask.iter()).length > 0;
      case 'signal': case 'historian': case 'lease': case 'structural': return mine(db.evidence.iter()).some(e => e.agent === base);
      case 'hypothesize': return mine(db.hypothesis.iter()).length > 0;
      case 'simulate': return [...db.scenario.iter()].some(s => s.label === 'do_nothing' && s.riskJson.length > 4);
      case 'propose': return mine(db.actionCandidate.iter()).length > 0;
      case 'compare': return [...db.scenario.iter()].some(s => s.label === 'with_plan') && mine(db.actionCandidate.iter()).every(a => a.rank > 0);
      case 'critique': return mine(db.critique.iter()).length > (db.investigation.id.find(invId)?.critiqueRounds ?? 0);
      case 'decide': return mine(db.decision.iter()).length > 0;
      case 'narrate': return mine(db.answer.iter()).length >= 2;
      case 'revise': { const agent = note.split('|')[0]; return mine(db.subtask.iter()).some(s => s.agent === agent && s.status === 'done') || mine(db.evidence.iter()).length > 0; }
    }
    return false;
  });
}

// Scripted ops (indexes into OPS) used when a phase can't be done by Nemotron.
const FALLBACK: Record<string, number[]> = {
  plan: [0, 1, 2], signal: [3, 5, 11], historian: [6, 7, 8], lease: [9, 10], structural: [21, 22], hypothesize: [4], simulate: [12, 13],
  propose: [14], compare: [15], critique: [18], decide: [19], narrate: [20], revise: [17],
};

// Wall-change fallbacks (WALL_OPS indexes + 100).
const FALLBACK_WALL: Record<string, number[]> = {
  plan: [100, 101], structural: [102, 105, 107], historian: [103], lease: [104], propose: [109], critique: [108], decide: [110], narrate: [111], revise: [107],
};

function runFallback(ctx: PCtx, invId: bigint, base: string) {
  const kind = ctx.withTx(tx => tx.db.investigation.id.find(invId)?.kind ?? 'cascade');
  for (const i of (kind === 'structural_change' ? FALLBACK_WALL : FALLBACK)[base] ?? []) {
    const op = i >= 100 ? WALL_OPS[i - 100] : i >= 21 ? STRUCT_OPS[i - 21] : OPS[i];
    const step = ctx.withTx(tx => logStep(tx, invId, op.agent, op.tool, op.why, '', op.target ?? '', 'running', ''));
    try { ctx.sleep(TimeDuration.fromMillis(700)); } catch { /* no pacing */ }
    ctx.withTx(tx => {
      const inv = tx.db.investigation.id.find(invId);
      if (!inv) return;
      let out: { result: string; status?: string };
      try { out = i >= 100 ? runWallOp(tx, invId, i - 100) : i >= 21 ? runStructOp(tx, invId, i - 21) : runOp(tx, inv, op, i); }
      catch (e) { out = { result: `skipped: ${String(e).slice(0, 120)}`, status: 'error' }; }
      const r = tx.db.agentStep.id.find(step.id);
      if (r) tx.db.agentStep.id.update({ ...r, result: out.result, status: out.status === 'revise' ? 'done' : out.status ?? 'done' });
    });
  }
}

/** Phase machine: which phases run next once one finishes. */
function advance(tx: Ctx, invId: bigint, phase: string) {
  const inv = tx.db.investigation.id.find(invId);
  if (!inv) return;
  const done = new Set(inv.phasesDone ? inv.phasesDone.split(',') : []);
  done.add(phase);
  let row = { ...inv, phasesDone: [...done].join(',') };
  const next = (p: string, note = '') => { scheduleJob(tx, invId, p, note, 100_000n); row = { ...row, phase: p }; };
  const base = phase.split(':')[0];

  const group = parallelGroup(inv.kind);
  const wallChange = inv.kind === 'structural_change';
  if (base === 'plan') { for (const g of group) next(g); row.phase = 'evidence'; }
  else if (group.includes(base)) {
    if (group.every(p => done.has(p)) && !done.has('group-done')) { done.add('group-done'); row.phasesDone = [...done].join(','); next(wallChange ? 'propose' : 'hypothesize'); }
  }
  else if (base === 'hypothesize') next('simulate');
  else if (base === 'simulate') next('propose');
  else if (base === 'propose') next(wallChange ? 'critique' : 'compare');
  else if (base === 'compare') next('critique');
  else if (base === 'critique') {
    const rounds = inv.critiqueRounds + 1;
    row.critiqueRounds = rounds;
    const last = [...tx.db.critique.iter()].filter(c => c.invId === invId).sort((x, y) => (x.id < y.id ? 1 : -1))[0];
    if (last?.verdict === 'revise' && rounds <= 1 && inv.pending) {
      for (const agent of inv.pending.split(',')) next(`revise:${agent}`, `${agent}|${last.text}`);
      row.phase = 'revise';
    } else next('decide');
  }
  else if (base === 'revise') {
    const left = inv.pending.split(',').filter(a => a && a !== phase.split(':')[1]);
    row.pending = left.join(',');
    if (!left.length) next('critique');
  }
  else if (base === 'decide') next('narrate');
  else if (base === 'narrate') {
    row.phase = 'done'; row.status = 'awaiting_approval';
    if (inv.mode === 'nemotron') {
      const secs = Number(tx.timestamp.microsSinceUnixEpoch - inv.createdAt.microsSinceUnixEpoch) / 1e6;
      const calls = [...tx.db.llmCall.iter()].filter(l => l.invId === invId).length;
      setMetric(tx, `agents.ttd.${inv.kind}`, `Nemotron team: question → decision (${inv.kind === 'structural_change' ? 'wall change' : 'risk investigation'})`, secs, 's', `${calls} model calls · 8 agents · last run`, 'agents');
    }
    if (![...tx.db.sceneCommand.iter()].some(c => c.payload.includes('guide_trail'))) emit(tx, { kind: 'guide_trail', target: 'CH-2', forRole: 'technician' });
  }
  tx.db.investigation.id.update(row);
}

export const agentWorker = spacetimedb.procedure({ onSchedule: agentJob }, { job: agentJob.rowType }, t.unit(), (ctx, { job }) => {
  const state = ctx.withTx(tx => {
    const latest = [...tx.db.investigation.iter()].sort((a, b) => (a.id < b.id ? 1 : -1))[0];
    return latest && latest.id === job.invId ? cfgOf(tx) : 'stale';
  });
  if (state === 'stale') return {};
  const base = job.phase.split(':')[0];
  let ok = false;
  if (state) {
    ok = runPhaseV2(ctx, state, job.invId, job.phase, job.note);
    // spec: on a failed call the phase gets one more try, then the deterministic path is used
    if (!ok && !['revise', 'simulate', 'compare'].includes(base)) ok = runPhaseV2(ctx, state, job.invId, job.phase, job.note);
  }
  if (!ok) runFallback(ctx, job.invId, base);
  // the critique fallback approves; the critic must always leave a verdict
  ctx.withTx(tx => {
    if (base === 'narrate' && [...tx.db.answer.iter()].filter(a => a.invId === job.invId).length < 2) {
      const inv = tx.db.investigation.id.find(job.invId);
      if (inv) { if (inv.kind === 'structural_change') runWallOp(tx, inv.id, 11); else runOp(tx, inv, OPS[20], 20); }
    }
    if (['signal', 'historian', 'lease', 'structural'].includes(base)) subtaskDone(tx, job.invId, base);
    // wall changes: the narrator's fallback marks the wall; make sure the state is final
    const inv2 = tx.db.investigation.id.find(job.invId);
    if (base === 'decide' && inv2?.kind === 'structural_change') {
      const w = tx.db.wall.id.find(inv2.subject);
      if (w && w.proposalState === 'evaluating') tx.db.wall.id.update({ ...w, proposalState: removalOk(assessRemoval(tx, w.id).verdict) ? 'recommended' : 'not_recommended' });
    }
    advance(tx, job.invId, job.phase);
    if (base === 'narrate') gradeInvestigation(tx, job.invId);
  });
  return {};
});

// ================================================================ reasoning pipeline v2
// The model reasons; the database gathers, computes and verifies.
//   plan        Planner (large)          1 structured call
//   gather      4 specialists (small)    tools pre-run by the DB; 1 structured call each → findings,
//                                        every number must appear in the data they were given
//   hypothesize Planner (large, think)   1 call; must cite evidence ids; duplicates merged
//   simulate    Sim Engineer             deterministic: the simulation engine is the tool
//   propose     Planner (large)          1 call; only actions the engine says apply
//   compare     Sim Engineer             deterministic re-simulation + ranking
//   critique    Critic (large, think)    1 call over a DB-computed audit; send-backs run as
//                                        deterministic checks, then 1 re-critique
//   decide      Planner (large)          1 structured decision, checked against evidence + engine
//   narrate     Narrator (large)         1 call from a fact sheet → claim verifier (numbers,
//                                        names, polarity) → 1 retry → verified template
// ~10 model calls per investigation instead of ~50, and every spoken number is traceable.

const SOURCE_TABLES = ['asset', 'room', 'work_order', 'tenant', 'past_incident', 'sensor_reading', 'scenario', 'dependency', 'issue', 'wall', 'field_note'];

const POST_FINDINGS: ToolDef = { name: 'post_findings', description: 'Post 2-4 findings from the briefing and a one-sentence summary.', params: { ...W,
  findings: { type: 'array', items: { type: 'object', properties: {
    fact: { type: 'string', description: 'One sentence; every number must come from the briefing' },
    source_table: { type: 'string', enum: SOURCE_TABLES }, source_row: { type: 'string' },
    confidence: { type: 'number' }, about_asset: { type: 'string', description: 'asset or wall id the finding is about' },
    supports: { type: 'boolean', description: 'true if it points to a real problem' } },
    required: ['fact', 'source_table', 'source_row', 'confidence', 'about_asset', 'supports'] } },
  summary: { type: 'string' } }, required: ['why', 'findings', 'summary'] };

const POST_HYPOTHESES: ToolDef = { name: 'post_hypotheses', description: 'Up to 3 distinct root causes, each citing evidence ids.', params: { ...W,
  hypotheses: { type: 'array', items: { type: 'object', properties: {
    asset_id: { type: 'string', description: 'asset id, or SENSOR for a faulty sensor' }, mechanism: { type: 'string' },
    supporting_evidence_ids: { type: 'array', items: { type: 'integer' } }, contradicting_evidence_ids: { type: 'array', items: { type: 'integer' } } },
    required: ['asset_id', 'mechanism', 'supporting_evidence_ids'] } } }, required: ['why', 'hypotheses'] };

const POST_ACTIONS = (kinds: string[]): ToolDef => ({ name: 'post_actions', description: 'Propose the actions that address the problem, most important first.', params: { ...W,
  actions: { type: 'array', items: { type: 'object', properties: { kind: { type: 'string', enum: kinds }, rationale: { type: 'string' } }, required: ['kind', 'rationale'] } } },
  required: ['why', 'actions'] });

const WRITE_DECISION_V2: ToolDef = { name: 'write_decision', description: 'The final decision.', params: { ...W,
  root_cause_hypothesis_id: { type: 'integer' }, ranked_actions: { type: 'array', items: { type: 'string' } },
  confidence: { type: 'number', description: '0..1' }, summary: { type: 'string', description: '2-3 sentences, numbers only from the evidence' },
  what_would_change_my_mind: { type: 'string' } }, required: ['why', 'summary'] };

const POST_ANSWERS: ToolDef = { name: 'post_answers', description: 'Spoken answers, at most 3 short sentences each, plain English, no markdown.', params: { ...W,
  technician: { type: 'string', description: 'what is wrong, where to go, what to check first' },
  manager: { type: 'string', description: 'the risk, the cost if ignored, the recommended action and its cost; ask to approve' } },
  required: ['why', 'technician', 'manager'] };

// ---- verification helpers

function numbersIn(text: string): number[] {
  const out: number[] = [];
  for (const m of text.replace(/[A-Z]{1,4}-\d+[A-Z]?/g, ' ').matchAll(/(\d[\d,]*\.?\d*)\s*([kK]\b|%)?/g)) {
    const v = parseFloat(m[1].replace(/,/g, ''));
    if (isFinite(v)) out.push(m[2] === 'k' || m[2] === 'K' ? v * 1000 : v);
  }
  return out;
}

/** A number in prose is grounded if it matches a number in the facts (rounding, k, % and per-hour forms allowed). */
function groundedNumber(n: number, facts: number[]): boolean {
  if (Number.isInteger(n) && n <= 12) return true; // small counts: "2 tickets", "3 sentences"
  return facts.some(a => {
    const tol = Math.max(0.051, Math.abs(a) * 0.02);
    return Math.abs(a - n) <= tol || Math.abs(a * 100 - n) <= 0.6 || Math.abs(a / 1000 - n) <= Math.max(0.051, Math.abs(a / 1000) * 0.02) || Math.abs(Math.round(a) - n) < 0.5;
  });
}

function unverified(text: string, facts: number[]): number[] { return numbersIn(text).filter(n => !groundedNumber(n, facts)); }

/** Everything the Narrator may say, with ids. */
function factSheet(tx: Ctx, invId: bigint): string {
  const db = tx.db, inv = db.investigation.id.find(invId)!;
  const lines: string[] = [];
  const dec = [...db.decision.iter()].find(d => d.invId === invId);
  if (dec) lines.push(`DECISION: ${dec.rootCause} (likelihood ${Math.round(dec.likelihood * 100)}%). ${dec.summary}`);
  for (const h of [...db.hypothesis.iter()].filter(h => h.invId === invId)) lines.push(`HYPOTHESIS ${h.id} [${h.assetId}] ${Math.round(h.likelihood * 100)}%: ${h.text}`);
  for (const e of [...db.evidence.iter()].filter(e => e.invId === invId)) lines.push(`EVIDENCE ${e.id} (${e.agent}, ${e.sourceTable}/${e.sourceRow}): ${e.fact}`);
  for (const s of [...db.scenario.iter()]) lines.push(`SCENARIO ${s.label}: outage chance ${Math.round(s.outageChance * 100)}%, first outage ${s.outageHour >= 0 ? clock(s.outageHour) : 'none'}, ${s.downtimeHours.toFixed(1)} h down, impact ${money(s.impact)} (${Math.round(s.impact)})`);
  for (const a of [...db.actionCandidate.iter()].filter(a => a.invId === invId)) lines.push(`ACTION ${a.kind}${a.recommended ? ' (recommended)' : ''}: ${a.title}, cost ${money(a.cost)} (${a.cost})${a.saved ? `, saves ${money(a.saved)}` : ''}`);
  if (inv.kind === 'structural_change') {
    const w = db.wall.id.find(inv.subject)!, r = assessRemoval(tx, w.id);
    lines.push(`WALL ${w.id} ${w.name}: ${w.kind === 'partition' ? 'NON-load-bearing partition' : 'LOAD-BEARING'}, ${w.loadKnm.toFixed(1)} kN/m of ${w.capacityKnm.toFixed(1)} kN/m (${Math.round(w.utilization * 100)}%), inside: ${w.services || 'nothing'}`);
    lines.push(`VERDICT ${r.verdict}${r.beam ? `, beam ${r.beam}` : ''}, total ${money(r.cost)} (${r.cost}), ${r.days} days. ${r.reasons.join(' ')}`);
    lines.push(`ROOMS BESIDE: ${roomsBeside(tx, w).map(x => `${x.name}${x.tenantId ? ` (tenant ${db.tenant.id.find(x.tenantId)?.name})` : ''}`).join(', ')}`);
  } else {
    const f = forecastThreshold(tx, 'CH-2');
    lines.push(`FORECAST CH-2 crosses ${VIB_LIMIT} mm/s at ${clock(f.hours)} (+${f.hours.toFixed(1)} h)`);
    for (const t2 of db.tenant.iter()) lines.push(`TENANT ${t2.name}: $${t2.costPerHour}/h downtime, ${t2.allowedDowntimeHours} h allowed, penalty ${money(t2.penalty)} (${t2.penalty})`);
  }
  return lines.join('\n');
}

/** Problems with an answer: unverified numbers, wrong tenants, structural polarity flips. */
function verifyAnswer(tx: Ctx, invId: bigint, text: string, facts: string): string[] {
  const issues: string[] = [];
  const bad = unverified(text, numbersIn(facts));
  if (bad.length) issues.push(`numbers not in the facts: ${bad.join(', ')}`);
  for (const t2 of tx.db.tenant.iter()) if (text.includes(t2.name) && !facts.includes(t2.name)) issues.push(`${t2.name} is not involved`);
  for (const t2 of tx.db.tenant.iter()) if (new RegExp(`\\b${t2.id}\\b`).test(text)) issues.push(`use the tenant's name, not the id ${t2.id}`);
  const inv = tx.db.investigation.id.find(invId)!;
  if (inv.kind === 'structural_change') {
    const w = tx.db.wall.id.find(inv.subject)!;
    const saysNot = /\b(not|non)[- ]?(load[- ]?bearing|structural)/i.test(text);
    const saysBearing = /load[- ]?bearing/i.test(text) && !saysNot;
    if (w.kind !== 'partition' && saysNot) issues.push('the wall IS load-bearing');
    if (w.kind === 'partition' && saysBearing) issues.push('the wall is NOT load-bearing');
  }
  return issues;
}

/** What the Critic should look at, computed from the blackboard. */
function audit(tx: Ctx, invId: bigint): string {
  const db = tx.db, inv = db.investigation.id.find(invId)!;
  const ev = [...db.evidence.iter()].filter(e => e.invId === invId);
  const links = [...db.hypothesisEvidence.iter()].filter(l => l.invId === invId);
  const out: string[] = [];
  for (const h of [...db.hypothesis.iter()].filter(h => h.invId === invId)) {
    const sup = ev.filter(e => (e.hypothesisId === h.id && e.supports) || links.some(l => l.hypothesisId === h.id && l.evidenceId === e.id && l.supports));
    const con = ev.filter(e => (e.hypothesisId === h.id && !e.supports) || links.some(l => l.hypothesisId === h.id && l.evidenceId === e.id && !l.supports));
    const sources = new Set(sup.map(e => e.sourceTable)).size;
    out.push(`H${h.id} ${h.assetId} ${Math.round(h.likelihood * 100)}%: ${sup.length} supporting from ${sources} independent source types, ${con.length} contradicting${sources < 2 ? '  ← fewer than 2 independent sources' : ''}`);
  }
  const acts = [...db.actionCandidate.iter()].filter(a => a.invId === invId);
  const simulated = [...db.scenario.iter()].some(s => s.label === 'with_plan');
  if (inv.kind !== 'structural_change') out.push(`ACTIONS: ${acts.map(a => `${a.kind}${a.rank ? ` #${a.rank}` : ''}`).join(', ')}; ${simulated ? 'simulated against doing nothing' : 'NOT simulated yet'}`);
  const assumptions: string[] = [];
  const has = (re: RegExp) => ev.some(e => re.test(e.fact));
  if (inv.kind !== 'structural_change') {
    if (acts.some(a => a.kind === 'work_order' || a.kind === 'load_shift') && !has(/Chiller 1 has [\d.]+ tons spare/i)) assumptions.push('the plan takes Chiller 2 offline; nobody has verified Chiller 1 can carry the server room meanwhile (simulation)');
    if (!has(/sensor (looks|is) healthy|smooth trend|sibling/i)) assumptions.push('a faulty vibration sensor has not been ruled out (signal)');
  } else {
    const w = db.wall.id.find(inv.subject)!;
    if (w.services && !has(/inside the wall/i)) assumptions.push(`nobody has checked what runs inside ${w.id} (structural)`);
    if (w.kind !== 'partition' && !has(/shor/i)) assumptions.push('who carries the load while the wall is out has not been addressed (structural)');
  }
  out.push(assumptions.length ? `UNVERIFIED ASSUMPTIONS:\n- ${assumptions.join('\n- ')}` : 'UNVERIFIED ASSUMPTIONS: none');
  return out.join('\n');
}

// ---- structured single call

function structured(ctx: PCtx, cfg: AiCfg, invId: bigint, agent: string, large: boolean, think: boolean, system: string, user: string, tool: ToolDef): Json | null {
  const model = large ? cfg.large : cfg.small;
  const thinking = ctx.withTx(tx => logStep(tx, invId, agent, 'thinking', think ? 'Reasoning…' : 'Working…', '', '', 'thinking', model));
  let out: ChatOut;
  try {
    out = chat(ctx, cfg, model, [{ role: 'system', content: `${thinkPrefix(model, think)}\n${system}` }, { role: 'user', content: user }], [tool], think, { invId, agent }, tool.name);
  } catch (e) {
    ctx.withTx(tx => { const r = tx.db.agentStep.id.find(thinking.id); if (r) tx.db.agentStep.id.update({ ...r, tool: 'nemotron', why: 'Model call failed; falling back', result: String(e).slice(0, 200), status: 'error' }); });
    return null;
  }
  ctx.withTx(tx => {
    const r = tx.db.agentStep.id.find(thinking.id);
    if (!r) return;
    const why = (out.reasoning || '').replace(/\s+/g, ' ').trim();
    if (why) tx.db.agentStep.id.update({ ...r, tool: 'reasoning', why: why.slice(0, 120), result: why.slice(120, 340), status: 'done' });
    else tx.db.agentStep.id.delete(r.id);
  });
  const call = out.calls.find(c => c.name === tool.name) ?? out.calls[0];
  if (call) return call.args;
  try { const m = out.content.match(/\{[\s\S]*\}/); if (m) return JSON.parse(m[0]) as Json; } catch { /* ignore */ }
  return null;
}

function ctxLine(tx: Ctx, invId: bigint) {
  const inv = tx.db.investigation.id.find(invId)!;
  return { inv, header: `${PREAMBLE({ id: inv.id, trigger: inv.trigger }, [...tx.db.asset.iter()].map(x => x.id).join(', '), [...tx.db.room.iter()].map(x => x.id).join(', '), 4)}\n${inv.kind === 'structural_change' ? `INVESTIGATION TYPE: proposed structural change, remove wall ${inv.subject}.` : 'INVESTIGATION TYPE: operational risk.'}` };
}

const logTool = (tx: Ctx, invId: bigint, agent: string, tool: string, why: string, line: string, target: string, model: string, ok = true, err = '') => {
  logStep(tx, invId, agent, tool, why, ok ? line : `rejected: ${err}`, target, ok ? 'done' : 'error', model);
  tx.db.toolCall.insert({ id: 0n, invId, agent, tool, ok, error: err.slice(0, 200), model, ms: 0 });
};

// ---- phases

function v2Plan(ctx: PCtx, cfg: AiCfg, invId: bigint): boolean {
  const pre = ctx.withTx(tx => {
    const { inv, header } = ctxLine(tx, invId);
    const s = execTool(tx, invId, 'planner', { id: 'b', name: 'get_building_summary', args: {} });
    logTool(tx, invId, 'planner', 'get_building_summary', 'Learning the floor, assets and tenants', s.line, '', cfg.large);
    return { header, kind: inv.kind, trigger: inv.trigger, summary: JSON.stringify(s.out).slice(0, 2500) };
  });
  const args = structured(ctx, cfg, invId, 'planner', true, false,
    `${pre.header}\n\n${ROLE_PROMPTS.plan}`,
    `QUESTION: "${pre.trigger}"\nBUILDING: ${pre.summary}\nCall create_subtasks now.`, TOOLS.create_subtasks);
  if (!args) return false;
  return ctx.withTx(tx => {
    try { const r = execTool(tx, invId, 'planner', { id: 'c', name: 'create_subtasks', args }); logTool(tx, invId, 'planner', 'create_subtasks', str(args.why) || 'Splitting the work across the team', r.line, '', cfg.large); }
    catch (e) { logTool(tx, invId, 'planner', 'create_subtasks', 'Splitting the work', '', '', cfg.large, false, String(e)); }
    return [...tx.db.subtask.iter()].some(s => s.invId === invId);
  });
}

function v2Specialist(ctx: PCtx, cfg: AiCfg, invId: bigint, role: string, extra = ''): boolean {
  const pre = ctx.withTx(tx => {
    const { inv, header } = ctxLine(tx, invId);
    const sub = [...tx.db.subtask.iter()].find(s => s.invId === invId && s.agent === role && s.status !== 'done');
    if (sub && (sub.status === 'ready' || sub.status === 'reopened')) tx.db.subtask.id.update({ ...sub, status: 'claimed' });
    const rp = role === 'structural' ? ROLE_PROMPTS.structural : ROLE_PROMPTS[role] ?? '';
    return { header, rp, goal: sub?.goal ?? inv.trigger, briefing: brief(tx, invId, role, inv.kind, inv.subject, cfg.small) };
  });
  const facts = numbersIn(pre.briefing);
  let feedback = '';
  for (let attempt = 0; attempt < 2; attempt++) {
    const args = structured(ctx, cfg, invId, role, false, false, `${pre.header}\n\n${pre.rp}`,
      `YOUR SUBTASK: ${pre.goal}${extra ? `\nTHE CRITIC ASKED: ${extra}` : ''}\nBRIEFING (already read from the database for you):\n${pre.briefing}\n${feedback}Call post_findings with 2-4 findings. Cite source_table/source_row from the briefing. Use only numbers that appear in the briefing.`, POST_FINDINGS);
    if (!args) return false;
    const rejected: string[] = [];
    const accepted = ctx.withTx(tx => {
      let n = 0;
      for (const fRaw of arr(args.findings).slice(0, 4)) {
        const f = (fRaw ?? {}) as Json;
        const fact = str(f.fact).slice(0, 240);
        const bad = unverified(fact, facts);
        if (bad.length) { rejected.push(`"${fact.slice(0, 60)}…" has numbers not in the briefing: ${bad.join(', ')}`); logTool(tx, invId, role, 'post_evidence', 'Posting a finding', '', '', cfg.small, false, `unverified number ${bad.join(', ')}`); continue; }
        try {
          postEvidence(tx, invId, role, fact, str(f.source_table), str(f.source_row), Math.max(0, Math.min(1, num(f.confidence, 0.6))), str(f.about_asset), f.supports !== false);
          logTool(tx, invId, role, 'post_evidence', 'Posting a finding', fact, str(f.about_asset), cfg.small);
          n++;
        } catch (e) { const m = String(e instanceof Error ? e.message : e); rejected.push(m); logTool(tx, invId, role, 'post_evidence', 'Posting a finding', '', '', cfg.small, false, m); }
      }
      if (n > 0) { subtaskDone(tx, invId, role); logTool(tx, invId, role, 'complete_subtask', 'Done', str(args.summary), '', cfg.small); }
      return n;
    });
    if (accepted > 0) return true;
    feedback = `YOUR PREVIOUS FINDINGS WERE REJECTED: ${rejected.join(' | ')}\n`;
  }
  return false;
}

function v2Hypothesize(ctx: PCtx, cfg: AiCfg, invId: bigint): boolean {
  const pre = ctx.withTx(tx => {
    const { header } = ctxLine(tx, invId);
    const ev = [...tx.db.evidence.iter()].filter(e => e.invId === invId).map(e => `E${e.id} [${e.agent}] (${e.supports ? 'problem' : 'reassuring'}, about ${e.aboutAsset}): ${e.fact}`).join('\n');
    return { header, ev };
  });
  const args = structured(ctx, cfg, invId, 'planner', true, true, `${pre.header}\n\n${ROLE_PROMPTS.hypothesize}\nHypotheses must be DISTINCT root causes (different assets or SENSOR). Cite evidence by number.`,
    `EVIDENCE ON THE BLACKBOARD:\n${pre.ev}\nCall post_hypotheses.`, POST_HYPOTHESES);
  if (!args) return false;
  return ctx.withTx(tx => {
    const seenAssets = new Set<string>();
    for (const hRaw of arr(args.hypotheses).slice(0, 3)) {
      const h = (hRaw ?? {}) as Json;
      const asset = str(h.asset_id);
      if (seenAssets.has(asset)) { logTool(tx, invId, 'planner', 'post_hypothesis', 'Naming a root cause', '', asset, cfg.large, false, `duplicate of an existing ${asset} hypothesis (merged)`); continue; }
      if (!arr(h.supporting_evidence_ids).length && asset !== 'SENSOR') { logTool(tx, invId, 'planner', 'post_hypothesis', 'Naming a root cause', '', asset, cfg.large, false, 'cites no evidence'); continue; }
      try {
        const r = execTool(tx, invId, 'planner', { id: 'h', name: 'post_hypothesis', args: { asset_id: asset, mechanism: h.mechanism, supporting_evidence_ids: h.supporting_evidence_ids, contradicting_evidence_ids: h.contradicting_evidence_ids } });
        logTool(tx, invId, 'planner', 'post_hypothesis', 'Naming a root cause', r.line, asset, cfg.large);
        seenAssets.add(asset);
      } catch (e) { logTool(tx, invId, 'planner', 'post_hypothesis', 'Naming a root cause', '', asset, cfg.large, false, String(e instanceof Error ? e.message : e)); }
    }
    return seenAssets.size > 0;
  });
}

/** Deterministic phases: the engine is the agent. */
function v2Deterministic(ctx: PCtx, invId: bigint, phase: 'simulate' | 'compare'): boolean {
  return ctx.withTx(tx => {
    const inv = tx.db.investigation.id.find(invId)!;
    if (inv.kind === 'structural_change') return true;
    if (phase === 'simulate') {
      claim(tx, invId, 'simulation');
      const a = execTool(tx, invId, 'simulation', { id: 's', name: 'simulate_cascade', args: { actions: [] } });
      logTool(tx, invId, 'simulation', 'simulate_cascade', 'What happens tonight if we do nothing?', a.line, 'SRV300', 'engine');
      const b = execTool(tx, invId, 'simulation', { id: 'r', name: 'run_risk_estimate', args: { actions: [] } });
      logTool(tx, invId, 'simulation', 'run_risk_estimate', 'How likely is the outage? 50 futures', b.line, 'SRV300', 'engine');
      const o = a.out as Json, r = b.out as Json;
      postEvidence(tx, invId, 'simulation', `Do nothing: Chiller 2 fails ${str(o.chiller2_fails_at)}; Server Room 300 down at ${str(o.server_room_down_at)}, ${num(o.downtime_hours).toFixed(1)} h down, ${money(num(o.impact_usd))}; outage in ${Math.round(num(r.outage_chance) * 100)}% of 50 runs.`,
        'scenario', 'do_nothing', 0.85, 'CH-2', true);
      subtaskDone(tx, invId, 'simulation');
      return true;
    }
    const c = execTool(tx, invId, 'simulation', { id: 'c', name: 'compare_scenarios', args: {} });
    logTool(tx, invId, 'simulation', 'compare_scenarios', 'Testing every fix in the simulation', c.line, 'SRV300', 'engine');
    return true;
  });
}

function v2Propose(ctx: PCtx, cfg: AiCfg, invId: bigint): boolean {
  const pre = ctx.withTx(tx => {
    const { inv, header } = ctxLine(tx, invId);
    const kinds = inv.kind === 'structural_change'
      ? [...assessRemoval(tx, inv.subject).parts.map(p => p.kind), ...(tx.db.wall.id.find(inv.subject)?.health === 'damaged' ? ['structural_repair'] : [])]
      : Object.keys(ACTION_KINDS);
    const priced = kinds.map(k => {
      if (inv.kind === 'structural_change') { const p = assessRemoval(tx, inv.subject).parts.find(x => x.kind === k); return `${k}: ${p ? `${p.title}, ${money(p.cost)}` : 'Isolate Chiller 2 and inject the cracks, $6.5k'}`; }
      return `${k}: ${ACTION_KINDS[k].title}, ${money(ACTION_KINDS[k].cost)}`;
    }).join('\n');
    const ev = [...tx.db.evidence.iter()].filter(e => e.invId === invId).map(e => `E${e.id}: ${e.fact}`).join('\n');
    const hy = [...tx.db.hypothesis.iter()].filter(h => h.invId === invId).map(h => `H${h.id} ${h.assetId} ${Math.round(h.likelihood * 100)}%: ${h.text}`).join('\n');
    return { header, kinds, priced, ev, hy };
  });
  const args = structured(ctx, cfg, invId, 'planner', true, false, `${pre.header}\n\n${ROLE_PROMPTS.propose}`,
    `HYPOTHESES:\n${pre.hy || '(none)'}\nEVIDENCE:\n${pre.ev}\nACTIONS THAT APPLY (price list from the engine):\n${pre.priced}\nCall post_actions with the actions that address the problem.`, POST_ACTIONS(pre.kinds));
  if (!args) return false;
  return ctx.withTx(tx => {
    let n = 0;
    for (const aRaw of arr(args.actions).slice(0, 6)) {
      const a = (aRaw ?? {}) as Json;
      try { const r = execTool(tx, invId, 'planner', { id: 'a', name: 'post_action_candidate', args: { kind: a.kind, rationale: a.rationale } }); logTool(tx, invId, 'planner', 'post_action_candidate', 'Proposing a fix', r.line, '', cfg.large); n++; }
      catch (e) { logTool(tx, invId, 'planner', 'post_action_candidate', 'Proposing a fix', '', '', cfg.large, false, String(e instanceof Error ? e.message : e)); }
    }
    return n > 0;
  });
}

function v2Critique(ctx: PCtx, cfg: AiCfg, invId: bigint): boolean {
  const pre = ctx.withTx(tx => { claim(tx, invId, 'critic'); const { header } = ctxLine(tx, invId); return { header, audit: audit(tx, invId), rounds: tx.db.investigation.id.find(invId)!.critiqueRounds }; });
  const args = structured(ctx, cfg, invId, 'critic', true, true, `${pre.header}\n\n${ROLE_PROMPTS.critique}`,
    `AUDIT OF THE BLACKBOARD (computed by the database):\n${pre.audit}\n${pre.rounds > 0 ? 'This is the re-check after your requests were answered.\n' : ''}Call post_critique. Send work back (revise) only for a real gap in the audit; name the agent_type that should close it.`, TOOLS.post_critique);
  if (!args) return false;
  return ctx.withTx(tx => {
    try { const r = execTool(tx, invId, 'critic', { id: 'k', name: 'post_critique', args }); logTool(tx, invId, 'critic', 'post_critique', str(args.why) || 'Checking the plan for gaps', r.line, '', cfg.large); }
    catch (e) { logTool(tx, invId, 'critic', 'post_critique', 'Checking the plan', '', '', cfg.large, false, String(e)); return false; }
    const last = [...tx.db.agentStep.iter()].filter(s => s.invId === invId && s.tool === 'post_critique').sort((a, b) => b.seq - a.seq)[0];
    if (last && last.result.startsWith('REVISE')) tx.db.agentStep.id.update({ ...last, status: 'revise' });
    return true;
  });
}

/** Send-backs: the database runs the check the Critic asked for (fast, exact); unknown asks go back to the specialist. */
function v2Revise(ctx: PCtx, cfg: AiCfg, invId: bigint, agent: string, request: string): boolean {
  const q = request.toLowerCase();
  const done = ctx.withTx(tx => {
    const inv = tx.db.investigation.id.find(invId)!;
    if (inv.kind !== 'structural_change' && /chiller 1|ch-1|spare|capacity|carry/.test(q)) { const out = runOp(tx, inv, OPS[17], 17); logTool(tx, invId, 'simulation', 'simulate_cascade', 'Can Chiller 1 carry the server room during repair?', out.result, 'CH-1', 'engine'); return true; }
    if (inv.kind !== 'structural_change' && /sensor|calibrat|faulty|drift/.test(q)) { const out = runOp(tx, inv, OPS[11], 11); logTool(tx, invId, 'signal', 'check_sensor_health', 'Could the sensor be wrong?', out.result, 'CH-2', 'engine'); return true; }
    if (inv.kind === 'structural_change' && /inside|service|duct|pipe|conduit|sprinkler|shor|load path|carry/.test(q)) { const out = runWallOp(tx, invId, 7); logTool(tx, invId, 'structural', 'check_wall_services', 'Answering the Critic', out.result, '', 'engine'); return true; }
    return false;
  });
  return done || v2Specialist(ctx, cfg, invId, agent, request);
}

function v2Decide(ctx: PCtx, cfg: AiCfg, invId: bigint): boolean {
  const pre = ctx.withTx(tx => { const { header, inv } = ctxLine(tx, invId); return { header, facts: factSheet(tx, invId), kind: inv.kind, subject: inv.subject }; });
  const args = structured(ctx, cfg, invId, 'planner', true, false, `${pre.header}\n\n${ROLE_PROMPTS.decide}`,
    `FACTS:\n${pre.facts}\nCall write_decision. root_cause_hypothesis_id must be one of the HYPOTHESIS ids above (for a wall change, omit it). Only use numbers from the facts.`, WRITE_DECISION_V2);
  if (!args) return false;
  return ctx.withTx(tx => {
    const hs = [...tx.db.hypothesis.iter()].filter(h => h.invId === invId).sort((a, b) => b.likelihood - a.likelihood);
    let h = hs.find(x => Number(x.id) === Math.floor(num(args.root_cause_hypothesis_id, -1)));
    let note = '';
    if (pre.kind !== 'structural_change' && hs.length && (!h || h.likelihood + 0.05 < hs[0].likelihood)) { note = ' (the evidence favors the top hypothesis; corrected)'; h = hs[0]; }
    let summary = str(args.summary).slice(0, 500);
    const bad = unverified(summary, numbersIn(pre.facts));
    if (bad.length) { logTool(tx, invId, 'planner', 'write_decision', 'Checking the decision', '', '', cfg.large, false, `unverified numbers ${bad.join(', ')}; using the engine's numbers`); summary = ''; }
    let root = h?.text ?? '';
    if (pre.kind === 'structural_change') {
      const r = assessRemoval(tx, pre.subject), w = tx.db.wall.id.find(pre.subject)!;
      root = `Remove ${w.name}: ${r.verdict.replace(/_/g, ' ')}${r.beam ? ` (${r.beam})` : ''}`;
      summary = `${removalOk(r.verdict) ? 'Recommended' : 'Not recommended'}: ${money(r.cost)}, ${r.days} days. ${summary || r.reasons.join(' ')}`;
      tx.db.wall.id.update({ ...w, proposalState: removalOk(r.verdict) ? 'recommended' : 'not_recommended' });
    } else if (!summary) {
      const dn = [...tx.db.scenario.iter()].find(s => s.label === 'do_nothing'), wp = [...tx.db.scenario.iter()].find(s => s.label === 'with_plan');
      summary = `Outage risk ${Math.round((dn?.outageChance ?? 0) * 100)}% → ${Math.round((wp?.outageChance ?? 0) * 100)}%, impact ${money(dn?.impact ?? 0)} → ${money(wp?.impact ?? 0)}.`;
    }
    const conf = Math.max(0, Math.min(1, num(args.confidence, h?.likelihood ?? 0.8)));
    tx.db.decision.insert({ id: 0n, invId, rootCause: root, likelihood: pre.kind === 'structural_change' ? conf : h?.likelihood ?? conf,
      summary: `${summary}${args.what_would_change_my_mind ? ` Would change our mind: ${str(args.what_would_change_my_mind).slice(0, 160)}` : ''}${note}` });
    logTool(tx, invId, 'planner', 'write_decision', str(args.why) || 'Writing the decision', `${root.slice(0, 90)}${note}`, '', cfg.large);
    return true;
  });
}

function v2Narrate(ctx: PCtx, cfg: AiCfg, invId: bigint): boolean {
  const pre = ctx.withTx(tx => { const { header } = ctxLine(tx, invId); return { header, facts: factSheet(tx, invId) }; });
  let feedback = '';
  for (let attempt = 0; attempt < 2; attempt++) {
    const args = structured(ctx, cfg, invId, 'narrator', true, false, `${pre.header}\n\n${ROLE_PROMPTS.narrate}\nSpeak like a calm colleague. Use names (Chiller 2, Server Room 300, Northwind Analytics), never raw ids. Every number must come from the FACTS.`,
      `FACTS:\n${pre.facts}\n${feedback}Call post_answers.`, POST_ANSWERS);
    if (!args) return false;
    const result = ctx.withTx(tx => {
      const issues: string[] = [];
      for (const role of ['technician', 'manager'] as const) {
        const text = str(args[role]).replace(/\*\*/g, '').trim();
        const problems = text ? verifyAnswer(tx, invId, text, pre.facts) : ['empty'];
        if (problems.length) { issues.push(`${role}: ${problems.join('; ')}`); logTool(tx, invId, 'narrator', 'verify_claims', `Fact-checking the ${role} answer`, '', '', cfg.large, false, problems.join('; ')); continue; }
        for (const old of [...tx.db.answer.iter()]) if (old.invId === invId && old.role === role) tx.db.answer.id.delete(old.id);
        tx.db.answer.insert({ id: 0n, invId, role, text: text.slice(0, 420), byNemotron: true });
        logTool(tx, invId, 'narrator', 'verify_claims', `Fact-checking the ${role} answer`, `every number and name checked against the facts (${numbersIn(text).length} numbers)`, '', cfg.large);
      }
      return issues;
    });
    if (!result.length) break;
    feedback = `YOUR LAST ANSWERS FAILED FACT-CHECKING: ${result.join(' | ')}. Rewrite them using only the FACTS.\n`;
  }
  return ctx.withTx(tx => {
    const inv = tx.db.investigation.id.find(invId)!;
    const missing = ['technician', 'manager'].filter(r => ![...tx.db.answer.iter()].some(a => a.invId === invId && a.role === r));
    if (missing.length) {
      // verified template for any role that failed twice (scripted narrator writes both; keep the verified Nemotron one)
      const keep = [...tx.db.answer.iter()].filter(a => a.invId === invId);
      if (inv.kind === 'structural_change') runWallOp(tx, invId, 11); else runOp(tx, inv, OPS[20], 20);
      for (const a of [...tx.db.answer.iter()]) if (a.invId === invId && keep.some(k => k.role === a.role) && !keep.some(k => k.id === a.id)) tx.db.answer.id.delete(a.id);
    }
    const top = [...tx.db.hypothesis.iter()].filter(h => h.invId === invId).sort((a, b) => b.likelihood - a.likelihood)[0];
    if (inv.kind !== 'structural_change') {
      const target = top && tx.db.asset.id.find(top.assetId) ? top.assetId : 'CH-2';
      emit(tx, { kind: 'guide_trail', target, forRole: 'technician' });
      emit(tx, { kind: 'thread', ids: [target, 'AHU-2', 'SRV300'] });
    }
    return true;
  });
}

/** Self-grading: did the investigation reach the right conclusion, with verified claims? */
function gradeInvestigation(tx: Ctx, invId: bigint) {
  const inv = tx.db.investigation.id.find(invId)!;
  if (inv.mode !== 'nemotron') return;
  const top = [...tx.db.hypothesis.iter()].filter(h => h.invId === invId).sort((a, b) => b.likelihood - a.likelihood)[0];
  const acts = [...tx.db.actionCandidate.iter()].filter(a => a.invId === invId);
  const answers = [...tx.db.answer.iter()].filter(a => a.invId === invId);
  const verified = answers.filter(a => a.byNemotron).length;
  let correct: boolean;
  if (inv.kind === 'structural_change') {
    const r = assessRemoval(tx, inv.subject);
    correct = acts.some(a => a.recommended) === removalOk(r.verdict) || (!removalOk(r.verdict) && acts.every(a => !a.recommended || a.kind === 'structural_repair'));
  } else correct = top?.assetId === 'CH-2' && acts.some(a => a.kind === 'work_order') && acts.some(a => a.kind === 'load_shift');
  const secs = Number(tx.timestamp.microsSinceUnixEpoch - inv.createdAt.microsSinceUnixEpoch) / 1e6;
  const calls = [...tx.db.llmCall.iter()].filter(l => l.invId === invId).length;
  tx.db.evalResult.insert({ id: 0n, runId: `inv-${invId}`, caseId: inv.kind, mode: 'investigation', answer: JSON.stringify({ root: top?.assetId, actions: acts.map(a => a.kind), verified }),
    correct, latencyMs: secs * 1000, toolCalls: calls, grounded: verified === 2 });
  const rows = [...tx.db.evalResult.iter()].filter(r => r.mode === 'investigation');
  const ok = rows.filter(r => r.correct).length;
  const secsAll = rows.map(r => r.latencyMs / 1000).sort((a, b) => a - b);
  setMetric(tx, 'eval.investigation.accuracy', 'Nemotron team: correct conclusion, end to end', ok / rows.length, 'ratio', `${ok}/${rows.length} investigations (root cause + plan, or wall verdict)`, 'reasoning');
  setMetric(tx, 'eval.investigation.verified', 'Spoken answers passing fact-check (numbers, names, polarity)', rows.filter(r => r.grounded).length / rows.length, 'ratio', `${rows.filter(r => r.grounded).length}/${rows.length} investigations, both roles`, 'grounding');
  setMetric(tx, 'eval.investigation.ttd_p50', 'Nemotron team: question → decision (median)', secsAll[Math.floor(secsAll.length / 2)], 's', `${calls} model calls in the last run`, 'agents');
}

/** Run one phase of the v2 pipeline. */
function runPhaseV2(ctx: PCtx, cfg: AiCfg, invId: bigint, phase: string, note: string): boolean {
  const base = phase.split(':')[0];
  switch (base) {
    case 'plan': return v2Plan(ctx, cfg, invId);
    case 'signal': case 'historian': case 'lease': case 'structural': return v2Specialist(ctx, cfg, invId, base);
    case 'hypothesize': return v2Hypothesize(ctx, cfg, invId);
    case 'simulate': return v2Deterministic(ctx, invId, 'simulate');
    case 'propose': return v2Propose(ctx, cfg, invId);
    case 'compare': return v2Deterministic(ctx, invId, 'compare');
    case 'critique': return v2Critique(ctx, cfg, invId);
    case 'revise': return v2Revise(ctx, cfg, invId, note.split('|')[0], note.split('|').slice(1).join('|'));
    case 'decide': return v2Decide(ctx, cfg, invId);
    case 'narrate': return v2Narrate(ctx, cfg, invId);
  }
  return false;
}

// ---------------------------------------------------------------- key + model check

const MODEL_SKIP = /embed|rerank|reward|safety|guard|parse|ocr|retriev|content|audio|asr|tts|nemoguard|clip|math|code/i;

function pickModels(ids: string[]): { large: string; small: string } {
  const nem = ids.filter(id => /nemotron/i.test(id) && !MODEL_SKIP.test(id)).sort().reverse();
  const by = (re: RegExp) => nem.find(id => re.test(id)) ?? '';
  const large = by(/nemotron-3\.5.*super/i) || by(/nemotron-3.*super|nemotron-3.*ultra/i) || by(/super-49b-v1\.5/) || by(/super/i) || by(/ultra/i) || nem[0] || '';
  const small = by(/nemotron-3\.5.*nano/i) || by(/nemotron-3.*nano/i) || by(/nemotron-nano-9b-v2|nano-9b/i) || large;
  return { large, small };
}

export const nemotronCheck = spacetimedb.procedure({ onSchedule: nemotronCheckJob }, { job: nemotronCheckJob.rowType }, t.unit(), (ctx, _args) => {
  const s = ctx.withTx(tx => ({ key: secretOf(tx, 'nvidia_api_key'), base: secretOf(tx, 'nvidia_base_url') || DEFAULT_BASE, large: secretOf(tx, 'model_large'), small: secretOf(tx, 'model_small') }));
  const setStatus = (enabled: boolean, large: string, small: string, message: string) => ctx.withTx(tx => {
    const row = { id: 0, enabled, modelLarge: large, modelSmall: small, message };
    if (tx.db.aiStatus.id.find(0)) tx.db.aiStatus.id.update(row); else tx.db.aiStatus.insert(row);
  });
  if (!s.key) { setStatus(false, '', '', 'No NVIDIA key yet: the scripted team is running.'); return {}; }
  try {
    const res = ctx.http.fetch(`${s.base}/models`, { headers: { authorization: `Bearer ${s.key}` }, timeout: TimeDuration.fromMillis(20000) });
    if (res.status !== 200) { setStatus(false, '', '', `Key rejected (HTTP ${res.status}).`); return {}; }
    const ids = arr((JSON.parse(res.text()) as Json).data).map(m => str((m as Json).id));
    const picked = pickModels(ids);
    const large = s.large || picked.large, small = s.small || picked.small;
    if (!large) { setStatus(false, '', '', 'No Nemotron chat model available for this key.'); return {}; }
    const ping = ctx.http.fetch(`${s.base}/chat/completions`, {
      method: 'POST', headers: { 'content-type': 'application/json', authorization: `Bearer ${s.key}` }, timeout: TimeDuration.fromMillis(40000),
      body: JSON.stringify({ model: small, max_tokens: 16, messages: [{ role: 'user', content: 'Reply with OK.' }], ...(/nemotron-3/.test(small) ? { chat_template_kwargs: { enable_thinking: false } } : {}) }),
    });
    if (ping.status !== 200) { setStatus(false, large, small, `Model ${small} failed (HTTP ${ping.status}).`); return {}; }
    setStatus(true, large, small, `Nemotron team online: ${large.split('/').pop()} + ${small.split('/').pop()}`);
  } catch (e) { setStatus(false, '', '', `Check failed: ${String(e).slice(0, 120)}`); }
  return {};
});

/** Owner only: store the NVIDIA key (private table) and verify it. Never readable by clients. */
export const setNvidiaKey = spacetimedb.reducer({ key: t.string() }, (ctx, { key }) => {
  const o = ctx.db.owner.id.find(0);
  if (!o || !o.identity.equals(ctx.sender)) throw new SenderError('only the database owner can set the key');
  const row = { key: 'nvidia_api_key', value: key.trim() };
  if (ctx.db.secret.key.find('nvidia_api_key')) ctx.db.secret.key.update(row); else ctx.db.secret.insert(row);
  ctx.db.nemotronCheckJob.insert({ scheduledId: 0n, scheduledAt: ScheduleAt.time(ctx.timestamp.microsSinceUnixEpoch + 50_000n) });
});

/** Owner only: override the auto-picked models. Empty = auto. */
export const setModels = spacetimedb.reducer({ large: t.string(), small: t.string() }, (ctx, { large, small }) => {
  const o = ctx.db.owner.id.find(0);
  if (!o || !o.identity.equals(ctx.sender)) throw new SenderError('only the database owner can set models');
  for (const [k, v] of [['model_large', large], ['model_small', small]] as const) {
    if (ctx.db.secret.key.find(k)) ctx.db.secret.key.update({ key: k, value: v }); else ctx.db.secret.insert({ key: k, value: v });
  }
  ctx.db.nemotronCheckJob.insert({ scheduledId: 0n, scheduledAt: ScheduleAt.time(ctx.timestamp.microsSinceUnixEpoch + 50_000n) });
});

/** Re-run the model check (e.g. after a network hiccup). */
export const recheckNemotron = spacetimedb.reducer(ctx => {
  ctx.db.nemotronCheckJob.insert({ scheduledId: 0n, scheduledAt: ScheduleAt.time(ctx.timestamp.microsSinceUnixEpoch + 50_000n) });
});


// ================================================================ evaluation harness + metrics
// A ground-truth benchmark (as-built drawings + a structural engineer's answers) graded
// three ways: plan-only baseline, framing-aware solver, and the Nemotron agent end to end.
// Every number lands in the `metric` table (also served at GET /metrics), next to grounding
// stats from the tool/LLM call logs and backend timings measured inside the database.

type Expected = { bearing?: boolean; verdict?: string; beam?: string; supports?: string[] };
const EVAL_CASES: { id: string; question: string; kind: string; subject: string; expected: Expected }[] = [
  { id: 'C01', kind: 'classify', subject: 'W-MECH-E', question: 'Is the Mechanical room east wall load-bearing?', expected: { bearing: true } },
  { id: 'C02', kind: 'classify', subject: 'W-200-COM', question: 'Is the Suite 200 / Commons wall load-bearing?', expected: { bearing: true } },
  { id: 'C03', kind: 'classify', subject: 'W-100-200', question: 'Is the Suite 100 / Suite 200 wall load-bearing?', expected: { bearing: false } },
  { id: 'C04', kind: 'classify', subject: 'W-300-SRV', question: 'Is the Suite 300 / Server Room wall load-bearing?', expected: { bearing: false } },
  { id: 'C05', kind: 'classify', subject: 'W-SPINE-N', question: 'Is the corridor north wall load-bearing?', expected: { bearing: true } },
  { id: 'C06', kind: 'classify', subject: 'W-LOB-100', question: 'Is the Lobby / Suite 100 wall load-bearing?', expected: { bearing: false } },
  { id: 'R01', kind: 'removal', subject: 'W-300-SRV', question: 'Can we remove the Suite 300 / Server Room wall?', expected: { verdict: 'go' } },
  { id: 'R02', kind: 'removal', subject: 'W-100-200', question: 'Can we remove the Suite 100 / Suite 200 wall?', expected: { verdict: 'go' } },
  { id: 'R03', kind: 'removal', subject: 'W-SRV-400', question: 'Can we remove the Server Room / Suite 400 wall?', expected: { verdict: 'not_allowed' } },
  { id: 'R04', kind: 'removal', subject: 'W-200-COM', question: 'Can we remove the Suite 200 / Commons wall, and what beam would it take?', expected: { verdict: 'go_with_beam', beam: 'W14x48' } },
  { id: 'R05', kind: 'removal', subject: 'W-MECH-E', question: 'Can we remove the Mechanical room east wall?', expected: { verdict: 'not_now' } },
  { id: 'R06', kind: 'removal', subject: 'W-SPINE-S', question: 'Can we remove the whole corridor south wall?', expected: { verdict: 'not_feasible' } },
  { id: 'R07', kind: 'removal', subject: 'W-EXT-N', question: 'Can we remove the north exterior wall?', expected: { verdict: 'not_now' } },
  { id: 'L01', kind: 'loadpath', subject: 'RT-1', question: 'Which walls hold up the cooling tower (RT-1)?', expected: { supports: ['W-EXT-W', 'W-MECH-E'] } },
  { id: 'L02', kind: 'loadpath', subject: 'RT-2', question: 'Which walls hold up the rooftop unit over the Commons (RT-2)?', expected: { supports: ['W-200-COM', 'W-EXT-E'] } },
];

function seedEvalCases(ctx: Ctx) {
  clearTable(ctx.db.evalCase.iter(), r => ctx.db.evalCase.id.delete(r.id));
  for (const c of EVAL_CASES) ctx.db.evalCase.insert({ id: c.id, question: c.question, kind: c.kind, subject: c.subject, expected: JSON.stringify(c.expected) });
}

const NAIVE_BEAM = (span: number) => span <= 4 ? 'W8x18' : span <= 6 ? 'W10x26' : span <= 9 ? 'W12x35' : 'W14x48';

/** The answer a reasoner gives for one case. */
function answerCase(ctx: Ctx, c: typeof EVAL_CASES[number], mode: 'plan_only' | 'solver'): Expected {
  const w = ctx.db.wall.id.find(c.subject);
  if (c.kind === 'classify') return { bearing: mode === 'solver' ? w!.kind !== 'partition' : w!.planOnlyBearing };
  if (c.kind === 'removal') {
    if (mode === 'solver') { const a = assessRemoval(ctx, c.subject); return { verdict: a.verdict, beam: a.beam || undefined }; }
    // plan-only: no framing, no code or condition checks
    if (w!.kind === 'exterior') return { verdict: 'not_now' };
    if (!w!.planOnlyBearing) return { verdict: 'go' };
    return { verdict: 'go_with_beam', beam: NAIVE_BEAM(wallLen(w!)) };
  }
  // load path
  if (mode === 'solver') {
    const walls = new Set(loadPath(ctx, c.subject).flat().filter(id => ctx.db.wall.id.find(id)));
    return { supports: [...walls].filter(id => [...ctx.db.elementEdge.fromId.filter(id)].some(e => e.toId === c.subject)).sort() };
  }
  // plan-only: the walls of the room under the load
  const p = ctx.db.element.id.find(c.subject)!;
  const room = [...ctx.db.room.iter()].find(r => p.x0 >= r.x0 && p.x0 <= r.x1 && p.z0 >= r.z0 && p.z0 <= r.z1);
  return { supports: room ? [...ctx.db.wall.iter()].filter(wl => roomsBeside(ctx, wl).some(r => r.id === room.id)).map(wl => wl.id).sort() : [] };
}

function grade(expected: Expected, got: Expected): boolean {
  if (expected.bearing !== undefined && expected.bearing !== got.bearing) return false;
  if (expected.verdict !== undefined && expected.verdict !== got.verdict) return false;
  if (expected.beam !== undefined && expected.beam !== got.beam) return false;
  if (expected.supports !== undefined && JSON.stringify([...expected.supports].sort()) !== JSON.stringify([...(got.supports ?? [])].sort())) return false;
  return true;
}

function setMetric(ctx: Ctx, key: string, label: string, value: number, unit: string, detail: string, group: string) {
  const row = { key, label, value: Math.round(value * 1000) / 1000, unit, detail, group, at: ctx.timestamp };
  if (ctx.db.metric.key.find(key)) ctx.db.metric.key.update(row); else ctx.db.metric.insert(row);
}

/** Grade the deterministic reasoners on every case and record accuracy metrics. */
function runDeterministicEval(ctx: Ctx) {
  const runId = `det-${ctx.timestamp.microsSinceUnixEpoch}`;
  for (const mode of ['plan_only', 'solver'] as const) {
    const byKind = new Map<string, [number, number]>();
    for (const c of EVAL_CASES) {
      const got = answerCase(ctx, c, mode);
      const ok = grade(c.expected, got);
      ctx.db.evalResult.insert({ id: 0n, runId, caseId: c.id, mode, answer: JSON.stringify(got), correct: ok, latencyMs: 0, toolCalls: 0, grounded: true });
      const k = byKind.get(c.kind) ?? [0, 0]; k[0] += ok ? 1 : 0; k[1]++; byKind.set(c.kind, k);
    }
    const all = [...byKind.values()].reduce((s, [a, b]) => [s[0] + a, s[1] + b], [0, 0]);
    const label = mode === 'solver' ? 'Framing-aware reasoning' : 'Plan-only baseline';
    setMetric(ctx, `eval.${mode}.accuracy`, `${label}: benchmark accuracy`, all[0] / all[1], 'ratio', `${all[0]}/${all[1]} cases`, 'reasoning');
    for (const [kind, [a, b]] of byKind) setMetric(ctx, `eval.${mode}.${kind}`, `${label}: ${kind}`, a / b, 'ratio', `${a}/${b}`, 'reasoning');
  }
  // load-bearing classification precision/recall vs as-built, both reasoners
  const truth = new Set(HARBOR_POINT.asBuilt.bearing);
  for (const mode of ['plan_only', 'solver'] as const) {
    let tp = 0, fp = 0, fn = 0, tn = 0;
    for (const w of ctx.db.wall.iter()) {
      const pred = mode === 'solver' ? w.kind !== 'partition' : w.planOnlyBearing;
      const t2 = truth.has(w.id);
      if (pred && t2) tp++; else if (pred && !t2) fp++; else if (!pred && t2) fn++; else tn++;
    }
    const label = mode === 'solver' ? 'Framing-aware' : 'Plan-only';
    setMetric(ctx, `structure.${mode}.recall`, `${label}: load-bearing walls found (recall)`, tp / Math.max(1, tp + fn), 'ratio', `${tp} of ${tp + fn} bearing walls`, 'reasoning');
    setMetric(ctx, `structure.${mode}.precision`, `${label}: load-bearing precision`, tp / Math.max(1, tp + fp), 'ratio', `${fp} false alarms`, 'reasoning');
    setMetric(ctx, `structure.${mode}.accuracy`, `${label}: wall classification accuracy`, (tp + tn) / Math.max(1, tp + tn + fp + fn), 'ratio', `${tp + tn}/${tp + tn + fp + fn} walls`, 'reasoning');
  }
}

/** Grounding and agent stats from the call logs (Nemotron runs) and the blackboard. */
function refreshAgentMetrics(ctx: Ctx) {
  const tools = [...ctx.db.toolCall.iter()];
  const llm = [...ctx.db.llmCall.iter()];
  if (tools.length) {
    const rejected = tools.filter(t2 => !t2.ok).length;
    setMetric(ctx, 'grounding.tool_calls', 'Agent tool calls executed in-database', tools.length, 'calls', `${rejected} rejected by validation`, 'grounding');
    setMetric(ctx, 'grounding.rejection_rate', 'Invalid claims/ids caught before they reached the blackboard', rejected / tools.length, 'ratio', 'unknown ids, missing citations, bad kinds', 'grounding');
  }
  const ev = [...ctx.db.evidence.iter()];
  if (ev.length) setMetric(ctx, 'grounding.cited', 'Evidence rows citing an existing source row', 1, 'ratio', `${ev.length} pieces, all validated at insert`, 'grounding');
  if (llm.length) {
    const ok = llm.filter(l => l.ok);
    const ms = ok.map(l => l.ms).sort((a, b) => a - b);
    const pct = (p: number) => ms.length ? ms[Math.min(ms.length - 1, Math.floor(p * ms.length))] : 0;
    setMetric(ctx, 'agents.llm_calls', 'Nemotron calls', llm.length, 'calls', `${llm.length - ok.length} failed`, 'agents');
    setMetric(ctx, 'agents.llm_p50_ms', 'Nemotron latency p50', pct(0.5), 'ms', `p95 ${Math.round(pct(0.95))} ms`, 'agents');
    setMetric(ctx, 'agents.tokens', 'Tokens used', ok.reduce((s, l) => s + l.promptTokens + l.completionTokens, 0), 'tokens', 'prompt + completion', 'agents');
  }
  const crit = [...ctx.db.critique.iter()];
  if (crit.length) setMetric(ctx, 'agents.critic_sendbacks', 'Critic send-backs', crit.filter(c => c.verdict === 'revise').length, 'count', `${crit.length} critiques`, 'agents');
}

/** Run the deterministic benchmark (plan-only vs framing-aware) and refresh all metrics. */
export const runEval = spacetimedb.reducer(ctx => {
  runDeterministicEval(ctx);
  refreshAgentMetrics(ctx);
});

/** Owner-only: external benchmark results (e.g. client round-trip latency) land here. */
export const recordMetric = spacetimedb.reducer({ key: t.string(), label: t.string(), value: t.f64(), unit: t.string(), detail: t.string(), group: t.string() },
  (ctx, { key, label, value, unit, detail, group }) => {
    const o = ctx.db.owner.id.find(0);
    if (!o || !o.identity.equals(ctx.sender)) throw new SenderError('only the database owner can record metrics');
    setMetric(ctx, key, label, value, unit, detail, group);
  });

/** Round-trip probe for client benchmarks: the client times call → subscription update. */
export const benchPing = spacetimedb.reducer({ seq: t.u32() }, (ctx, { seq }) => {
  ctx.db.benchEcho.insert({ seq, at: ctx.timestamp });
});

/** Backend timings measured inside the database (transaction timestamps around the work). */
export const benchBackend = spacetimedb.procedure(t.unit(), ctx => {
  const now = () => ctx.withTx(tx => tx.timestamp.microsSinceUnixEpoch);
  const t0 = now();
  const sims = ctx.withTx(tx => { const b = loadBuilding(tx); const f = forecastThreshold(tx, 'CH-2'); for (let i = 0; i < 300; i++) simulate(b, NO_PLAN, f.hours, 0.8 + (i % 10) / 20); return 300; });
  const t1 = now();
  ctx.withTx(tx => { const b = loadBuilding(tx); const f = forecastThreshold(tx, 'CH-2'); riskEstimate(tx, b, NO_PLAN, f.hours, failureChance(tx, 'CH-2')); });
  const t2 = now();
  ctx.withTx(tx => { for (const w of [...tx.db.wall.iter()]) assessRemoval(tx, w.id); for (const p of HARBOR_POINT.pointLoads) loadPath(tx, p.id); });
  const t3 = now();
  ctx.withTx(tx => {
    const simMs = Number(t1 - t0) / 1000, riskMs = Number(t2 - t1) / 1000, structMs = Number(t3 - t2) / 1000;
    setMetric(tx, 'backend.sim_per_sec', 'Cascade simulations per second (in-database)', simMs > 0 ? sims / (simMs / 1000) : 0, 'runs/s', `${sims} × 145-tick runs in ${simMs.toFixed(1)} ms`, 'backend');
    setMetric(tx, 'backend.risk_ms', '50-run risk estimate', riskMs, 'ms', 'Monte Carlo inside one transaction', 'backend');
    setMetric(tx, 'backend.struct_ms', 'Structural what-ifs: every wall + load paths', structMs, 'ms', `${tx.db.wall.count()} removals sized + ${HARBOR_POINT.pointLoads.length} load paths`, 'backend');
  });
  return {};
});

// ---- Nemotron agent on the benchmark (end to end, graded)
const ANSWER_TOOL: ToolDef = { name: 'submit_answer', description: 'Submit your final answer for this case.', params: { ...W,
  bearing: { type: 'boolean', description: 'for classification cases' },
  verdict: { type: 'string', enum: ['go', 'go_with_beam', 'not_now', 'not_allowed', 'not_feasible'], description: 'for removal cases' },
  beam: { type: 'string', description: 'steel section if a beam is needed, e.g. W12x35' },
  supports: { type: 'array', items: { type: 'string' }, description: 'wall ids that directly hold up the load, for load-path cases' } }, required: ['why'] };
const EVAL_TOOLS = ['get_structure', 'get_load_path', 'assess_wall_removal', 'check_code', 'check_wall_services', 'get_inspection_issues'];

export const agentEval = spacetimedb.procedure({ onSchedule: evalJob }, { job: evalJob.rowType }, t.unit(), (ctx, { job }) => {
  const cfg = ctx.withTx(tx => cfgOf(tx));
  if (!cfg) return {};
  const llmOnly = job.caseIds.startsWith('llm_only');
  const ids = job.caseIds.replace(/^llm_only:?/, '');
  const cases = ids ? EVAL_CASES.filter(c => ids.split(',').includes(c.id)) : EVAL_CASES;
  const runId = `${llmOnly ? 'llm' : 'agent'}-${job.scheduledId}`;
  const mode = llmOnly ? 'llm_only' : 'agent';
  for (const c of cases) {
    const t0 = ctx.withTx(tx => tx.timestamp.microsSinceUnixEpoch);
    const tools = llmOnly ? [ANSWER_TOOL] : [...EVAL_TOOLS.map(n => TOOLS[n]), ANSWER_TOOL];
    const bpText = JSON.stringify({ ...HARBOR_POINT, asBuilt: undefined, rooms: ROOMS.map(r => ({ id: r[0], name: r[1], kind: r[2], x0: r[3], z0: r[4], x1: r[5], z1: r[6] })),
      issues: ISSUES.map(i => ({ id: i[0], kind: i[1], title: i[3], detail: i[4], room: i[6] })) });
    const messages: Json[] = llmOnly ? [
      { role: 'system', content: `${thinkPrefix(cfg.large, true)}\nYou are a structural engineer. Use the blueprint below (walls, roof framing bays with span direction and kPa loads, rooftop point loads, rooms, inspection findings). Material capacities in kN/m: Reinforced CMU 40, CMU 30, Steel stud + CMU core 26; cracked walls lose 15%. Beams: choose the lightest of W8x18 (S 249 cm³, I 2580 cm⁴), W10x26 (454, 5080), W12x35 (749, 11900), W14x48 (1150, 20100), W16x57 (1520, 31000), W18x71 (2240, 48300), W21x83 (2810, 70800), W24x104 (4000, 129000) meeting bending (0.66·Fy, Fy=345 MPa) and L/360 deflection (E=200 GPa). Verdicts: go, go_with_beam, not_now (exterior or damaged), not_allowed (code), not_feasible (no single beam works). Call submit_answer exactly once.\nBLUEPRINT: ${bpText}` },
      { role: 'user', content: c.question },
    ] : [
      { role: 'system', content: `${thinkPrefix(cfg.large, true)}\nYou are the Structural Engineer agent of Cascade for "Harbor Point, Floor 3". Answer only from tool results. Call tools, then call submit_answer exactly once. Wall ids: ${HARBOR_POINT.walls.map(w => w.id).join(', ')}. Rooftop loads: RT-1, RT-2.` },
      { role: 'user', content: c.question },
    ];
    let answer: Expected | null = null, calls = 0, grounded = true;
    for (let round = 0; round < 6 && !answer; round++) {
      let out: ChatOut;
      try { out = chat(ctx, cfg, cfg.large, messages, tools, true, { invId: 0n, agent: llmOnly ? 'eval-llm-only' : 'eval' }); } catch { break; }
      if (!out.calls.length) { messages.push({ role: 'assistant', content: out.content || '(none)' }, { role: 'user', content: 'Use the tools, then call submit_answer.' }); continue; }
      messages.push({ role: 'assistant', content: out.content || null, tool_calls: out.calls.map(k => ({ id: k.id, type: 'function', function: { name: k.name, arguments: JSON.stringify(k.args) } })) });
      for (const k of out.calls) {
        calls++;
        if (k.name === 'submit_answer') {
          answer = { bearing: typeof k.args.bearing === 'boolean' ? k.args.bearing : undefined, verdict: str(k.args.verdict) || undefined, beam: str(k.args.beam) || undefined,
            supports: Array.isArray(k.args.supports) ? (k.args.supports as unknown[]).map(x => str(x)) : undefined };
          messages.push({ role: 'tool', tool_call_id: k.id, name: k.name, content: '{"ok":true}' });
          continue;
        }
        let result: unknown, ok = true, err = '';
        if (!EVAL_TOOLS.includes(k.name)) { ok = false; err = `tool ${k.name} not available`; result = { error: err }; }
        else {
          try { result = ctx.withTx(tx => execTool(tx, 0n, 'structural', k).out); }
          catch (e) { ok = false; err = String(e instanceof Error ? e.message : e); result = { error: err }; }
        }
        if (!ok) grounded = false;
        ctx.withTx(tx => tx.db.toolCall.insert({ id: 0n, invId: 0n, agent: 'eval', tool: k.name, ok, error: err.slice(0, 200), model: cfg.large, ms: 0 }));
        messages.push({ role: 'tool', tool_call_id: k.id, name: k.name, content: JSON.stringify(result).slice(0, 3500) });
      }
    }
    const t1 = ctx.withTx(tx => tx.timestamp.microsSinceUnixEpoch);
    const ok = answer ? grade(c.expected, answer) : false;
    ctx.withTx(tx => tx.db.evalResult.insert({ id: 0n, runId, caseId: c.id, mode, answer: JSON.stringify(answer ?? {}), correct: ok,
      latencyMs: Number(t1 - t0) / 1000, toolCalls: calls, grounded }));
  }
  ctx.withTx(tx => {
    const rows = [...tx.db.evalResult.iter()].filter(r => r.runId === runId);
    const correct = rows.filter(r => r.correct).length;
    const lat = rows.map(r => r.latencyMs).sort((a, b) => a - b);
    if (llmOnly) {
      setMetric(tx, 'eval.llm_only.accuracy', 'Same model, no tools (blueprint in prompt)', correct / Math.max(1, rows.length), 'ratio', `${correct}/${rows.length} cases · ${cfg.large.split('/').pop()}`, 'reasoning');
      return;
    }
    setMetric(tx, 'eval.agent.accuracy', 'Nemotron agent: benchmark accuracy', correct / Math.max(1, rows.length), 'ratio', `${correct}/${rows.length} cases · ${cfg.large.split('/').pop()}`, 'reasoning');
    setMetric(tx, 'eval.agent.latency_p50', 'Nemotron agent: time per case (p50)', lat[Math.floor(lat.length / 2)] ?? 0, 'ms', `p95 ${Math.round(lat[Math.min(lat.length - 1, Math.floor(lat.length * 0.95))] ?? 0)} ms`, 'agents');
    setMetric(tx, 'eval.agent.tool_calls', 'Nemotron agent: tool calls per case', rows.reduce((s, r) => s + r.toolCalls, 0) / Math.max(1, rows.length), 'calls', 'including submit_answer', 'agents');
    setMetric(tx, 'eval.agent.grounded', 'Nemotron agent: cases with no rejected tool call', rows.filter(r => r.grounded).length / Math.max(1, rows.length), 'ratio', 'every id it used existed', 'grounding');
    refreshAgentMetrics(tx);
  });
  return {};
});

/** Queue the Nemotron agent on the benchmark (all cases, or a comma list of case ids). */
/** caseIds: "" = all cases; "C01,R04" = some; prefix "llm_only:" = ablation without tools. */
export const runAgentEval = spacetimedb.reducer({ caseIds: t.string() }, (ctx, { caseIds }) => {
  if (!nemotronReady(ctx)) throw new SenderError('set an NVIDIA key first (set_nvidia_key)');
  ctx.db.evalJob.insert({ scheduledId: 0n, scheduledAt: ScheduleAt.time(ctx.timestamp.microsSinceUnixEpoch + 50_000n), caseIds });
});

// ---- HTTP API: the database is the server
const json = (body: unknown, status = 200) => new SyncResponse(JSON.stringify(body, null, 1), { status, headers: { 'content-type': 'application/json', 'access-control-allow-origin': '*' } });

export const httpMetrics = spacetimedb.httpHandler(ctx => json(ctx.withTx(tx => [...tx.db.metric.iter()]
  .sort((a, b) => a.group.localeCompare(b.group) || a.key.localeCompare(b.key))
  .map(m => ({ key: m.key, group: m.group, label: m.label, value: m.value, unit: m.unit, detail: m.detail })))));

export const httpStructure = spacetimedb.httpHandler(ctx => json(ctx.withTx(tx => [...tx.db.wall.iter()].map(w => ({
  id: w.id, name: w.name, kind: w.kind, load_kN_per_m: w.loadKnm, capacity_kN_per_m: w.capacityKnm, utilization: w.utilization, condition: w.health,
  plan_only_guess: w.planOnlyBearing ? 'bearing' : 'partition', carries: w.carries, inside: w.services,
})))));

function tokenOk(ctx: { withTx<T>(f: (tx: Ctx) => T): T }, req: { headers: { get(name: string): string | null } }): boolean {
  const need = ctx.withTx(tx => secretOf(tx, 'ingest_token'));
  return !need || req.headers.get('x-ingest-token') === need;
}

/** IoT ingest: POST {"asset":"CH-2","value":5.4} → live reading (watchdog sees it next tick). */
export const httpSensor = spacetimedb.httpHandler((ctx, req) => {
  if (!tokenOk(ctx, req)) return json({ error: 'bad token' }, 401);
  let body: Json;
  try { body = JSON.parse(req.text()) as Json; } catch { return json({ error: 'body must be JSON' }, 400); }
  const id = str(body.asset), value = num(body.value, NaN);
  if (!isFinite(value)) return json({ error: 'value must be a number' }, 400);
  const ok = ctx.withTx(tx => {
    const a = tx.db.asset.id.find(id);
    if (!a) return false;
    tx.db.asset.id.update({ ...a, reading: value });
    return true;
  });
  return ok ? json({ ok: true, asset: id, value }) : json({ error: `unknown asset '${id}'` }, 404);
});

/** Blueprint ingest: POST a blueprint JSON → knowledge graph + structural analysis rebuilt in one transaction. */
export const httpBlueprint = spacetimedb.httpHandler((ctx, req) => {
  if (!tokenOk(ctx, req)) return json({ error: 'bad token' }, 401);
  let bp: Blueprint;
  try { bp = JSON.parse(req.text()) as Blueprint; } catch { return json({ error: 'body must be a blueprint JSON' }, 400); }
  if (!bp.walls?.length || !bp.bays?.length) return json({ error: 'blueprint needs walls and bays' }, 400);
  const n = ctx.withTx(tx => { ingestBlueprint(tx, bp); runDeterministicEval(tx); return tx.db.element.count(); });
  return json({ ok: true, blueprint: bp.id, elements: Number(n) });
});

export const httpHealth = spacetimedb.httpHandler(() => json({ ok: true, service: 'cascade' }));

export const routes = spacetimedb.httpRouter(new Router()
  .get('/health', httpHealth)
  .get('/metrics', httpMetrics)
  .get('/structure', httpStructure)
  .post('/sensor', httpSensor)
  .post('/blueprint', httpBlueprint));

// ================================================================ live data: replay + watchdog

const WATCHDOG_Z = 6;
const WATCHDOG_AFTER_TICKS = 60; // ~3 min after the twin is built, if nobody asked

export const runReplay = spacetimedb.reducer({ onSchedule: replayTick }, { timer: replayTick.rowType }, ctx => {
  const st = ctx.db.plantState.id.find(0);
  if (!st) return;
  ctx.db.plantState.id.update({ ...st, ticks: st.ticks + 1 });
  const repaired = [...ctx.db.workOrder.iter()].some(w => w.assetId === 'CH-2' && w.createdBy === 'cascade');
  for (const a of [...ctx.db.asset.iter()]) {
    const n = (ctx.random() - 0.5);
    let v = a.reading;
    if (a.id === 'CH-2') v = repaired ? Math.max(a.baseline, v - 0.15) + n * 0.06 : Math.min(5.3, v + 0.001) + n * 0.05;
    else v = a.baseline + n * (a.readingUnit === 'mm/s' ? 0.15 : 1.2);
    ctx.db.asset.id.update({ ...a, reading: Math.round(v * 100) / 100 });
  }
  // Watchdog: opens an investigation by itself when CH-2 is far outside normal
  // and nobody has asked yet.
  const built = ctx.db.timeline.id.find(0)?.built ?? false;
  if (built && st.ticks >= WATCHDOG_AFTER_TICKS && !st.watchdogFired && ctx.db.investigation.count() === 0n) {
    const z = anomalyScore(ctx, 'CH-2').z;
    if (z > WATCHDOG_Z) {
      ctx.db.plantState.id.update({ ...ctx.db.plantState.id.find(0)!, watchdogFired: true });
      openInvestigation(ctx, `Watchdog: Chiller 2 vibration ${z.toFixed(0)}σ above normal`, 'watchdog', 'Cascade watchdog');
    }
  }
});

function startReplay(ctx: Ctx) {
  clearTable(ctx.db.replayTick.iter(), r => ctx.db.replayTick.scheduledId.delete(r.scheduledId));
  ctx.db.replayTick.insert({ scheduledId: 0n, scheduledAt: ScheduleAt.interval(3_000_000n) });
  const st = ctx.db.plantState.id.find(0);
  if (st) ctx.db.plantState.id.update({ ...st, ticks: 0, watchdogFired: false });
  else ctx.db.plantState.insert({ id: 0, ticks: 0, watchdogFired: false });
}

function resetAll(ctx: Ctx) {
  clearTable(ctx.db.fieldNote.iter(), r => ctx.db.fieldNote.id.delete(r.id));
  clearTable(ctx.db.agentTick.iter(), r => ctx.db.agentTick.scheduledId.delete(r.scheduledId));
  clearBlackboard(ctx);
  seedBuilding(ctx);
  clearTable(ctx.db.sceneCommand.iter(), r => ctx.db.sceneCommand.id.delete(r.id));
  setTimeline(ctx, { hour: 0, label: 'do_nothing' });
  startReplay(ctx);
}

// ================================================================ lifecycle

export const init = spacetimedb.init(ctx => {
  resetAll(ctx);
  setTimeline(ctx, { built: false });
  ctx.db.owner.insert({ id: 0, identity: ctx.sender });
  ctx.db.aiStatus.insert({ id: 0, enabled: false, modelLarge: '', modelSmall: '', message: 'No NVIDIA key yet: the scripted team is running.' });
});

export const onConnect = spacetimedb.clientConnected(ctx => {
  const me = ctx.db.user.identity.find(ctx.sender);
  if (me) ctx.db.user.identity.update({ ...me, online: true });
  else ctx.db.user.insert({ identity: ctx.sender, name: 'Guest', role: 'technician', x: 0, z: 0, present: false, online: true });
});

export const onDisconnect = spacetimedb.clientDisconnected(ctx => {
  const me = ctx.db.user.identity.find(ctx.sender);
  if (me) ctx.db.user.identity.update({ ...me, online: false, present: false });
});

// ================================================================ reducers

const ROLES = new Set(['technician', 'manager']);
function me(ctx: Ctx) {
  const u = ctx.db.user.identity.find(ctx.sender);
  if (!u) throw new SenderError('not joined');
  return u;
}

export const join = spacetimedb.reducer({ name: t.string(), role: t.string() }, (ctx, { name, role }) => {
  if (!ROLES.has(role)) throw new SenderError(`unknown role '${role}'`);
  const u = ctx.db.user.identity.find(ctx.sender);
  const row = { identity: ctx.sender, name: name.trim() || 'Guest', role, x: u?.x ?? 0, z: u?.z ?? 0, present: u?.present ?? false, online: true };
  if (u) ctx.db.user.identity.update(row); else ctx.db.user.insert(row);
});

export const setRole = spacetimedb.reducer({ role: t.string() }, (ctx, { role }) => {
  if (!ROLES.has(role)) throw new SenderError(`unknown role '${role}'`);
  ctx.db.user.identity.update({ ...me(ctx), role });
});

export const updatePosition = spacetimedb.reducer({ x: t.f32(), z: t.f32(), present: t.bool() }, (ctx, { x, z, present }) => {
  ctx.db.user.identity.update({ ...me(ctx), x, z, present });
});

export const buildTwin = spacetimedb.reducer(ctx => {
  setTimeline(ctx, { built: true });
  const st = ctx.db.plantState.id.find(0);
  if (st) ctx.db.plantState.id.update({ ...st, ticks: 0 });
});

export const setTime = spacetimedb.reducer({ hour: t.f32(), label: t.string() }, (ctx, { hour, label }) => {
  if (label !== 'do_nothing' && label !== 'with_plan') throw new SenderError(`unknown scenario '${label}'`);
  setTimeline(ctx, { hour: Math.max(0, Math.min(24, hour)), label });
});

export const ask = spacetimedb.reducer({ text: t.string() }, (ctx, { text }) => {
  const q = text.trim();
  if (!q) throw new SenderError('empty question');
  clearTable(ctx.db.agentTick.iter(), r => ctx.db.agentTick.scheduledId.delete(r.scheduledId));
  const wallId = wallFromText(ctx, q);
  if (wallId) {
    for (const other of [...ctx.db.wall.iter()]) if (other.proposal) ctx.db.wall.id.update({ ...other, proposal: '', proposalState: '' });
    ctx.db.wall.id.update({ ...ctx.db.wall.id.find(wallId)!, proposal: 'remove', proposalState: 'evaluating' });
    openInvestigation(ctx, q, 'ask', me(ctx).name, 'structural_change', wallId);
    return;
  }
  openInvestigation(ctx, q, 'ask', me(ctx).name);
});

/** Manager taps a room or asset on the tabletop: a trail appears for the technician. */
export const sendTech = spacetimedb.reducer({ targetId: t.string() }, (ctx, { targetId }) => {
  emit(ctx, { kind: 'guide_trail', target: targetId, forRole: 'technician' });
});

export const approveAction = spacetimedb.reducer({ actionId: t.u64(), approved: t.bool() }, (ctx, { actionId, approved }) => {
  if (me(ctx).role !== 'manager') throw new SenderError('only a manager can approve actions');
  const a = ctx.db.actionCandidate.id.find(actionId);
  if (!a) throw new SenderError('unknown action');
  if (a.status === 'done') return;
  ctx.db.actionCandidate.id.update({ ...a, status: approved ? 'approved' : 'proposed' });
});

/** The approval gate: only approved actions run. Then a follow-up simulation proves the result. */
export const executeAction = spacetimedb.reducer(ctx => {
  const m = me(ctx);
  if (m.role !== 'manager') throw new SenderError('only a manager can execute the plan');
  const inv = [...ctx.db.investigation.iter()][0];
  if (!inv) throw new SenderError('no investigation');
  const approved = [...ctx.db.actionCandidate.iter()].filter(a => a.invId === inv.id && a.status === 'approved');
  if (!approved.length) throw new SenderError('approve at least one action first');

  if (inv.kind === 'structural_change') {
    const w = ctx.db.wall.id.find(inv.subject);
    for (const a of approved) {
      if (a.kind === 'structural_repair') {
        for (const iss of [...ctx.db.issue.iter()]) if (iss.id === 'ISS-2' && iss.status === 'open') ctx.db.issue.id.update({ ...iss, status: 'scheduled' });
      }
      ctx.db.workOrder.insert({ id: `WO-${4600 + Number(ctx.db.workOrder.count())}`, assetId: a.target, title: a.title, status: 'scheduled', ageDays: 0, createdBy: 'cascade', cost: a.cost });
      ctx.db.actionCandidate.id.update({ ...a, status: 'done' });
    }
    const removing = approved.some(a => ['install_beam', 'remove_partition'].includes(a.kind));
    if (w && removing) ctx.db.wall.id.update({ ...w, proposalState: 'approved' });
    ctx.db.agentStep.insert({ id: 0n, invId: inv.id, seq: Number(ctx.db.agentStep.count()) + 100, agent: 'structural', tool: 'execute_action', why: 'Scheduling the approved work',
      result: `${approved.length} work orders · ${money(approved.reduce((s, a) => s + a.cost, 0))}${removing ? ' · wall change approved' : ''}`, target: '', status: 'done', model: '' });
    ctx.db.investigation.id.update({ ...inv, status: 'resolved' });
    return;
  }

  const plan: Plan = { ...NO_PLAN };
  for (const a of approved) {
    if (a.kind === 'work_order') {
      plan.repair = true;
      for (const iss of [...ctx.db.issue.iter()]) if (iss.assetId === 'CH-2' && iss.status === 'open') ctx.db.issue.id.update({ ...iss, status: 'scheduled' });
      ctx.db.workOrder.insert({ id: `WO-${4600 + Number(ctx.db.workOrder.count())}`, assetId: 'CH-2', title: `Planned bearing repair ${clock(PLANNED_START)}–${clock(PLANNED_END)} (closes WO-4471, WO-4502)`, status: 'scheduled', ageDays: 0, createdBy: 'cascade', cost: a.cost });
    } else if (a.kind === 'load_shift') {
      plan.loadShift = true;
      ctx.db.workOrder.insert({ id: `WO-${4600 + Number(ctx.db.workOrder.count())}`, assetId: 'CH-1', title: 'Load shift: Server Room 300 priority on Chiller 1', status: 'scheduled', ageDays: 0, createdBy: 'cascade', cost: a.cost });
    } else if (a.kind === 'tenant_notice') {
      plan.notice = true;
      ctx.db.notice.insert({ id: 0n, tenantId: 'T-NW', text: `Northwind Analytics: planned chiller maintenance today ${clock(PLANNED_START)}–${clock(PLANNED_END)}. Your server room stays cooled by Chiller 1 throughout.` });
    } else if (a.kind === 'structural_repair') {
      for (const iss of [...ctx.db.issue.iter()]) if (iss.id === 'ISS-2' && iss.status === 'open') ctx.db.issue.id.update({ ...iss, status: 'scheduled' });
      const mw = ctx.db.wall.id.find('W-MECH-E');
      if (mw) ctx.db.wall.id.update({ ...mw, health: 'watch' });
      ctx.db.workOrder.insert({ id: `WO-${4600 + Number(ctx.db.workOrder.count())}`, assetId: 'CH-2', title: 'Chiller 2 isolation pads + epoxy crack injection, Mechanical east wall', status: 'scheduled', ageDays: 0, createdBy: 'cascade', cost: a.cost });
    } else if (a.kind === 'portable_cooling') {
      plan.portable = true;
      ctx.db.workOrder.insert({ id: `WO-${4600 + Number(ctx.db.workOrder.count())}`, assetId: 'AHU-2', title: 'Deliver 15-ton portable cooling to Server Room 300', status: 'scheduled', ageDays: 0, createdBy: 'cascade', cost: a.cost });
    }
    ctx.db.actionCandidate.id.update({ ...a, status: 'done' });
  }

  const b = loadBuilding(ctx);
  const f = forecastThreshold(ctx, 'CH-2');
  const risk = riskEstimate(ctx, b, plan, f.hours, failureChance(ctx, 'CH-2'));
  const main = simulate(b, plan, f.hours);
  saveScenario(ctx, inv.id, 'with_plan', 'With approved plan', true, b, main, risk);
  const dn = [...ctx.db.scenario.iter()].find(s => s.label === 'do_nothing');
  const seq = ctx.db.agentStep.count();
  ctx.db.agentStep.insert({ id: 0n, invId: inv.id, seq: Number(seq) + 100, agent: 'simulation', tool: 'execute_action → simulate_cascade', why: 'Proving the approved plan works',
    result: main.outageHour < 0 ? `outage avoided · impact ${money(dn?.impact ?? 0)} → ${money(main.impact)}` : `outage still at ${clock(main.outageHour)}: approve more actions`,
    target: 'SRV300', status: 'done', model: '' });
  const ch2 = ctx.db.asset.id.find('CH-2');
  if (ch2 && plan.repair) ctx.db.asset.id.update({ ...ch2, health: 'repair' });
  ctx.db.investigation.id.update({ ...inv, status: main.outageHour < 0 ? 'resolved' : 'awaiting_approval' });
  setTimeline(ctx, { label: 'with_plan' });
  emit(ctx, { kind: 'clear' });
});

// ================================================================ collaboration

/** Row-level security: shared notes are visible to everyone, private notes only to their author. */
export const sharedNotes = spacetimedb.clientVisibilityFilter.sql('SELECT * FROM field_note WHERE is_private = false');
export const ownNotes = spacetimedb.clientVisibilityFilter.sql('SELECT * FROM field_note WHERE author = :sender');

function placeOf(ctx: Ctx, targetId: string, u: { x: number; z: number }): { x: number; z: number; label: string } {
  const a = ctx.db.asset.id.find(targetId);
  if (a) return { x: a.x, z: a.z, label: a.name };
  const r = ctx.db.room.id.find(targetId);
  if (r) return { x: (r.x0 + r.x1) / 2, z: (r.z0 + r.z1) / 2, label: r.name };
  const w = ctx.db.wall.id.find(targetId);
  if (w) return { x: (w.x0 + w.x1) / 2, z: (w.z0 + w.z1) / 2, label: w.name };
  const i = ctx.db.issue.id.find(targetId);
  if (i) return { x: i.x, z: i.z, label: i.title };
  return { x: u.x, z: u.z, label: 'here' };
}

/** "Look here": a beacon on every headset. kind: look | come */
export const sendPing = spacetimedb.reducer({ targetId: t.string(), kind: t.string() }, (ctx, { targetId, kind }) => {
  const u = me(ctx);
  const p = placeOf(ctx, targetId, u);
  ctx.db.ping.insert({ fromName: u.name, fromRole: u.role, targetId, x: p.x, z: p.z, kind: kind === 'come' ? 'come' : 'look', label: p.label });
});

/** A field note pinned to a place; the agents read shared notes as evidence. */
export const addNote = spacetimedb.reducer({ targetId: t.string(), text: t.string(), isPrivate: t.bool() }, (ctx, { targetId, text, isPrivate }) => {
  const u = me(ctx);
  const body = text.trim().slice(0, 280);
  if (!body) throw new SenderError('empty note');
  const p = placeOf(ctx, targetId, u);
  ctx.db.fieldNote.insert({ id: 0n, author: ctx.sender, authorName: u.name, role: u.role, targetId: targetId || '', x: p.x, z: p.z, text: body, isPrivate, at: ctx.timestamp });
});

export const deleteNote = spacetimedb.reducer({ id: t.u64() }, (ctx, { id }) => {
  const n = ctx.db.fieldNote.id.find(id);
  if (n && n.author.equals(ctx.sender)) ctx.db.fieldNote.id.delete(id);
});

export const resetDemo = spacetimedb.reducer(ctx => { resetAll(ctx); setTimeline(ctx, { built: false }); });

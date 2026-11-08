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

import { schema, table, t, SenderError, type InferSchema, type ReducerCtx, type ProcedureCtx } from 'spacetimedb/server';
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
const narrateJob = table({ name: 'narrate_job' }, {
  scheduledId: t.u64().primaryKey().autoInc(),
  scheduledAt: t.scheduleAt(),
  invId: t.u64(),
});

const spacetimedb = schema({
  room, asset, dependency, sensorReading, workOrder, tenant, pastIncident, notice, issue,
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
  for (const [id, kind, severity, title, detail, source, roomId, assetId, surface, x, y, z, nx, nz] of ISSUES)
    db.issue.insert({ id, kind, severity, title, detail, source, roomId, assetId, surface, x, y, z, nx, nz, status: 'open' });

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

const STEP_MICROS = 1_150_000n;
const DONE_MICROS = 450_000n;

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

function openInvestigation(ctx: Ctx, trigger: string, source: string, askedBy: string) {
  clearBlackboard(ctx);
  const ai = nemotronReady(ctx);
  const inv = ctx.db.investigation.insert({ id: 0n, trigger, source, askedBy, status: 'investigating', createdAt: ctx.timestamp,
    mode: ai ? 'nemotron' : 'scripted', phase: ai ? 'plan' : '', phasesDone: '', critiqueRounds: 0, pending: '' });
  emit(ctx, { kind: 'clear' });
  setTimeline(ctx, { hour: 0, label: 'do_nothing' });
  if (ai) scheduleJob(ctx, inv.id, 'plan', '', 200_000n);
  else {
    ctx.db.agentPlan.insert({ invId: inv.id, cursor: 0, opsJson: '' });
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
        ['simulation', 'Simulate the next 24 hours and test fixes', 'signal,historian,lease'],
        ['critic', 'Challenge the evidence and the plan', 'simulation'],
      ];
      for (const [agent, goal, dependsOn] of plan)
        db.subtask.insert({ id: 0n, invId: id, agent, goal, dependsOn, status: dependsOn ? 'waiting' : 'ready' });
      return { result: '5 subtasks · signal, historian, lease run in parallel' };
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
      ];
      for (const [kind, target, title, detail, cost] of acts)
        db.actionCandidate.insert({ id: 0n, invId: id, kind, target, title, detail, cost, saved: 0, rank: 0, recommended: false, status: 'proposed' });
      return { result: '4 candidates: repair, load shift, tenant notice, portable cooling' };
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
        summary: `Repair Chiller 2 today with the server room on Chiller 1, and notify Northwind. Outage risk ${Math.round(dn.outageChance * 100)}% → ${Math.round(wp.outageChance * 100)}%, impact ${money(dn.impact)} → ${money(wp.impact)}. Would change our mind: Chiller 1 vibration rising too.`,
      });
      return { result: `root cause ${top.assetId} (${Math.round(top.likelihood * 100)}%)` };
    }
    case 20: {
      const dn = [...db.scenario.iter()].find(s => s.label === 'do_nothing')!;
      const f = forecastThreshold(ctx, 'CH-2');
      db.answer.insert({ id: 0n, invId: id, role: 'technician', byNemotron: false,
        text: `Chiller 2's bearing is failing and will likely trip around ${clock(f.hours)}. Go to the Mechanical room; I've drawn the path. Check the bearing first, then shift the server room to Chiller 1.` });
      db.answer.insert({ id: 0n, invId: id, role: 'manager', byNemotron: false,
        text: `If we do nothing, Server Room 300 overheats tonight: ${dn.downtimeHours.toFixed(1)} hours down and about ${money(dn.impact)} in losses and penalties. I recommend a planned repair today for ${money(PLANNED_REPAIR_COST)}, with a load shift and a tenant notice. Approve to proceed.` });
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
  if (i >= OPS.length) { ctx.db.agentPlan.invId.delete(plan.invId); return; }
  const op = OPS[i];
  if (c % 2 === 0) {
    ctx.db.agentStep.insert({ id: 0n, invId: inv.id, seq: i, agent: op.agent, tool: op.tool, why: op.why, result: '', target: op.target ?? '', status: 'running', model: '' });
    schedule(ctx, inv.id, STEP_MICROS);
  } else {
    const out = runOp(ctx, inv, op, i);
    const row = [...ctx.db.agentStep.iter()].find(s => s.invId === inv.id && s.seq === i);
    if (row) ctx.db.agentStep.id.update({ ...row, result: out.result, status: out.status ?? 'done' });
    schedule(ctx, inv.id, DONE_MICROS);
  }
  ctx.db.agentPlan.invId.update({ ...plan, cursor: c + 1 });
});

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

const W = { why: { type: 'string', description: 'One plain sentence under 15 words: what you are checking. Shown live in VR.' } };
const ASSET = { type: 'string', description: 'Asset id, e.g. CH-2' };
const ACTIONS = { type: 'array', items: { type: 'string', enum: Object.keys(ACTION_KINDS) }, description: 'Actions to apply; empty = do nothing' };

const TOOLS: Record<string, ToolDef> = {
  get_investigation: { name: 'get_investigation', description: 'The trigger, status and subtasks of this investigation.', params: { ...W } },
  get_building_summary: { name: 'get_building_summary', description: 'Rooms, assets, tenants and how cooling flows.', params: { ...W } },
  post_evidence: { name: 'post_evidence', description: 'Post one finding to the blackboard. Must cite an existing row.', params: { ...W,
    fact: { type: 'string', description: 'One sentence, numbers only from tool results' },
    source_table: { type: 'string', enum: ['asset', 'room', 'work_order', 'tenant', 'past_incident', 'sensor_reading', 'scenario', 'dependency', 'issue'] },
    source_row: { type: 'string', description: 'Row id in that table (asset id for sensor_reading/dependency)' },
    confidence: { type: 'number', description: '0..1' },
    about_asset: { type: 'string', description: 'Asset the finding is about' },
    supports: { type: 'boolean', description: 'true if it points to a real problem with about_asset' } },
    required: ['why', 'fact', 'source_table', 'source_row', 'confidence', 'about_asset', 'supports'] },
  complete_subtask: { name: 'complete_subtask', description: 'Finish your subtask with a one-sentence summary.', params: { ...W, summary: { type: 'string' } }, required: ['why', 'summary'] },
  create_subtasks: { name: 'create_subtasks', description: 'Create 4-8 subtasks for the specialists.', params: { ...W,
    subtasks: { type: 'array', items: { type: 'object', properties: {
      agent_type: { type: 'string', enum: ['signal', 'historian', 'lease', 'simulation', 'critic'] },
      goal: { type: 'string' }, depends_on: { type: 'array', items: { type: 'string' } } }, required: ['agent_type', 'goal'] } } },
    required: ['why', 'subtasks'] },
  post_hypothesis: { name: 'post_hypothesis', description: 'Name one possible root cause (max 3 per investigation).', params: { ...W,
    asset_id: ASSET, mechanism: { type: 'string', description: 'Failure mechanism in one sentence' },
    supporting_evidence_ids: { type: 'array', items: { type: 'integer' } },
    contradicting_evidence_ids: { type: 'array', items: { type: 'integer' } } }, required: ['why', 'asset_id', 'mechanism'] },
  post_action_candidate: { name: 'post_action_candidate', description: 'Propose one action. Costs come from the price list, not from you.', params: { ...W,
    kind: { type: 'string', enum: Object.keys(ACTION_KINDS) }, rationale: { type: 'string' } }, required: ['why', 'kind'] },
  write_decision: { name: 'write_decision', description: 'Final decision once the Critic approved.', params: { ...W,
    root_cause_asset: ASSET, summary: { type: 'string', description: 'Root cause, evidence chain, ranked actions' },
    what_would_change_my_mind: { type: 'string' } }, required: ['why', 'root_cause_asset', 'summary'] },
  get_sensor_history: { name: 'get_sensor_history', description: 'Hourly readings for the last 24 h, the live value and the normal baseline.', params: { ...W, asset_id: ASSET }, required: ['why', 'asset_id'] },
  detect_anomaly: { name: 'detect_anomaly', description: 'Score how unusual each chiller reading is (z-score vs normal).', params: { ...W } },
  forecast_threshold: { name: 'forecast_threshold', description: 'Hours until an asset crosses its alarm limit, from the trend.', params: { ...W, asset_id: ASSET }, required: ['why', 'asset_id'] },
  check_sensor_health: { name: 'check_sensor_health', description: 'Is a sensor trustworthy? Compares noise and a sibling sensor.', params: { ...W, asset_id: ASSET }, required: ['why', 'asset_id'] },
  get_asset: { name: 'get_asset', description: 'Asset registry row: age, expected life, capacity, health.', params: { ...W, asset_id: ASSET }, required: ['why', 'asset_id'] },
  get_work_orders: { name: 'get_work_orders', description: 'Open and closed tickets for an asset.', params: { ...W, asset_id: ASSET }, required: ['why', 'asset_id'] },
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
    requests: { type: 'array', items: { type: 'object', properties: { agent_type: { type: 'string', enum: ['signal', 'historian', 'lease', 'simulation'] }, request: { type: 'string' } }, required: ['agent_type', 'request'] } } },
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
        if (!['signal', 'historian', 'lease', 'simulation', 'critic'].includes(agentType)) continue;
        db.subtask.insert({ id: 0n, invId, agent: agentType, goal: str(sObj.goal).slice(0, 200), dependsOn: arr(sObj.depends_on).map(x => str(x)).join(','),
          status: ['signal', 'historian', 'lease'].includes(agentType) ? 'ready' : 'waiting' });
        n++;
      }
      for (const need of ['signal', 'historian', 'lease'])
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
      const kind = str(a.kind), k = ACTION_KINDS[kind];
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
      const reqs = arr(a.requests).slice(0, 3).map(r => r as Json).filter(r => ['signal', 'historian', 'lease', 'simulation'].includes(str(r.agent_type)));
      const text = verdict === 'revise' && reqs.length ? reqs.map(r => str(r.request)).join(' ') : str(a.summary);
      db.critique.insert({ id: 0n, invId, verdict: verdict === 'revise' && reqs.length ? 'revise' : 'approve', text: text.slice(0, 300) });
      if (verdict === 'revise' && reqs.length && inv.critiqueRounds < 2) {
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
  if (/super-49b-v1$/.test(model) || /nemotron-70b|ultra-253b-v1$/.test(model)) return think ? 'detailed thinking on' : 'detailed thinking off';
  return think ? '/think' : '/no_think';
}

type ChatOut = { content: string; reasoning: string; calls: Call[]; raw: Json };

function chat(ctx: PCtx, cfg: AiCfg, model: string, messages: Json[], tools: ToolDef[], think: boolean): ChatOut {
  const body = {
    model, messages, temperature: think ? 0.4 : 0.2, top_p: 0.95, max_tokens: think ? 2048 : 900,
    tools: tools.map(td => ({ type: 'function', function: { name: td.name, description: td.description,
      parameters: { type: 'object', properties: td.params, required: td.required ?? ['why'] } } })),
    tool_choice: 'auto',
  };
  const res = ctx.http.fetch(`${cfg.base}/chat/completions`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', accept: 'application/json', authorization: `Bearer ${cfg.key}` },
    body: JSON.stringify(body),
    timeout: TimeDuration.fromMillis(60000),
  });
  const text = res.text();
  if (res.status !== 200) throw new Error(`Nemotron HTTP ${res.status}: ${text.slice(0, 160)}`);
  const j = JSON.parse(text) as Json;
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
  plan: 'ROLE: PLANNER. Call get_investigation and get_building_summary, then create_subtasks with 4-6 subtasks: agent_type (signal, historian, lease, simulation, critic), a one-sentence goal, and depends_on. Simulation depends on the evidence subtasks.',
  signal: 'ROLE: SIGNAL ANALYST. Check sensor history for the chillers. Use detect_anomaly to find unusual readings, forecast_threshold for hours until the limit, and check_sensor_health to rule out a faulty sensor. Post 2-4 pieces of evidence with post_evidence, then complete_subtask.',
  historian: 'ROLE: MAINTENANCE HISTORIAN. Check the suspicious asset\'s age against expected life, its open and recent work orders, get_inspection_issues for physical damage it has already caused, and search_past_incidents for similar failures. Flag tickets that were ignored or repeated. Post 2-4 pieces of evidence, then complete_subtask.',
  lease: 'ROLE: TENANT AND LEASE ANALYST. Use get_dependents to find every room the suspicious chiller serves, then get_tenants and get_lease_terms. Report who is affected, their cost of downtime per hour, uptime clauses and penalties. Post 2-4 pieces of evidence, then complete_subtask.',
  hypothesize: 'ROLE: PLANNER. Read get_evidence_chain, then call post_hypothesis for up to 3 root causes (one may be "SENSOR" for a faulty sensor). Each names an asset, the failure mechanism in one sentence, and the evidence ids that support or contradict it.',
  simulate: 'ROLE: SIMULATION ENGINEER. Run simulate_cascade with no actions (the do-nothing baseline) and run_risk_estimate with no actions. Post the outage time, downtime and impact as evidence (source_table scenario, source_row do_nothing), then complete_subtask.',
  propose: 'ROLE: PLANNER. Read get_evidence_chain, then call post_action_candidate for 3-4 actions that address the top hypothesis. Kinds: work_order (planned repair), load_shift, tenant_notice, portable_cooling.',
  compare: 'ROLE: SIMULATION ENGINEER. Call compare_scenarios to test every proposed action against doing nothing. If a repair takes a chiller offline, also simulate_cascade with the recommended plan to check the server room stays under its limit.',
  critique: 'ROLE: CRITIC. Find what the team got wrong before a person acts on it. Call get_evidence_chain, then check: 1) every hypothesis has 2+ independent sources; 2) contradicting evidence was not ignored; 3) no action rests on an unchecked assumption, for example whether Chiller 1 has enough spare capacity during a repair; 4) every action was verified by simulation; 5) impact numbers come from tool results. Then post_critique: "approve", or "revise" with at most 3 specific requests, each naming an agent_type. Be strict but fair.',
  decide: 'ROLE: PLANNER. The Critic approved. Call get_evidence_chain, then write_decision with the root cause, the evidence chain in order, the recommended actions in ranked order with costs, and what would change your mind.',
  narrate: 'ROLE: NARRATOR. Turn the decision into speech. Call get_evidence_chain, then post_answer twice, at most 3 short sentences each, plain spoken English, no markdown: technician = what is wrong, where to go, what to check first; manager = the risk, the cost if ignored, the recommended action and its cost, and ask them to approve. Then issue_scene_command guide_trail to the root-cause asset and a thread from it to the affected room.',
};

const PHASE_AGENT: Record<string, string> = {
  plan: 'planner', signal: 'signal', historian: 'historian', lease: 'lease', hypothesize: 'planner', simulate: 'simulation',
  propose: 'planner', compare: 'simulation', critique: 'critic', decide: 'planner', narrate: 'narrator',
};
const THINKERS = new Set(['plan', 'hypothesize', 'critique', 'decide']);

/** One agent's tool-calling loop. Returns true if the phase's goal was met. */
function runAgent(ctx: PCtx, cfg: AiCfg, invId: bigint, phase: string, note: string): boolean {
  const agent = PHASE_AGENT[phase.split(':')[0]] ?? 'planner';
  const base = phase.split(':')[0];
  const think = THINKERS.has(base);
  const model = think ? cfg.large : cfg.small;
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
    return { inv: { id: inv.id, trigger: inv.trigger }, assets, rooms, goal: sub?.goal ?? '', done };
  });
  if (!setup) return false;

  const rolePrompt = base === 'revise'
    ? `${ROLE_PROMPTS[note.split('|')[0] === 'simulation' ? 'compare' : note.split('|')[0]] ?? ''}\nTHE CRITIC SENT WORK BACK: ${note.split('|')[1] ?? ''}\nAnswer exactly that request with tools, post evidence, then complete_subtask.`
    : ROLE_PROMPTS[base];
  const system = `${thinkPrefix(model, think)}\n${PREAMBLE(setup.inv, setup.assets, setup.rooms, 8)}\n\n${rolePrompt}`;
  const user = `YOUR SUBTASK: ${setup.goal || setup.inv.trigger}\nCONTEXT FROM FINISHED SUBTASKS:\n${setup.done || '(none yet)'}\nStart now by calling tools.`;
  const messages: Json[] = [{ role: 'system', content: system }, { role: 'user', content: user }];

  let calls = 0, nudged = false;
  for (let round = 0; round < 5 && calls < 10; round++) {
    const thinking = ctx.withTx(tx => logStep(tx, invId, agent, 'thinking', think ? 'Reasoning…' : 'Choosing tools…', '', '', 'thinking', model));
    let out: ChatOut;
    try { out = chat(ctx, cfg, model, messages, tools, think); }
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
      if (!toolNames.includes(c.name)) result = { error: `tool ${c.name} is not available to you` };
      else {
        try {
          result = ctx.withTx(tx => {
            const r = execTool(tx, invId, agent, c);
            const status = c.name === 'post_critique' && str(r.line).startsWith('REVISE') ? 'revise' : 'done';
            logStep(tx, invId, agent, c.name, str(c.args.why) || c.name.replace(/_/g, ' '), r.line, toolTarget(c.args), status, model);
            return r.out;
          });
        } catch (e) {
          const msg = String(e instanceof Error ? e.message : e);
          ctx.withTx(tx => logStep(tx, invId, agent, c.name, str(c.args.why) || c.name, `rejected: ${msg}`, toolTarget(c.args), 'error', model));
          result = { error: msg };
        }
      }
      messages.push({ role: 'tool', tool_call_id: c.id, name: c.name, content: JSON.stringify(result).slice(0, 3500) });
    }
    if (goalMet(ctx, invId, base, note) && ['plan', 'hypothesize', 'critique', 'decide'].includes(base)) break;
  }
  return goalMet(ctx, invId, base, note);
}

function goalMet(ctx: PCtx, invId: bigint, base: string, note: string): boolean {
  return ctx.withTx(tx => {
    const db = tx.db;
    const mine = <T extends { invId: bigint }>(it: Iterable<T>) => [...it].filter(x => x.invId === invId);
    switch (base) {
      case 'plan': return mine(db.subtask.iter()).length > 0;
      case 'signal': case 'historian': case 'lease': return mine(db.evidence.iter()).some(e => e.agent === base);
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
  plan: [0, 1, 2], signal: [3, 5, 11], historian: [6, 7, 8], lease: [9, 10], hypothesize: [4], simulate: [12, 13],
  propose: [14], compare: [15], critique: [18], decide: [19], narrate: [20], revise: [17],
};

function runFallback(ctx: PCtx, invId: bigint, base: string) {
  for (const i of FALLBACK[base] ?? []) {
    const op = OPS[i];
    const step = ctx.withTx(tx => logStep(tx, invId, op.agent, op.tool, op.why, '', op.target ?? '', 'running', ''));
    try { ctx.sleep(TimeDuration.fromMillis(700)); } catch { /* no pacing */ }
    ctx.withTx(tx => {
      const inv = tx.db.investigation.id.find(invId);
      if (!inv) return;
      let out: { result: string; status?: string };
      try { out = runOp(tx, inv, op, i); } catch (e) { out = { result: `skipped: ${String(e).slice(0, 120)}`, status: 'error' }; }
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

  if (base === 'plan') { next('signal'); next('historian'); next('lease'); row.phase = 'evidence'; }
  else if (['signal', 'historian', 'lease'].includes(base)) {
    if (['signal', 'historian', 'lease'].every(p => done.has(p)) && !done.has('hypothesize-scheduled')) { done.add('hypothesize-scheduled'); row.phasesDone = [...done].join(','); next('hypothesize'); }
  }
  else if (base === 'hypothesize') next('simulate');
  else if (base === 'simulate') next('propose');
  else if (base === 'propose') next('compare');
  else if (base === 'compare') next('critique');
  else if (base === 'critique') {
    const rounds = inv.critiqueRounds + 1;
    row.critiqueRounds = rounds;
    const last = [...tx.db.critique.iter()].filter(c => c.invId === invId).sort((x, y) => (x.id < y.id ? 1 : -1))[0];
    if (last?.verdict === 'revise' && rounds <= 2 && inv.pending) {
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
    ok = runAgent(ctx, state, job.invId, job.phase, job.note);
    // spec: on bad output, the error goes back to the model once, then the cached path is used
    if (!ok && base !== 'revise') ok = runAgent(ctx, state, job.invId, job.phase, job.note);
  }
  if (!ok) runFallback(ctx, job.invId, base);
  // the critique fallback approves; the critic must always leave a verdict
  ctx.withTx(tx => {
    if (base === 'narrate' && [...tx.db.answer.iter()].filter(a => a.invId === job.invId).length < 2) {
      const inv = tx.db.investigation.id.find(job.invId);
      if (inv) runOp(tx, inv, OPS[20], 20);
    }
    if (['signal', 'historian', 'lease'].includes(base)) subtaskDone(tx, job.invId, base);
    advance(tx, job.invId, job.phase);
  });
  return {};
});

// ---------------------------------------------------------------- key + model check

const MODEL_SKIP = /embed|rerank|reward|safety|guard|vl|vision|parse|ocr|retriev|content|audio|asr|tts|nemoguard|clip|instruct-70b-reward|math|code/i;

function pickModels(ids: string[]): { large: string; small: string } {
  const nem = ids.filter(id => /nemotron/i.test(id) && !MODEL_SKIP.test(id)).sort().reverse();
  const by = (re: RegExp) => nem.find(id => re.test(id)) ?? '';
  const large = by(/nemotron-3.*super|nemotron-3.*ultra/i) || by(/super-49b-v1\.5/) || by(/super/i) || by(/ultra/i) || nem[0] || '';
  const small = by(/nemotron-3.*nano/i) || by(/nemotron-nano-9b-v2|nano-9b/i) || by(/nano/i) || large;
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
      body: JSON.stringify({ model: small, max_tokens: 8, messages: [{ role: 'system', content: '/no_think' }, { role: 'user', content: 'Reply with OK.' }] }),
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

export const resetDemo = spacetimedb.reducer(ctx => { resetAll(ctx); setTimeline(ctx, { built: false }); });

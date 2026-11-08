import { SenderError, t } from 'spacetimedb/server';
import { ScheduleAt } from 'spacetimedb';
import spacetimedb, { simTimer, userProfile, type Ctx } from './schema';
import {
  BLUEPRINTS, CASCADE_RULES, DATA_SOURCES, LOCATIONS, SENSOR_SPECS, sensorFromCondition,
} from './seed';
import { blueprintRisk, currentYear, heatedSqft, recompute } from './engine';
import { completeWorkOrder, createWorkOrderTx } from './ops';

export default spacetimedb;
export { askAgent } from './agent';

const PALETTE = ['#FF6B6B', '#4ECDC4', '#FFD166', '#6C5CE7', '#00B894', '#FD79A8', '#0984E3', '#E17055'];
const DEFAULT_SPEED_MS = 4000;

// ───────────────────────────── Seeding ──────────────────────────────

function seedStatic(ctx: Ctx) {
  for (const l of LOCATIONS) ctx.db.location.insert(l);
  CASCADE_RULES.forEach(([fromKind, toKind, weight, mechanism, source], i) =>
    ctx.db.cascadeRule.insert({ id: i + 1, fromKind, toKind, weight, mechanism, source }));
  DATA_SOURCES.forEach(([domain, name, usage, url], i) =>
    ctx.db.dataSource.insert({ id: i + 1, domain, name, usage, url }));
}

function seedBlueprints(ctx: Ctx, year: number) {
  for (const b of BLUEPRINTS) {
    const roomIds = new Map<string, number>();
    b.rooms.forEach((r, i) => {
      const id = b.id * 100 + i + 1;
      roomIds.set(r.name, id);
      ctx.db.room.insert({
        id, blueprintId: b.id, name: r.name, kind: r.kind, floor: r.floor, x: r.x, z: r.z, w: r.w, d: r.d,
        ceiling: r.ceiling ?? 9, glazing: r.glazing ?? 0, flags: r.flags ?? [],
      });
    });
    const sqft = heatedSqft(b.rooms.map((r, i) => ({ ...r, id: i, ceiling: 9, glazing: 0, flags: [] })));
    const { rooms: _r, components: _c, ...meta } = b;
    ctx.db.blueprint.insert({ ...meta, sqft });
    seedComponents(ctx, b.id, year, roomIds);
  }
}

function seedComponents(ctx: Ctx, bpId: number, year: number, roomIds?: Map<string, number>) {
  const b = BLUEPRINTS.find(x => x.id === bpId)!;
  if (!roomIds) {
    roomIds = new Map();
    for (const r of ctx.db.room.blueprintId.filter(bpId)) roomIds.set(r.name, r.id);
  }
  b.components.forEach((c, i) => {
    const spec = SENSOR_SPECS[c.kind];
    ctx.db.component.insert({
      id: bpId * 100 + i + 1, blueprintId: bpId, kind: c.kind, name: c.name, material: c.material,
      roomId: roomIds!.get(c.room) ?? bpId * 100 + 1, ox: c.ox ?? 0.5, oz: c.oz ?? 0.5, oy: c.oy ?? 4,
      installYear: year - c.age, expectedLife: c.life, replaceCost: c.cost, condition: c.condition,
      hazardFlag: c.hazardFlag ?? '', sensorLabel: spec.label, sensorValue: sensorFromCondition(c.kind, c.condition),
      sensorUnit: spec.unit, sensorWarn: spec.warn, sensorCritical: spec.crit,
    });
  });
}

function recomputeAll(ctx: Ctx) {
  for (const b of ctx.db.blueprint.iter()) recompute(ctx, b.id);
}

export const init = spacetimedb.init(ctx => {
  const nowYear = 1970 + Number(ctx.timestamp.microsSinceUnixEpoch / 1000n) / (365.2425 * 86400000);
  ctx.db.simulationState.insert({ id: 1, running: false, simMonths: 0, baseYear: nowYear, speedMs: DEFAULT_SPEED_MS });
  seedStatic(ctx);
  seedBlueprints(ctx, nowYear);
  recomputeAll(ctx);
  ctx.db.simTimer.insert({ scheduledId: 0n, scheduledAt: ScheduleAt.interval(BigInt(DEFAULT_SPEED_MS) * 1000n) });
});

// ───────────────────────────── Identity & presence ──────────────────────────────

export const onConnect = spacetimedb.clientConnected(ctx => {
  const p = ctx.db.userProfile.identity.find(ctx.sender);
  if (p) ctx.db.userProfile.identity.update({ ...p, online: true, lastSeen: ctx.timestamp });
});

export const onDisconnect = spacetimedb.clientDisconnected(ctx => {
  const p = ctx.db.userProfile.identity.find(ctx.sender);
  if (p) ctx.db.userProfile.identity.update({ ...p, online: false, lastSeen: ctx.timestamp });
  if (ctx.db.presence.identity.find(ctx.sender)) ctx.db.presence.identity.delete(ctx.sender);
});

export const registerUser = spacetimedb.reducer(
  { name: t.string(), role: t.string(), company: t.string() },
  (ctx, { name, role, company }) => {
    const clean = name.trim().slice(0, 40);
    if (!clean) throw new SenderError('Name is required');
    if (role !== 'construction_manager' && role !== 'real_estate_agent') throw new SenderError('Unknown role');
    const existing = ctx.db.userProfile.identity.find(ctx.sender);
    const color = existing?.color ?? PALETTE[ctx.random.integerInRange(0, PALETTE.length - 1)];
    const row = {
      identity: ctx.sender, name: clean, role, company: company.trim().slice(0, 60), color, online: true,
      selectedBlueprint: existing?.selectedBlueprint ?? 0, lastSeen: ctx.timestamp,
    };
    if (existing) ctx.db.userProfile.identity.update(row);
    else ctx.db.userProfile.insert(row);
  }
);

function requireProfile(ctx: Ctx) {
  const p = ctx.db.userProfile.identity.find(ctx.sender);
  if (!p) throw new SenderError('Sign in first');
  return p;
}

export const selectBlueprint = spacetimedb.reducer({ blueprintId: t.u32() }, (ctx, { blueprintId }) => {
  const p = requireProfile(ctx);
  if (!ctx.db.blueprint.id.find(blueprintId)) throw new SenderError('Unknown blueprint');
  ctx.db.userProfile.identity.update({ ...p, selectedBlueprint: blueprintId, lastSeen: ctx.timestamp });
});

export const updatePresence = spacetimedb.reducer(
  { blueprintId: t.u32(), x: t.f32(), y: t.f32(), z: t.f32(), yaw: t.f32() },
  (ctx, { blueprintId, x, y, z, yaw }) => {
    const p = ctx.db.userProfile.identity.find(ctx.sender);
    if (!p) return;
    const row = { identity: ctx.sender, blueprintId, name: p.name, color: p.color, x, y, z, yaw, updatedAt: ctx.timestamp };
    if (ctx.db.presence.identity.find(ctx.sender)) ctx.db.presence.identity.update(row);
    else ctx.db.presence.insert(row);
  }
);

export const addAnnotation = spacetimedb.reducer(
  { blueprintId: t.u32(), x: t.f32(), y: t.f32(), z: t.f32(), text: t.string() },
  (ctx, { blueprintId, x, y, z, text }) => {
    const p = requireProfile(ctx);
    ctx.db.annotation.insert({ id: 0n, blueprintId, authorName: p.name, color: p.color, x, y, z, text: text.slice(0, 200), at: ctx.timestamp });
  }
);

export const deleteAnnotation = spacetimedb.reducer({ id: t.u64() }, (ctx, { id }) => {
  requireProfile(ctx);
  ctx.db.annotation.id.delete(id);
});

// ───────────────────────────── Execution: work orders ──────────────────────────────

export const createWorkOrder = spacetimedb.reducer(
  { componentId: t.u32(), action: t.string() },
  (ctx, { componentId, action }) => {
    const p = requireProfile(ctx);
    createWorkOrderTx(ctx, componentId, action || 'Repair / replace', 'user', p.name);
  }
);

export const advanceWorkOrder = spacetimedb.reducer({ id: t.u64() }, (ctx, { id }) => {
  requireProfile(ctx);
  const wo = ctx.db.workOrder.id.find(id);
  if (!wo) throw new SenderError('Unknown work order');
  const next = wo.status === 'dispatched' ? 'in_progress' : 'done';
  ctx.db.workOrder.id.update({ ...wo, status: next });
  if (next === 'done') completeWorkOrder(ctx, wo);
});

// ───────────────────────────── Live simulation (scheduled reducer) ──────────────────────────────

export const setSimulation = spacetimedb.reducer({ running: t.bool() }, (ctx, { running }) => {
  requireProfile(ctx);
  const s = ctx.db.simulationState.id.find(1)!;
  ctx.db.simulationState.id.update({ ...s, running });
});

export const stepSimulation = spacetimedb.reducer({ months: t.u32() }, (ctx, { months }) => {
  requireProfile(ctx);
  for (let i = 0; i < Math.min(24, months); i++) advanceMonth(ctx);
});

export const resetDemo = spacetimedb.reducer(ctx => {
  requireProfile(ctx);
  for (const c of [...ctx.db.component.iter()]) ctx.db.component.id.delete(c.id);
  for (const w of [...ctx.db.workOrder.iter()]) ctx.db.workOrder.id.delete(w.id);
  for (const a of [...ctx.db.alert.iter()]) ctx.db.alert.id.delete(a.id);
  for (const a of [...ctx.db.annotation.iter()]) ctx.db.annotation.id.delete(a.id);
  for (const m of [...ctx.db.agentMessage.iter()]) ctx.db.agentMessage.id.delete(m.id);
  for (const s of [...ctx.db.agentStep.iter()]) ctx.db.agentStep.id.delete(s.id);
  for (const r of [...ctx.db.agentRun.iter()]) ctx.db.agentRun.id.delete(r.id);
  const s = ctx.db.simulationState.id.find(1)!;
  ctx.db.simulationState.id.update({ ...s, running: false, simMonths: 0 });
  const year = currentYear(ctx);
  for (const b of BLUEPRINTS) seedComponents(ctx, b.id, year);
  recomputeAll(ctx);
});

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

function advanceMonth(ctx: Ctx) {
  const s = ctx.db.simulationState.id.find(1)!;
  const month = s.simMonths + 1;
  ctx.db.simulationState.id.update({ ...s, simMonths: month });
  const calMonth = Math.floor(((s.baseYear % 1) * 12 + month)) % 12;

  // Move work orders through their lifecycle.
  for (const wo of [...ctx.db.workOrder.iter()]) {
    if (wo.status === 'dispatched') ctx.db.workOrder.id.update({ ...wo, status: 'in_progress' });
    else if (wo.status === 'in_progress') {
      ctx.db.workOrder.id.update({ ...wo, status: 'done' });
      completeWorkOrder(ctx, wo);
    }
  }

  for (const bp of ctx.db.blueprint.iter()) {
    const loc = ctx.db.location.id.find(bp.locationId)!;
    const hail = calMonth >= 2 && calMonth <= 5 && ctx.random() < loc.hailRisk * 0.18;
    const freeze = (calMonth === 0 || calMonth === 1 || calMonth === 11) && ctx.random() < loc.freezeRisk * 0.12;
    const drought = (calMonth === 6 || calMonth === 7) && loc.soilShrinkSwell > 0.6;
    if (hail) alertFor(ctx, bp.id, 0, 'warn', `${MONTHS[calMonth]}: NOAA hail report (1.75") near ${loc.city} — inspect roof & windows`, month);
    if (freeze) alertFor(ctx, bp.id, 0, 'warn', `${MONTHS[calMonth]}: hard freeze (< 20°F) forecast — exposed plumbing at risk`, month);

    const risk = blueprintRisk(ctx, bp.id);
    const riskBy = new Map(risk?.risk.rows.map(r => [r.componentId, r]) ?? []);
    for (const c of [...ctx.db.component.blueprintId.filter(bp.id)]) {
      const r = riskBy.get(c.id);
      let wear = (1 / Math.max(1, c.expectedLife)) / 12 * (0.6 + 0.8 * ctx.random());
      wear += (r?.cascadeProb ?? 0) * 0.02; // stressed upstream systems accelerate wear
      if (hail && (c.kind === 'roof' || c.kind === 'windows')) wear += 0.06 + 0.1 * ctx.random();
      if (freeze && c.kind === 'plumbing') wear += 0.05 + 0.08 * ctx.random();
      if (drought && (c.kind === 'foundation' || c.kind === 'sewer')) wear += 0.015;
      if (bp.stage === 'under_construction' && c.expectedLife < 2) wear += 0.03;
      const condition = Math.max(0.02, c.condition - wear);
      const sensorValue = sensorFromCondition(c.kind, condition);
      if (c.sensorValue < c.sensorCritical && sensorValue >= c.sensorCritical) {
        alertFor(ctx, bp.id, c.id, 'critical', `${c.name}: ${c.sensorLabel} hit ${sensorValue} ${c.sensorUnit} (critical ≥ ${c.sensorCritical})`, month);
      } else if (c.sensorValue < c.sensorWarn && sensorValue >= c.sensorWarn) {
        alertFor(ctx, bp.id, c.id, 'warn', `${c.name}: ${c.sensorLabel} rising — ${sensorValue} ${c.sensorUnit}`, month);
      }
      ctx.db.component.id.update({ ...c, condition, sensorValue });
    }
    recompute(ctx, bp.id, false);
  }

  // Keep the alert feed bounded.
  const alerts = [...ctx.db.alert.iter()].sort((a, b) => Number(b.id - a.id));
  for (const a of alerts.slice(60)) ctx.db.alert.id.delete(a.id);
}

function alertFor(ctx: Ctx, blueprintId: number, componentId: number, level: string, message: string, simMonth: number) {
  ctx.db.alert.insert({ id: 0n, blueprintId, componentId, level, message, simMonth, at: ctx.timestamp });
}

export const simTick = spacetimedb.reducer({ onSchedule: simTimer }, { timer: simTimer.rowType }, (ctx, { timer: _timer }) => {
  const s = ctx.db.simulationState.id.find(1);
  if (!s || !s.running) return;
  advanceMonth(ctx);
});

// Per-viewer view: the signed-in user's own profile.
export const myProfile = spacetimedb.view(
  { name: 'my_profile', public: true },
  t.option(userProfile.rowType),
  ctx => ctx.db.userProfile.identity.find(ctx.sender) ?? undefined
);

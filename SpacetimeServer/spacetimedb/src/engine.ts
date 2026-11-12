// Analysis engines that run *inside* SpacetimeDB transactions:
//   1. Structural integrity checks (IRC-derived rules over plan geometry)
//   2. Weibull lifecycle hazard + noisy-OR cascade propagation over a failure graph
//   3. Location-aware valuation, feature demand and upgrade ROI
// Everything here is deterministic so it can be called from reducers.

import type { Ctx } from './schema';
import { CASCADE_RULES, FEATURE_CATALOG, KIND_ORDER, UPGRADE_CATALOG } from './seed';

type Loc = {
  id: number; name: string; medianPpsf: number; daysOnMarket: number; hailRisk: number;
  soilShrinkSwell: number; floodRisk: number; freezeRisk: number; humidity: number;
  propertyTaxRate: number; buyerTags: string[];
};
type Bp = {
  id: number; stage: string; yearBuilt: number; beds: number; baths: number; floorJoist: string;
  ceilingJoist: string; features: string[]; locationId: number;
};
type Rm = { id: number; name: string; kind: string; floor: number; x: number; z: number; w: number; d: number; ceiling: number; glazing: number; flags: string[] };
type Comp = {
  id: number; kind: string; name: string; installYear: number; expectedLife: number; replaceCost: number;
  condition: number; hazardFlag: string; sensorValue: number; sensorWarn: number; sensorCritical: number; sensorUnit: string; sensorLabel: string;
};

const NON_LIVING = new Set(['garage', 'patio', 'pool', 'deck']);
const FLOOR_SPAN: Record<string, number> = { '2x8': 13.1, '2x10': 16.1, '2x12': 18.8, 'I-joist': 22.0 };
const CEIL_SPAN: Record<string, number> = { '2x6': 14.5, '2x8': 19.0, truss: 32.0 };
const KIND_LABEL: Record<string, string> = {
  roof: 'Roof', gutters: 'Drainage', foundation: 'Foundation', plumbing: 'Plumbing', sewer: 'Sewer',
  electrical: 'Electrical', hvac: 'HVAC', water_heater: 'Water heater', windows: 'Windows',
  attic_insulation: 'Insulation', drywall: 'Finishes', mold: 'Mold / IAQ',
};

const round = (v: number, d = 0) => { const p = Math.pow(10, d); return Math.round(v * p) / p; };
const clamp = (v: number, a: number, b: number) => Math.max(a, Math.min(b, v));
const usd = (v: number) => '$' + Math.round(v).toLocaleString('en-US');

export function currentYear(ctx: Ctx): number {
  const s = ctx.db.simulationState.id.find(1);
  const base = s ? s.baseYear : 2026.85;
  return base + (s ? s.simMonths : 0) / 12;
}

export function heatedSqft(rooms: Rm[]): number {
  return rooms.filter(r => !NON_LIVING.has(r.kind)).reduce((a, r) => a + r.w * r.d, 0);
}

// ───────────────────────────── Structural ──────────────────────────────

type Finding = { roomId: number; severity: string; category: string; title: string; detail: string; codeRef: string; metric: number; limit: number; fixCost: number };

function overlap(a0: number, a1: number, b0: number, b1: number) {
  return Math.max(0, Math.min(a1, b1) - Math.max(a0, b0));
}

type Edge = { horiz: boolean; c: number; a0: number; a1: number };
function edges(r: Rm): Edge[] {
  return [
    { horiz: true, c: r.z, a0: r.x, a1: r.x + r.w },
    { horiz: true, c: r.z + r.d, a0: r.x, a1: r.x + r.w },
    { horiz: false, c: r.x, a0: r.z, a1: r.z + r.d },
    { horiz: false, c: r.x + r.w, a0: r.z, a1: r.z + r.d },
  ];
}

function exteriorLength(r: Rm, sameFloor: Rm[]): { total: number; longest: number } {
  let total = 0, longest = 0;
  for (const e of edges(r)) {
    let shared = 0;
    for (const o of sameFloor) {
      if (o.id === r.id || NON_LIVING.has(o.kind) && o.kind !== 'garage') continue;
      for (const oe of edges(o)) {
        if (oe.horiz === e.horiz && Math.abs(oe.c - e.c) < 0.5) shared += overlap(e.a0, e.a1, oe.a0, oe.a1);
      }
    }
    const ext = Math.max(0, e.a1 - e.a0 - shared);
    total += ext;
    longest = Math.max(longest, ext);
  }
  return { total, longest };
}

export function structuralAnalysis(bp: Bp, rooms: Rm[], comps: Comp[], loc: Loc): Finding[] {
  const out: Finding[] = [];
  const byFloor = (f: number) => rooms.filter(r => r.floor === f);

  for (const r of rooms) {
    if (r.kind === 'pool' || r.kind === 'closet' || r.kind === 'stair') continue;
    const above = byFloor(r.floor + 1).filter(u => !NON_LIVING.has(u.kind) || u.kind === 'deck');
    const coveredArea = above.reduce((a, u) => a + overlap(r.x, r.x + r.w, u.x, u.x + u.w) * overlap(r.z, r.z + r.d, u.z, u.z + u.d), 0);
    const carriesFloor = coveredArea > 0.2 * r.w * r.d;
    const hasBeam = r.flags.includes('beam');

    // 1. Clear-span check
    if (r.kind !== 'deck' && r.kind !== 'garage') {
      const span = Math.min(r.w, r.d);
      const member = carriesFloor ? bp.floorJoist : bp.ceilingJoist;
      const table = carriesFloor ? FLOOR_SPAN : CEIL_SPAN;
      let allow = table[member] ?? 16;
      if (r.kind === 'patio') allow = hasBeam ? 28 : 16;
      else if (hasBeam) allow *= 1.6;
      const ratio = span / allow;
      let severity = ratio > 1.15 ? 'critical' : ratio > 1.0 ? 'watch' : 'ok';
      if (hasBeam && severity === 'ok' && span > (table[member] ?? 16)) severity = 'watch';
      const what = carriesFloor ? `${member} floor framing carrying the level above` : `${member === 'truss' ? 'roof trusses' : member + ' ceiling joists'}`;
      out.push({
        roomId: r.id, severity, category: 'span',
        title: severity === 'ok' ? `${r.name}: span within limits` : `${r.name}: ${round(span, 1)} ft clear span`,
        detail: severity === 'ok'
          ? `${round(span, 1)} ft vs ${round(allow, 1)} ft allowable for ${what}.`
          : hasBeam
            ? `Relies on an engineered beam (${round(span, 1)} ft span). Verify beam sizing, bearing length and point-load path to the foundation.`
            : `${round(span, 1)} ft exceeds ${round(allow, 1)} ft allowable for ${what}. Expect deflection / ceiling cracking; add an LVL beam or intermediate bearing.`,
        codeRef: carriesFloor ? 'IRC R502.3.1 / AWC span tables' : 'IRC R802.4 / AWC span tables',
        metric: round(span, 1), limit: round(allow, 1),
        fixCost: severity === 'critical' ? 9700 : severity === 'watch' ? (hasBeam ? 1200 : 2600) : 0,
      });
    }

    // 2. Removed bearing wall
    if (r.flags.includes('removed_wall')) {
      out.push({
        roomId: r.id, severity: 'critical', category: 'load_path',
        title: `${r.name}: bearing wall removed without beam`,
        detail: 'Permit history shows interior wall removal; the bedrooms above now bear on mid-span floor joists. Install a flush LVL beam with posts to footings before listing.',
        codeRef: 'IRC R301.1 (continuous load path) / R502.4', metric: 1, limit: 0, fixCost: 14500,
      });
    }

    // 4. Cantilevers beyond the floor below
    if (r.floor > 0) {
      const below = byFloor(r.floor - 1).filter(b => !['pool', 'patio'].includes(b.kind));
      if (below.length) {
        const minX = Math.min(...below.map(b => b.x)), maxX = Math.max(...below.map(b => b.x + b.w));
        const minZ = Math.min(...below.map(b => b.z)), maxZ = Math.max(...below.map(b => b.z + b.d));
        const over = Math.max(0, minX - r.x, r.x + r.w - maxX, minZ - r.z, r.z + r.d - maxZ);
        if (over > 0.5) {
          const limit = 2.0;
          const sev = hasBeam ? 'watch' : over > limit ? 'critical' : 'watch';
          out.push({
            roomId: r.id, severity: sev, category: 'cantilever',
            title: `${r.name}: ${round(over, 1)} ft cantilever`,
            detail: hasBeam
              ? `Projects ${round(over, 1)} ft past the wall below on engineered outriggers. Inspect connections & flashing annually.`
              : `Projects ${round(over, 1)} ft past the supporting wall (limit ≈ ${limit} ft for ${bp.floorJoist} joists carrying a bearing wall). Look for floor bounce and sagging at the exterior wall.`,
            codeRef: r.kind === 'deck' ? 'IRC R507.6 (deck joist cantilever)' : 'IRC R502.3.3 (cantilevered floor joists)',
            metric: round(over, 1), limit, fixCost: sev === 'critical' ? 11500 : 900,
          });
        }
      }
    }

    // 5. Lateral bracing at heavily glazed walls
    if (!NON_LIVING.has(r.kind)) {
      const ext = exteriorLength(r, byFloor(r.floor));
      if (ext.longest >= 16 && r.glazing >= 0.55) {
        out.push({
          roomId: r.id, severity: 'watch', category: 'lateral',
          title: `${r.name}: ${Math.round(r.glazing * 100)}% glazed braced wall`,
          detail: `A ${round(ext.longest)} ft exterior wall is ${Math.round(r.glazing * 100)}% glass, leaving little full-height sheathing. Use portal frames / hold-downs (method PFH) to meet braced wall panel requirements.`,
          codeRef: 'IRC R602.10 (wall bracing)', metric: round(r.glazing * 100), limit: 55, fixCost: 2400,
        });
      }
      // 6. Egress
      if ((r.kind === 'bedroom' || r.kind === 'primary') && ext.total < 2) {
        out.push({
          roomId: r.id, severity: 'critical', category: 'egress', title: `${r.name}: no exterior wall for egress`,
          detail: 'Sleeping rooms require an emergency escape opening.', codeRef: 'IRC R310.1', metric: 0, limit: 1, fixCost: 6000,
        });
      }
      // 7. Tall walls
      if (r.ceiling > 12) {
        out.push({
          roomId: r.id, severity: 'watch', category: 'tall_wall', title: `${r.name}: ${r.ceiling} ft tall wall`,
          detail: 'Stud height exceeds 10 ft table limits; specify engineered studs (LSL) or reduce spacing for wind load.',
          codeRef: 'IRC R602.3(5)', metric: r.ceiling, limit: 10, fixCost: 1600,
        });
      }
    }
  }

  // 3. Load-path continuity, aggregated per upper level
  const maxFloor = Math.max(...rooms.map(r => r.floor));
  for (let f = 1; f <= maxFloor; f++) {
    const upper = byFloor(f).filter(r => !NON_LIVING.has(r.kind));
    const below = byFloor(f - 1).filter(b => b.kind !== 'pool');
    if (!upper.length || !below.length) continue;
    let total = 0, supported = 0;
    for (const r of upper) for (const e of edges(r)) {
      total += e.a1 - e.a0;
      let s = 0;
      for (const b of below) for (const be of edges(b)) {
        if (be.horiz === e.horiz && Math.abs(be.c - e.c) < 0.75) s += overlap(e.a0, e.a1, be.a0, be.a1);
      }
      supported += Math.min(s, e.a1 - e.a0);
    }
    const unsupported = total > 0 ? 1 - supported / total : 0;
    const overRemoved = below.some(b => b.flags.includes('removed_wall'));
    if (unsupported > 0.45 || overRemoved) {
      const worst = upper.reduce((a, b) => (a.w * a.d > b.w * b.d ? a : b));
      out.push({
        roomId: worst.id, severity: overRemoved ? 'critical' : 'watch', category: 'load_path',
        title: `Level ${f + 1}: ${Math.round(unsupported * 100)}% of walls not stacked`,
        detail: `${Math.round(unsupported * 100)}% of level-${f + 1} wall length has no wall directly below; bearing loads must transfer through floor framing${overRemoved ? ' — directly over the opened great room, so bedroom loads now sit mid-span' : ''}. Confirm headers, beams and squash blocks.`,
        codeRef: 'IRC R502.4 (joists under bearing partitions)', metric: Math.round(unsupported * 100), limit: 45,
        fixCost: overRemoved ? 0 : 1800,
      });
    }
  }

  // 8. Foundation on expansive soil
  const fnd = comps.find(c => c.kind === 'foundation');
  if (fnd) {
    const sev = fnd.condition < 0.55 && loc.soilShrinkSwell > 0.6 ? 'critical' : loc.soilShrinkSwell > 0.6 ? 'watch' : 'ok';
    const anchor = rooms.find(r => r.floor === 0 && !NON_LIVING.has(r.kind)) ?? rooms[0];
    out.push({
      roomId: anchor.id, severity: sev, category: 'foundation',
      title: sev === 'critical' ? `Foundation: ${fnd.sensorValue} mm slab cracking` : 'Foundation on expansive clay',
      detail: sev === 'critical'
        ? `Crack width ${fnd.sensorValue} mm on high shrink-swell soil (index ${round(loc.soilShrinkSwell, 2)}). Order an elevation survey; budget pressed piers on the low corner.`
        : `Soil shrink-swell index ${round(loc.soilShrinkSwell, 2)}. Maintain perimeter moisture and positive drainage to limit seasonal movement.`,
      codeRef: 'IRC R403.1.8 (expansive soils) / PTI DC10.5', metric: fnd.sensorValue, limit: 3, fixCost: sev === 'critical' ? 18500 : 0,
    });
  }

  // 9. Construction sequencing (construction managers)
  if (bp.stage === 'under_construction') {
    for (const c of comps.filter(c => c.hazardFlag)) {
      const anchor = rooms[0];
      out.push({
        roomId: anchor.id, severity: 'watch', category: 'sequencing', title: `Sequencing: ${c.name}`,
        detail: c.hazardFlag, codeRef: 'Construction schedule / IRC R702.3', metric: 0, limit: 0, fixCost: 0,
      });
    }
  }
  return out;
}

export function integrityScore(f: Finding[]) {
  const crit = f.filter(x => x.severity === 'critical').length;
  const watch = f.filter(x => x.severity === 'watch').length;
  return { score: clamp(100 - crit * 14 - watch * 4, 5, 100), crit, watch, ok: f.filter(x => x.severity === 'ok').length };
}

// ───────────────────────────── Risk & cascade ──────────────────────────────

function hazardMultiplier(kind: string, loc: Loc, hazardFlag: string): number {
  let m = 1;
  switch (kind) {
    case 'roof': m = 1 + 1.2 * loc.hailRisk; break;
    case 'windows': m = 1 + 0.4 * loc.hailRisk; break;
    case 'foundation': m = 1 + 2.5 * loc.soilShrinkSwell; break;
    case 'plumbing': m = 1 + 0.8 * loc.freezeRisk + 0.6 * loc.soilShrinkSwell; break;
    case 'sewer': m = 1 + 0.8 * loc.soilShrinkSwell; break;
    case 'hvac': m = 1 + 0.5 * loc.humidity; break;
    case 'attic_insulation': m = 1 + 0.3 * loc.humidity; break;
    case 'mold': m = 1 + 1.2 * loc.humidity + 0.5 * loc.floodRisk; break;
    case 'gutters': m = 1 + 0.3 * loc.floodRisk; break;
  }
  if (hazardFlag) m *= kind === 'electrical' ? 3 : 1.5;
  return m;
}

export type RiskRow = {
  componentId: number; kind: string; name: string; baseProb: number; cascadeProb: number; expectedLoss: number;
  remainingLife: number; tier: string; drivers: string; cost: number;
};

export function riskModel(comps: Comp[], loc: Loc, year: number, overrides?: Set<number>): { rows: RiskRow[]; total: number } {
  const base = new Map<number, number>();
  const life = new Map<number, number>();
  for (const c of comps) {
    const fixed = overrides?.has(c.id);
    const age = fixed ? 0 : Math.max(0, year - c.installYear);
    const cond = fixed ? 1 : c.condition;
    const L = Math.max(0.5, c.expectedLife);
    const effAge = age + (1 - cond) * L * 0.5;
    const lam = L / 0.893; // Weibull scale so the mean equals expected life (k = 3)
    const H = (t: number) => Math.pow(t / lam, 3);
    let m = hazardMultiplier(c.kind, loc, fixed ? '' : c.hazardFlag);
    // Live telemetry sharpens the prior: sensors past warn/critical thresholds raise the hazard.
    if (!fixed && c.sensorCritical > 0) m *= c.sensorValue >= c.sensorCritical ? 2.5 : c.sensorValue >= c.sensorWarn ? 1.6 : 1;
    const p = clamp(1 - Math.exp(-m * (H(effAge + 1) - H(effAge))), 0.004, 0.97);
    base.set(c.id, p);
    life.set(c.id, Math.max(0, L - effAge));
  }
  const byKind = new Map<string, Comp>();
  for (const c of comps) byKind.set(c.kind, c);
  const cascade = new Map<number, number>();
  const drivers = new Map<number, string[]>();
  for (const kind of KIND_ORDER) {
    const c = byKind.get(kind);
    if (!c) continue;
    let survive = 1 - base.get(c.id)!;
    const ds: string[] = [];
    for (const [from, to, w] of CASCADE_RULES) {
      if (to !== kind) continue;
      const up = byKind.get(from);
      if (!up) continue;
      const pu = cascade.get(up.id) ?? base.get(up.id)!;
      const contrib = pu * w;
      survive *= 1 - contrib;
      if (contrib > 0.02) ds.push(`${KIND_LABEL[from]} ${Math.round(contrib * 100)}%`);
    }
    cascade.set(c.id, 1 - survive);
    drivers.set(c.id, ds);
  }
  let total = 0;
  const rows: RiskRow[] = comps.map(c => {
    const b = base.get(c.id)!;
    const p = cascade.get(c.id) ?? b;
    const sev = c.kind === 'electrical' && c.hazardFlag ? 4 : 1;
    const loss = c.replaceCost * (b * sev + 1.3 * Math.max(0, p - b));
    total += loss;
    const tier = p > 0.5 ? 'severe' : p > 0.3 ? 'high' : p > 0.12 ? 'elevated' : 'low';
    const ds = drivers.get(c.id) ?? [];
    const own = `Age ${round(Math.max(0, year - c.installYear), 1)}/${c.expectedLife} yr, health ${Math.round(c.condition * 100)}%`;
    return {
      componentId: c.id, kind: c.kind, name: c.name, baseProb: b, cascadeProb: p, expectedLoss: loss,
      remainingLife: life.get(c.id)!, tier, cost: c.replaceCost,
      drivers: ds.length ? `${own}; upstream: ${ds.join(', ')}` : own,
    };
  });
  return { rows, total };
}

export function cascadePaths(comps: Comp[], risk: RiskRow[]) {
  const byKind = new Map<string, Comp>();
  for (const c of comps) byKind.set(c.kind, c);
  const riskBy = new Map(risk.map(r => [r.componentId, r]));
  const paths: { ids: number[]; label: string; probability: number; loss: number; mechanism: string }[] = [];
  const walk = (chain: Comp[], prob: number, loss: number, mech: string[]) => {
    const tail = chain[chain.length - 1];
    if (chain.length >= 2) {
      paths.push({
        ids: chain.map(c => c.id), label: chain.map(c => KIND_LABEL[c.kind]).join(' → '),
        probability: prob, loss, mechanism: mech.join(' → '),
      });
    }
    if (chain.length >= 4) return;
    for (const [from, to, w, m] of CASCADE_RULES) {
      if (from !== tail.kind) continue;
      const next = byKind.get(to);
      if (!next || chain.includes(next)) continue;
      const p = prob * w;
      walk([...chain, next], p, loss + p * next.replaceCost * 1.3, [...mech, m]);
    }
  };
  for (const c of comps) {
    const r = riskBy.get(c.id)!;
    walk([c], r.baseProb, 0, []);
  }
  paths.sort((a, b) => b.loss - a.loss);
  const perRoot = new Map<number, number>();
  return paths.filter(p => {
    const n = perRoot.get(p.ids[0]) ?? 0;
    if (n >= 2) return false;
    perRoot.set(p.ids[0], n + 1);
    return true;
  }).slice(0, 7);
}

// ───────────────────────────── Valuation ──────────────────────────────

export function featureDemand(key: string, loc: Loc) {
  const f = FEATURE_CATALOG[key];
  if (!f) return 0;
  return clamp(f.demand + (loc.buyerTags.includes(key) ? 0.3 : 0), 0, 1);
}

export function valuationModel(bp: Bp, rooms: Rm[], comps: Comp[], loc: Loc, findings: Finding[], risk: { rows: RiskRow[]; total: number }, year: number) {
  const sqft = heatedSqft(rooms);
  const marketBase = sqft * loc.medianPpsf;
  const age = Math.max(0, year - bp.yearBuilt);
  const ageAdj = bp.stage === 'under_construction' ? 0.05 : -Math.min(0.08, Math.max(0, age - 5) * 0.003);
  const layoutAdj = clamp((bp.beds - 3) * 0.02 + (bp.baths - 2) * 0.025, -0.06, 0.08);
  let featurePremium = 0;
  let demandSum = 0;
  for (const k of bp.features) {
    const f = FEATURE_CATALOG[k];
    if (!f) continue;
    const d = featureDemand(k, loc);
    demandSum += d;
    featurePremium += marketBase * f.pct * (0.6 + 0.8 * d);
  }
  const existing = bp.stage === 'existing';
  let conditionDiscount = 0, repairCost = 0;
  if (existing) {
    for (const c of comps) {
      const r = risk.rows.find(x => x.componentId === c.id)!;
      const severe = c.condition < 0.4 || r.remainingLife < 1 || (c.hazardFlag && c.kind === 'electrical');
      if (severe || c.condition < 0.5) {
        // Inspection findings become repair credits; buyers over-discount the severe ones.
        conditionDiscount += c.replaceCost * (severe ? 1.0 : 0.4);
        repairCost += c.replaceCost;
      }
    }
  }
  const crit = findings.filter(f => f.severity === 'critical');
  const structuralDiscount = existing ? crit.reduce((a, f) => a + f.fixCost * 1.3, 0) : 0;
  repairCost += existing ? crit.reduce((a, f) => a + f.fixCost, 0) : 0;
  const riskDrag = existing ? 0.25 * risk.total : 0;
  const listPrice = Math.max(0, marketBase * (1 + ageAdj + layoutAdj) + featurePremium - conditionDiscount - structuralDiscount - riskDrag);
  const valueUnlocked = conditionDiscount + structuralDiscount + riskDrag - repairCost;
  const riskShare = (conditionDiscount + structuralDiscount) / Math.max(1, marketBase);
  const avgDemand = bp.features.length ? demandSum / bp.features.length : 0.4;
  const dom = loc.daysOnMarket * (1 + 3.5 * riskShare) * (1 - 0.25 * (avgDemand - 0.5));
  const confidence = clamp(0.9 - crit.length * 0.06 - (existing ? 0 : 0.08), 0.5, 0.92);
  const band = 0.035 + (1 - confidence) * 0.12;
  return {
    sqft, marketBase, ageAdj, layoutAdj, featurePremium, conditionDiscount, structuralDiscount,
    expectedLoss12mo: risk.total, preListingRepairCost: repairCost, valueUnlocked, listPrice,
    lowPrice: listPrice * (1 - band), highPrice: listPrice * (1 + band), ppsf: listPrice / Math.max(1, sqft),
    daysOnMarket: dom, annualCarryCost: listPrice * (loc.propertyTaxRate + 0.009) + risk.total, confidence,
  };
}

// ───────────────────────────── Persist ──────────────────────────────

function loadBlueprint(ctx: Ctx, bpId: number) {
  const bp = ctx.db.blueprint.id.find(bpId);
  if (!bp) return null;
  const loc = ctx.db.location.id.find(bp.locationId);
  if (!loc) return null;
  const rooms = [...ctx.db.room.blueprintId.filter(bpId)] as Rm[];
  const comps = [...ctx.db.component.blueprintId.filter(bpId)] as Comp[];
  return { bp, loc, rooms, comps };
}

export function blueprintRisk(ctx: Ctx, bpId: number) {
  const d = loadBlueprint(ctx, bpId);
  if (!d) return null;
  return { ...d, risk: riskModel(d.comps, d.loc, currentYear(ctx)) };
}

/** Recompute every derived table for one blueprint in the current transaction. */
export function recompute(ctx: Ctx, bpId: number, full = true) {
  const d = loadBlueprint(ctx, bpId);
  if (!d) return;
  const { bp, loc, rooms, comps } = d;
  const year = currentYear(ctx);

  // Structural
  const findings = structuralAnalysis(bp, rooms, comps, loc);
  {
    for (const f of [...ctx.db.structuralFinding.blueprintId.filter(bpId)]) ctx.db.structuralFinding.id.delete(f.id);
    for (const f of findings) ctx.db.structuralFinding.insert({ id: 0n, blueprintId: bpId, ...f });
    const s = integrityScore(findings);
    const worst = findings.find(f => f.severity === 'critical') ?? findings.find(f => f.severity === 'watch');
    const summary = {
      blueprintId: bpId, integrityScore: s.score, criticalCount: s.crit, watchCount: s.watch, okCount: s.ok,
      headline: worst ? worst.title : 'No structural concerns detected',
    };
    if (ctx.db.structuralSummary.blueprintId.find(bpId)) ctx.db.structuralSummary.blueprintId.update(summary);
    else ctx.db.structuralSummary.insert(summary);
  }

  // Risk + cascade
  const risk = riskModel(comps, loc, year);
  const ranked = [...risk.rows].sort((a, b) => b.expectedLoss - a.expectedLoss);
  for (const r of [...ctx.db.componentRisk.blueprintId.filter(bpId)]) ctx.db.componentRisk.componentId.delete(r.componentId);
  ranked.forEach((r, i) => {
    const counter = riskModel(comps, loc, year, new Set([r.componentId]));
    ctx.db.componentRisk.insert({
      componentId: r.componentId, blueprintId: bpId, baseProb: r.baseProb, cascadeProb: r.cascadeProb,
      expectedLoss: r.expectedLoss, preventionValue: Math.max(0, risk.total - counter.total),
      remainingLife: r.remainingLife, tier: r.tier, rank: i + 1, drivers: r.drivers,
    });
  });
  for (const p of [...ctx.db.cascadePath.blueprintId.filter(bpId)]) ctx.db.cascadePath.id.delete(p.id);
  for (const p of cascadePaths(comps, risk.rows)) {
    ctx.db.cascadePath.insert({ id: 0n, blueprintId: bpId, componentIds: p.ids, label: p.label, probability: p.probability, loss: p.loss, mechanism: p.mechanism });
  }

  // Valuation
  const v = valuationModel(bp, rooms, comps, loc, findings, risk, year);
  const vrow = {
    blueprintId: bpId, listPrice: v.listPrice, lowPrice: v.lowPrice, highPrice: v.highPrice, ppsf: v.ppsf,
    marketBase: v.marketBase, ageAdj: v.ageAdj, layoutAdj: v.layoutAdj, featurePremium: v.featurePremium,
    conditionDiscount: v.conditionDiscount, structuralDiscount: v.structuralDiscount, expectedLoss12mo: v.expectedLoss12mo,
    preListingRepairCost: v.preListingRepairCost, valueUnlocked: v.valueUnlocked, daysOnMarket: v.daysOnMarket,
    annualCarryCost: v.annualCarryCost, confidence: v.confidence,
  };
  if (ctx.db.valuation.blueprintId.find(bpId)) ctx.db.valuation.blueprintId.update(vrow);
  else ctx.db.valuation.insert(vrow);

  if (!full) return;

  // Same model, every market
  for (const p of [...ctx.db.locationPricing.blueprintId.filter(bpId)]) ctx.db.locationPricing.id.delete(p.id);
  for (const l of ctx.db.location.iter()) {
    const lr = riskModel(comps, l, year);
    const lf = structuralAnalysis(bp, rooms, comps, l);
    const lv = valuationModel(bp, rooms, comps, l, lf, lr, year);
    const hazards: [string, number][] = [['Hail', l.hailRisk], ['Expansive soil', l.soilShrinkSwell], ['Flood', l.floodRisk], ['Freeze', l.freezeRisk]];
    hazards.sort((a, b) => b[1] - a[1]);
    ctx.db.locationPricing.insert({
      id: 0n, blueprintId: bpId, locationId: l.id, locationName: l.name, listPrice: lv.listPrice, ppsf: lv.ppsf,
      riskLoss12mo: lr.total, daysOnMarket: lv.daysOnMarket, topHazard: hazards[0][0],
    });
  }

  // Selling features (present + high-demand gaps)
  for (const f of [...ctx.db.sellingFeature.blueprintId.filter(bpId)]) ctx.db.sellingFeature.id.delete(f.id);
  const keys = new Set([...bp.features, ...loc.buyerTags]);
  for (const key of keys) {
    const f = FEATURE_CATALOG[key];
    if (!f) continue;
    const demand = featureDemand(key, loc);
    const present = bp.features.includes(key);
    ctx.db.sellingFeature.insert({
      id: 0n, blueprintId: bpId, key, name: f.name, present, demandScore: demand,
      valueImpact: v.marketBase * f.pct * (0.6 + 0.8 * demand),
      pitch: present ? f.pitch : `Missing — ${Math.round(demand * 100)}% local demand. ${f.pitch}`,
    });
  }

  // Upgrade / repair recommendations
  for (const u of [...ctx.db.upgradeRec.blueprintId.filter(bpId)]) ctx.db.upgradeRec.id.delete(u.id);
  const riskRows = [...ctx.db.componentRisk.blueprintId.filter(bpId)];
  for (const r of riskRows.sort((a, b) => b.preventionValue - a.preventionValue).slice(0, 4)) {
    const c = comps.find(c => c.id === r.componentId)!;
    const listingRelief = bp.stage === 'existing' && (c.condition < 0.5 || r.remainingLife < 1) ? c.replaceCost * (c.condition < 0.4 ? 1.1 : 0.45) : 0;
    ctx.db.upgradeRec.insert({
      id: 0n, blueprintId: bpId, name: `Replace / repair ${c.name.toLowerCase()}`, kind: 'repair', componentId: c.id,
      cost: c.replaceCost, valueAdd: listingRelief, riskAvoided: r.preventionValue,
      roi: (listingRelief + r.preventionValue) / Math.max(1, c.replaceCost),
      rationale: `Stops ${usd(r.preventionValue)}/yr of expected loss across the failure graph (${r.drivers}).`,
      source: 'Weibull lifecycle (NAHB/InterNACHI) × cascade graph',
    });
  }
  if (bp.stage === 'existing') {
    for (const u of UPGRADE_CATALOG) {
      const boost = u.feature && loc.buyerTags.includes(u.feature) ? 1.25 : 1;
      const valueAdd = u.cost * u.recoup * boost * (loc.medianPpsf / 250);
      ctx.db.upgradeRec.insert({
        id: 0n, blueprintId: bpId, name: u.name, kind: 'upgrade', componentId: 0, cost: u.cost, valueAdd, riskAvoided: 0,
        roi: valueAdd / u.cost, rationale: `Recoups ~${Math.round(u.recoup * 100)}% nationally${boost > 1 ? '; boosted by local buyer demand' : ''}, scaled to local $/sqft.`,
        source: 'Zonda / JLC Cost vs. Value 2024',
      });
    }
  }
  for (const key of loc.buyerTags) {
    const f = FEATURE_CATALOG[key];
    if (!f || !f.addCost || bp.features.includes(key)) continue;
    const valueAdd = v.marketBase * f.pct * (0.6 + 0.8 * featureDemand(key, loc));
    ctx.db.upgradeRec.insert({
      id: 0n, blueprintId: bpId, name: `Add ${f.name.toLowerCase()}`, kind: 'upgrade', componentId: 0, cost: f.addCost, valueAdd,
      riskAvoided: 0, roi: valueAdd / f.addCost, rationale: `High local demand in ${loc.name}. ${f.pitch}`, source: 'NAR / NAHB buyer preference surveys',
    });
  }
}

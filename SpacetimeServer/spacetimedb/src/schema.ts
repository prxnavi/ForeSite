import { schema, table, t, type InferSchema, type ReducerCtx } from 'spacetimedb/server';

// ─────────────────────────────────────────────────────────────────────────────
// Identity & collaboration
// ─────────────────────────────────────────────────────────────────────────────

export const userProfile = table(
  { name: 'user_profile', public: true },
  {
    identity: t.identity().primaryKey(),
    name: t.string(),
    role: t.string(), // 'construction_manager' | 'real_estate_agent'
    company: t.string(),
    color: t.string(), // hex color for avatar/presence
    online: t.bool(),
    selectedBlueprint: t.u32(),
    lastSeen: t.timestamp(),
  }
);

// Live multi-user walkthrough: every connected user's camera pose.
export const presence = table(
  { name: 'presence', public: true },
  {
    identity: t.identity().primaryKey(),
    blueprintId: t.u32().index('btree'),
    name: t.string(),
    color: t.string(),
    x: t.f32(),
    y: t.f32(),
    z: t.f32(),
    yaw: t.f32(),
    updatedAt: t.timestamp(),
  }
);

// Shared 3D issue pins dropped during a walkthrough.
export const annotation = table(
  { name: 'annotation', public: true },
  {
    id: t.u64().primaryKey().autoInc(),
    blueprintId: t.u32().index('btree'),
    authorName: t.string(),
    color: t.string(),
    x: t.f32(),
    y: t.f32(),
    z: t.f32(),
    text: t.string(),
    at: t.timestamp(),
  }
);

// ─────────────────────────────────────────────────────────────────────────────
// Fragmented real-estate data, unified (location, plans, systems, rules)
// ─────────────────────────────────────────────────────────────────────────────

export const location = table(
  { name: 'location', public: true },
  {
    id: t.u32().primaryKey(),
    name: t.string(),
    city: t.string(),
    state: t.string(),
    medianPpsf: t.f64(),
    yoyAppreciation: t.f64(),
    daysOnMarket: t.u32(),
    schoolRating: t.f64(),
    hailRisk: t.f64(),
    soilShrinkSwell: t.f64(),
    floodRisk: t.f64(),
    freezeRisk: t.f64(),
    humidity: t.f64(),
    walkScore: t.u32(),
    propertyTaxRate: t.f64(),
    buyerTags: t.array(t.string()),
    dataSource: t.string(),
  }
);

export const blueprint = table(
  { name: 'blueprint', public: true },
  {
    id: t.u32().primaryKey(),
    name: t.string(),
    tagline: t.string(),
    locationId: t.u32(),
    style: t.string(),
    stage: t.string(), // 'existing' | 'under_construction'
    yearBuilt: t.u32(),
    floors: t.u32(),
    sqft: t.f64(),
    beds: t.u32(),
    baths: t.f64(),
    lotSqft: t.f64(),
    foundation: t.string(),
    floorJoist: t.string(), // e.g. '2x10'
    ceilingJoist: t.string(), // e.g. '2x6' | 'truss'
    roofType: t.string(),
    features: t.array(t.string()),
    description: t.string(),
    accentColor: t.string(),
  }
);

export const room = table(
  { name: 'room', public: true },
  {
    id: t.u32().primaryKey(),
    blueprintId: t.u32().index('btree'),
    name: t.string(),
    kind: t.string(),
    floor: t.u32(),
    x: t.f64(), // feet, plan coordinates
    z: t.f64(),
    w: t.f64(),
    d: t.f64(),
    ceiling: t.f64(),
    glazing: t.f64(), // 0..1 fraction of exterior wall that is glass
    flags: t.array(t.string()), // e.g. 'beam', 'vaulted', 'removed_wall'
  }
);

// Building systems with live IoT-style sensor channel.
export const component = table(
  { name: 'component', public: true },
  {
    id: t.u32().primaryKey(),
    blueprintId: t.u32().index('btree'),
    kind: t.string(),
    name: t.string(),
    material: t.string(),
    roomId: t.u32(),
    ox: t.f64(), // offset within room (0..1)
    oz: t.f64(),
    oy: t.f64(), // height in feet
    installYear: t.f64(),
    expectedLife: t.f64(),
    replaceCost: t.f64(),
    condition: t.f64(), // 0..1 health
    hazardFlag: t.string(), // e.g. 'FPE Stab-Lok panel', '' if none
    sensorLabel: t.string(),
    sensorValue: t.f64(),
    sensorUnit: t.string(),
    sensorWarn: t.f64(),
    sensorCritical: t.f64(),
  }
);

// Failure-propagation knowledge graph (edges between system kinds).
export const cascadeRule = table(
  { name: 'cascade_rule', public: true },
  {
    id: t.u32().primaryKey(),
    fromKind: t.string(),
    toKind: t.string(),
    weight: t.f64(),
    mechanism: t.string(),
    source: t.string(),
  }
);

export const dataSource = table(
  { name: 'data_source', public: true },
  {
    id: t.u32().primaryKey(),
    domain: t.string(),
    name: t.string(),
    usage: t.string(),
    url: t.string(),
  }
);

// ─────────────────────────────────────────────────────────────────────────────
// Derived intelligence (recomputed transactionally inside the database)
// ─────────────────────────────────────────────────────────────────────────────

export const structuralFinding = table(
  { name: 'structural_finding', public: true },
  {
    id: t.u64().primaryKey().autoInc(),
    blueprintId: t.u32().index('btree'),
    roomId: t.u32(),
    severity: t.string(), // 'ok' | 'watch' | 'critical'
    category: t.string(),
    title: t.string(),
    detail: t.string(),
    codeRef: t.string(),
    metric: t.f64(),
    limit: t.f64(),
    fixCost: t.f64(),
  }
);

export const structuralSummary = table(
  { name: 'structural_summary', public: true },
  {
    blueprintId: t.u32().primaryKey(),
    integrityScore: t.f64(),
    criticalCount: t.u32(),
    watchCount: t.u32(),
    okCount: t.u32(),
    headline: t.string(),
  }
);

export const componentRisk = table(
  { name: 'component_risk', public: true },
  {
    componentId: t.u32().primaryKey(),
    blueprintId: t.u32().index('btree'),
    baseProb: t.f64(), // 12-month standalone failure probability
    cascadeProb: t.f64(), // after upstream propagation
    expectedLoss: t.f64(),
    preventionValue: t.f64(), // loss avoided across the graph if fixed now
    remainingLife: t.f64(),
    tier: t.string(), // 'low' | 'elevated' | 'high' | 'severe'
    rank: t.u32(),
    drivers: t.string(),
  }
);

export const cascadePath = table(
  { name: 'cascade_path', public: true },
  {
    id: t.u64().primaryKey().autoInc(),
    blueprintId: t.u32().index('btree'),
    componentIds: t.array(t.u32()),
    label: t.string(),
    probability: t.f64(),
    loss: t.f64(),
    mechanism: t.string(),
  }
);

export const valuation = table(
  { name: 'valuation', public: true },
  {
    blueprintId: t.u32().primaryKey(),
    listPrice: t.f64(),
    lowPrice: t.f64(),
    highPrice: t.f64(),
    ppsf: t.f64(),
    marketBase: t.f64(),
    ageAdj: t.f64(),
    layoutAdj: t.f64(),
    featurePremium: t.f64(),
    conditionDiscount: t.f64(),
    structuralDiscount: t.f64(),
    expectedLoss12mo: t.f64(),
    preListingRepairCost: t.f64(),
    valueUnlocked: t.f64(),
    daysOnMarket: t.f64(),
    annualCarryCost: t.f64(),
    confidence: t.f64(),
  }
);

export const locationPricing = table(
  { name: 'location_pricing', public: true },
  {
    id: t.u64().primaryKey().autoInc(),
    blueprintId: t.u32().index('btree'),
    locationId: t.u32(),
    locationName: t.string(),
    listPrice: t.f64(),
    ppsf: t.f64(),
    riskLoss12mo: t.f64(),
    daysOnMarket: t.f64(),
    topHazard: t.string(),
  }
);

export const sellingFeature = table(
  { name: 'selling_feature', public: true },
  {
    id: t.u64().primaryKey().autoInc(),
    blueprintId: t.u32().index('btree'),
    key: t.string(),
    name: t.string(),
    present: t.bool(),
    demandScore: t.f64(), // 0..1 local buyer demand
    valueImpact: t.f64(), // $
    pitch: t.string(),
  }
);

export const upgradeRec = table(
  { name: 'upgrade_rec', public: true },
  {
    id: t.u64().primaryKey().autoInc(),
    blueprintId: t.u32().index('btree'),
    name: t.string(),
    kind: t.string(), // 'repair' | 'upgrade'
    componentId: t.u32(),
    cost: t.f64(),
    valueAdd: t.f64(),
    riskAvoided: t.f64(),
    roi: t.f64(),
    rationale: t.string(),
    source: t.string(),
  }
);

// ─────────────────────────────────────────────────────────────────────────────
// Execution: work orders, alerts, live simulation
// ─────────────────────────────────────────────────────────────────────────────

export const workOrder = table(
  { name: 'work_order', public: true },
  {
    id: t.u64().primaryKey().autoInc(),
    blueprintId: t.u32().index('btree'),
    componentId: t.u32(),
    title: t.string(),
    action: t.string(),
    cost: t.f64(),
    status: t.string(), // 'dispatched' | 'in_progress' | 'done'
    createdByName: t.string(),
    source: t.string(), // 'agent' | 'user'
    lossAvoided: t.f64(),
    createdAt: t.timestamp(),
  }
);

export const alert = table(
  { name: 'alert', public: true },
  {
    id: t.u64().primaryKey().autoInc(),
    blueprintId: t.u32().index('btree'),
    componentId: t.u32(),
    level: t.string(), // 'info' | 'warn' | 'critical'
    message: t.string(),
    simMonth: t.u32(),
    at: t.timestamp(),
  }
);

export const simulationState = table(
  { name: 'simulation_state', public: true },
  {
    id: t.u32().primaryKey(),
    running: t.bool(),
    simMonths: t.u32(),
    baseYear: t.f64(),
    speedMs: t.u32(),
  }
);

export const simTimer = table(
  { name: 'sim_timer' },
  {
    scheduledId: t.u64().primaryKey().autoInc(),
    scheduledAt: t.scheduleAt(),
  }
);

// ─────────────────────────────────────────────────────────────────────────────
// Agentic workflow (Nemotron) — every step is a row clients watch live
// ─────────────────────────────────────────────────────────────────────────────

export const agentRun = table(
  { name: 'agent_run', public: true },
  {
    id: t.u64().primaryKey().autoInc(),
    blueprintId: t.u32().index('btree'),
    requesterName: t.string(),
    prompt: t.string(),
    status: t.string(), // 'running' | 'done' | 'error'
    model: t.string(),
    usedLlm: t.bool(),
    startedAt: t.timestamp(),
  }
);

export const agentStep = table(
  { name: 'agent_step', public: true },
  {
    id: t.u64().primaryKey().autoInc(),
    runId: t.u64().index('btree'),
    blueprintId: t.u32(),
    agent: t.string(), // 'orchestrator' | 'structural' | 'operations' | 'market'
    kind: t.string(), // 'plan' | 'tool_call' | 'tool_result' | 'handoff' | 'action' | 'final' | 'error'
    title: t.string(),
    detail: t.string(),
    at: t.timestamp(),
  }
);

export const agentMessage = table(
  { name: 'agent_message', public: true },
  {
    id: t.u64().primaryKey().autoInc(),
    blueprintId: t.u32().index('btree'),
    runId: t.u64(),
    role: t.string(), // 'user' | 'assistant'
    agent: t.string(),
    authorName: t.string(),
    content: t.string(),
    at: t.timestamp(),
  }
);

const spacetimedb = schema(
  {
    userProfile,
    presence,
    annotation,
    location,
    blueprint,
    room,
    component,
    cascadeRule,
    dataSource,
    structuralFinding,
    structuralSummary,
    componentRisk,
    cascadePath,
    valuation,
    locationPricing,
    sellingFeature,
    upgradeRec,
    workOrder,
    alert,
    simulationState,
    simTimer,
    agentRun,
    agentStep,
    agentMessage,
  },
  {
    // Secrets live in the database environment, never in the client.
    env: {
      NVIDIA_API_KEY: t.option(t.string()),
      NEMOTRON_MODEL: t.option(t.string()),
      NEMOTRON_SPECIALIST_MODEL: t.option(t.string()),
    },
  }
);

export default spacetimedb;
export type Ctx = ReducerCtx<InferSchema<typeof spacetimedb>>;

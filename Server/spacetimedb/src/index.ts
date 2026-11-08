// Atlas — SpacetimeDB module (the whole backend).
// Headsets and the agent are both clients of this module: they read the same
// tables and call the same reducers. Graph analysis (trace path, blast radius),
// the agent task queue and scene-command validation all run here.

import { schema, table, t, SenderError, type InferSchema, type ReducerCtx } from 'spacetimedb/server';
import { ScheduleAt } from 'spacetimedb';

// ---------------------------------------------------------------- tables

const node = table(
  { name: 'node', public: true },
  {
    id: t.string().primaryKey(), // "PAYPOST"
    title: t.string(),
    feature: t.string(), // business feature, used by set_layer business
    kind: t.string(), // Program | Batch | DataStore | External
    status: t.string(), // NotExplored | Understood | Risk | External
    tag: t.string(),
    file: t.string(),
    codeX: t.f32(), // code-layer layout on the table, meters
    codeZ: t.f32(),
    engineerSummary: t.string(),
    pmSummary: t.string(),
    architectSummary: t.string(),
  }
);

const edge = table(
  { name: 'edge', public: true },
  {
    id: t.u64().primaryKey().autoInc(),
    fromNode: t.string(),
    toNode: t.string(),
    label: t.string(),
  }
);

const participant = table(
  { name: 'participant', public: true },
  {
    identity: t.identity().primaryKey(),
    name: t.string(),
    role: t.string(), // Engineer | ProductManager | Architect
    selectedNode: t.string(), // "" = nothing selected
    online: t.bool(),
  }
);

const nodePose = table(
  { name: 'node_pose', public: true },
  {
    nodeId: t.string().primaryKey(),
    offsetX: t.f32(),
    offsetZ: t.f32(),
    held: t.bool(),
    heldBy: t.identity(),
  }
);

const sceneCommand = table(
  { name: 'scene_command', public: true },
  {
    id: t.u64().primaryKey().autoInc(),
    payload: t.string(), // {"kind":"highlight_path","nodes":[...]}
    issuedBy: t.identity(),
    issuedAt: t.timestamp(),
  }
);

const agentTask = table(
  { name: 'agent_task', public: true },
  {
    id: t.u64().primaryKey().autoInc(),
    question: t.string(),
    askedBy: t.identity(),
    askedByName: t.string(),
    askedRole: t.string(),
    state: t.string(), // thinking | done
    answer: t.string(),
    createdAt: t.timestamp(),
  }
);

const agentStep = table(
  { name: 'agent_step', public: true },
  {
    id: t.u64().primaryKey().autoInc(),
    taskId: t.u64().index('btree'),
    stepIndex: t.u32(),
    phase: t.string(), // Understand | Plan | Investigate | Check | Respond
    text: t.string(),
    done: t.bool(),
  }
);

// Private: the built-in agent's plan for a task, revealed step by step.
const agentPlan = table(
  { name: 'agent_plan' },
  {
    taskId: t.u64().primaryKey(),
    stepsJson: t.string(),
    commandsJson: t.string(),
    answer: t.string(),
    cursor: t.u32(),
  }
);

const agentTick = table(
  { name: 'agent_tick' },
  {
    scheduledId: t.u64().primaryKey().autoInc(),
    scheduledAt: t.scheduleAt(),
    taskId: t.u64(),
  }
);

const spacetimedb = schema({ node, edge, participant, nodePose, sceneCommand, agentTask, agentStep, agentPlan, agentTick });
export default spacetimedb;

type Ctx = ReducerCtx<InferSchema<typeof spacetimedb>>;

// ---------------------------------------------------------------- demo graph

const C0 = -0.36, C1 = -0.12, C2 = 0.12, C3 = 0.36;
const BACK = 0.17, MID = 0, FRONT = -0.17;

type NodeSeed = [string, string, string, string, string, string, string, number, number, string, string, string];

const NODES: NodeSeed[] = [
  ['SETTLE01', 'Batch settlement', 'Settle & post', 'Batch', 'NotExplored', 'nightly batch', 'SETTLE01.jcl', C0, BACK,
    "Nightly JCL job that replays the day's queued payments through PAYENTRY. Shares the payment-reference duplicate guard with the online path.",
    "Runs every night to settle the day's payments with the bank.",
    'Second entry into the payment core. Any new boundary must keep batch and API sharing the same idempotency key.'],
  ['PAYENTRY', 'Payment entry', 'Take payments', 'Program', 'Understood', 'input', 'PAYENTRY.cbl', C0, MID,
    'Online entry point. Reads the payment request from the CICS screen, builds PAYMENT-RECORD and hands it to PAYVALID. Nothing is written here.',
    "Where a customer's payment first arrives, from the website or a branch teller. Nothing is charged yet.",
    'Thin adapter between CICS and the payment core. Safe to replace with an API gateway without touching the core.'],
  ['PAYVALID', 'Validate payment', 'Take payments', 'Program', 'Understood', 'rules', 'PAYVALID.cbl', C1, MID,
    'Checks amount, account status and card limits by reading CUSTFILE, then sets PAYMENT-VALID and calls PAYPOST. Pure rules, easy to unit test.',
    'Makes sure a payment is allowed: the account is open, the amount is sensible, the card is under its limit.',
    'Mostly stateless rules with one read dependency. The cleanest extraction candidate in the payment path.'],
  ['PAYPOST', 'Post payment', 'Take payments', 'Program', 'Risk', 'elevated risk', 'PAYPOST.cbl', C2, MID,
    'Checks the duplicate flag, calls FRAUDCHK, then writes to LEDGER01. A shared WS-DUPLICATE flag crosses paragraphs, and batch and online both enter here.',
    'The moment a payment becomes real. If this goes wrong, a customer can be charged twice or not at all.',
    'The coupling hotspot: online and batch converge here, with an external fraud call before the ledger write. Change it last, behind a test harness.'],
  ['FRAUDCHK', 'Fraud gateway', 'Fraud protection', 'External', 'External', 'external · inspect next', 'vendor API', C2, BACK,
    'Synchronous CALL to the vendor fraud API. Times out after 3 seconds; on timeout PAYPOST holds the payment for review.',
    'An outside service that flags suspicious payments before we record them.',
    "External dependency on the critical path. Its latency and outages set PAYPOST's availability."],
  ['CUSTFILE', 'Customer file', 'Customer accounts', 'DataStore', 'NotExplored', 'shared data', 'CUSTFILE (VSAM)', C1, FRONT,
    'VSAM KSDS keyed by customer id. Read by PAYVALID; the record layout lives in CUSTREC.cpy.',
    'Who our customers are: names, addresses and account status.',
    'Shared data contract. Changing its layout touches every program that copies CUSTREC.cpy.'],
  ['LEDGER01', 'Ledger writer', 'Settle & post', 'Program', 'Risk', 'high complexity', 'LEDGER01.cbl', C2, FRONT,
    'Writes double-entry postings and updates ACCTFILE balances. About 1,800 lines with three GO TO loops; the riskiest program to change.',
    'Keeps the official record of every payment and updates customer balances.',
    'System of record. Keep its write contract stable; wrap it rather than rewrite it.'],
  ['ACCTFILE', 'Account master', 'Customer accounts', 'DataStore', 'NotExplored', 'balances', 'ACCTFILE (VSAM)', C3, FRONT,
    'VSAM file holding balances and credit limits. Written by LEDGER01, read by STMTGEN.',
    "Each customer's current balance and credit limit.",
    'Hub data store: every feature that reads balances is coupled through this file.'],
  ['STMTGEN', 'Statements', 'Billing', 'Batch', 'NotExplored', 'monthly batch', 'STMTGEN.cbl', C3, MID,
    'Monthly batch that reads ACCTFILE and the interest results to print customer statements.',
    'Produces the monthly bill each customer receives.',
    'Read-only consumer of balances. A good first service to extract for billing.'],
  ['INTCALC', 'Interest calc', 'Billing', 'Program', 'NotExplored', 'rules', 'INTCALC.cbl', C3, BACK,
    'Computes daily interest per account; results feed STMTGEN.',
    'Works out how much interest each customer owes.',
    'Pure calculation; easy to port once its inputs are made explicit.'],
];

const EDGES: [string, string, string][] = [
  ['SETTLE01', 'PAYENTRY', 'replays'],
  ['PAYENTRY', 'PAYVALID', 'validates'],
  ['PAYVALID', 'PAYPOST', 'calls'],
  ['PAYVALID', 'CUSTFILE', 'reads'],
  ['PAYPOST', 'FRAUDCHK', 'screens'],
  ['PAYPOST', 'LEDGER01', 'writes'],
  ['LEDGER01', 'ACCTFILE', 'updates'],
  ['INTCALC', 'STMTGEN', 'feeds'],
  ['STMTGEN', 'ACCTFILE', 'reads'],
];

function seedGraph(ctx: Ctx) {
  for (const n of [...ctx.db.node.iter()]) ctx.db.node.id.delete(n.id);
  for (const e of [...ctx.db.edge.iter()]) ctx.db.edge.id.delete(e.id);
  for (const p of [...ctx.db.nodePose.iter()]) ctx.db.nodePose.nodeId.delete(p.nodeId);
  for (const [id, title, feature, kind, status, tag, file, codeX, codeZ, eng, pm, arch] of NODES) {
    ctx.db.node.insert({ id, title, feature, kind, status, tag, file, codeX, codeZ,
      engineerSummary: eng, pmSummary: pm, architectSummary: arch });
  }
  for (const [fromNode, toNode, label] of EDGES) ctx.db.edge.insert({ id: 0n, fromNode, toNode, label });
}

// ---------------------------------------------------------------- graph analysis

function edges(ctx: Ctx) {
  return [...ctx.db.edge.iter()];
}

/** Shortest directed path between two pieces (BFS over call/data edges). */
function tracePath(ctx: Ctx, from: string, to: string): string[] {
  const all = edges(ctx);
  const prev = new Map<string, string | null>([[from, null]]);
  const q = [from];
  while (q.length) {
    const cur = q.shift()!;
    if (cur === to) break;
    for (const e of all) if (e.fromNode === cur && !prev.has(e.toNode)) { prev.set(e.toNode, cur); q.push(e.toNode); }
  }
  if (!prev.has(to)) return [];
  const path: string[] = [];
  for (let c: string | null = to; c !== null; c = prev.get(c) ?? null) path.push(c);
  return path.reverse();
}

/** Everything that depends on a piece: reverse BFS with hop distance. */
function blastRadius(ctx: Ctx, id: string): { id: string; depth: number }[] {
  const all = edges(ctx);
  const depth = new Map<string, number>([[id, 0]]);
  const q = [id];
  while (q.length) {
    const cur = q.shift()!;
    for (const e of all) if (e.toNode === cur && !depth.has(e.fromNode)) { depth.set(e.fromNode, depth.get(cur)! + 1); q.push(e.fromNode); }
  }
  return [...depth.entries()].filter(([k]) => k !== id).map(([k, d]) => ({ id: k, depth: d })).sort((a, b) => a.depth - b.depth);
}

type Command = { kind: string; nodes?: string[]; levels?: number[]; origin?: string; node?: string; layer?: string; color?: string };

function blastCommand(ctx: Ctx, id: string): Command {
  const br = blastRadius(ctx, id);
  return { kind: 'show_blast_radius', origin: id, nodes: br.map(b => b.id), levels: br.map(b => Math.min(b.depth, 3)) };
}

const KNOWN_KINDS = new Set(['highlight_nodes', 'highlight_path', 'animate_flow', 'show_blast_radius', 'set_layer', 'focus_node', 'show_ghost_architecture', 'clear']);
const MAX_COMMANDS = 30;

/** validate_scene_command: every named piece must exist before headsets render it. */
function emitCommand(ctx: Ctx, cmd: Command) {
  if (!KNOWN_KINDS.has(cmd.kind)) throw new SenderError(`unknown scene command '${cmd.kind}'`);
  const named = [...(cmd.nodes ?? []), ...(cmd.origin ? [cmd.origin] : []), ...(cmd.node ? [cmd.node] : [])];
  const missing = named.filter(n => !ctx.db.node.id.find(n));
  if (missing.length) throw new SenderError(`scene command names unknown pieces: ${missing.join(', ')}`);

  ctx.db.sceneCommand.insert({ id: 0n, payload: JSON.stringify(cmd), issuedBy: ctx.sender, issuedAt: ctx.timestamp });

  // Late joiners replay the remaining commands in order, so keep the log short.
  const rows = [...ctx.db.sceneCommand.iter()].sort((a, b) => (a.id < b.id ? -1 : 1));
  for (const r of rows.slice(0, Math.max(0, rows.length - MAX_COMMANDS))) ctx.db.sceneCommand.id.delete(r.id);
}

// ---------------------------------------------------------------- built-in agent
// Deterministic stand-in for the Nemotron agent, following the spec's five-step
// loop and answering only with facts computed from the graph. A Nemotron
// worker (or procedure) can take over by writing agent_step rows itself.

type Plan = { steps: [string, string][]; commands: Command[]; answer: string };

const ROLE_SHORT: Record<string, string> = { Engineer: 'Engineer', ProductManager: 'PM', Architect: 'Architect' };

function joinWords(items: string[]): string {
  if (items.length === 0) return 'nothing';
  if (items.length === 1) return items[0];
  return items.slice(0, -1).join(', ') + ' and ' + items[items.length - 1];
}

function planFor(ctx: Ctx, question: string, role: string, selected: string): Plan {
  const q = question.toLowerCase();
  const has = (...w: string[]) => w.some(x => q.includes(x));
  const nodes = [...ctx.db.node.iter()];
  const mentioned = [...nodes].sort((a, b) => b.title.length - a.title.length)
    .find(n => q.includes(n.id.toLowerCase()) || q.includes(n.title.toLowerCase()));
  const target = mentioned?.id ?? (selected || 'PAYPOST');
  const who = ROLE_SHORT[role] ?? role;

  if (has('reset', 'clear')) {
    return { steps: [['Understand', 'Reset requested.'], ['Respond', 'clear']], commands: [{ kind: 'clear' }], answer: 'Back to the full map.' };
  }

  const explain = (id: string): Plan => {
    const n = ctx.db.node.id.find(id)!;
    return {
      steps: [
        ['Understand', `${who} is asking about ${n.id}.`],
        ['Investigate', `get_node(${n.id}) → ${n.feature}`],
        ['Investigate', `read_file(${n.file})`],
        ['Check', 'Summary matches the source'],
        ['Respond', `focus_node(${n.id})`],
      ],
      commands: [{ kind: 'focus_node', node: n.id }],
      answer: role === 'ProductManager' ? n.pmSummary : role === 'Architect' ? n.architectSummary : n.engineerSummary,
    };
  };

  if (has('tell me more')) return explain(target);

  if (has('break', 'blast', 'impact', 'depend', 'change')) {
    const n = ctx.db.node.id.find(target)!;
    const br = blastRadius(ctx, target);
    const direct = br.filter(b => b.depth === 1).map(b => b.id);
    const features = [...new Set([...br.map(b => ctx.db.node.id.find(b.id)!.feature), n.feature])];
    let answer: string;
    if (br.length === 0) answer = `Nothing calls ${n.title}, so changes stay local. Check what it writes to before shipping.`;
    else if (role === 'ProductManager') answer = `Changing ${n.title} would affect ${joinWords(features)}, so every customer who uses them.`;
    else if (role === 'Architect') answer = `${n.title} has ${br.length} dependents across ${features.length} features. Put a boundary in front of it before refactoring.`;
    else answer = `${br.length} dependents. Direct: ${joinWords(direct)}. Add a regression test around ${n.file} before you change it.`;
    return {
      steps: [
        ['Understand', `${who} wants to know what depends on ${n.id}.`],
        ['Plan', 'Walk callers upstream and rank them by distance.'],
        ['Investigate', `get_neighbors(${n.id}) → ${direct.length} direct caller(s)`],
        ['Investigate', `blast_radius(${n.id}) → ${br.length} dependent piece(s)`],
        ['Check', 'Every dependent verified in the graph'],
        ['Respond', 'show_blast_radius'],
      ],
      commands: [blastCommand(ctx, target)],
      answer,
    };
  }

  const layerPlan = (layer: string): Plan => {
    const features = [...new Set(nodes.map(n => n.feature))];
    return {
      steps: [
        ['Understand', `${who} wants the ${layer} view of the system.`],
        ['Plan', layer === 'business' ? 'Group every program by the business feature it serves.' : 'Lay pieces out by call structure.'],
        ['Investigate', `get_node × ${nodes.length} → ${features.length} features`],
        ['Check', 'Every piece maps to exactly one feature'],
        ['Respond', `set_layer(${layer})`],
      ],
      commands: [{ kind: 'clear' }, { kind: 'set_layer', layer }],
      answer: layer === 'code'
        ? 'Back to the code view: programs, batch jobs and data stores.'
        : role === 'ProductManager'
          ? `It does ${features.length} things for customers: ${joinWords(features.map(f => f.toLowerCase()))}.`
          : `Regrouped into ${features.length} business features. Payments and billing meet at the account files.`,
    };
  };

  if (has('customer', 'business', 'feature', 'product')) return layerPlan('business');
  if (has('code view', 'engineer view', 'programs')) return layerPlan('code');

  if (has('payment') && has('process', 'flow', 'work', 'trace', 'how')) {
    const path = tracePath(ctx, 'PAYENTRY', 'ACCTFILE');
    return {
      steps: [
        ['Understand', `${who} asked how a payment is processed.`],
        ['Plan', 'Find where payments start, then follow the calls to storage.'],
        ['Investigate', 'search_code("PAYMENT-RECORD") → entry point PAYENTRY.cbl'],
        ['Investigate', `trace_path(PAYENTRY → ACCTFILE) → ${path.length} pieces`],
        ['Check', `Verified ${path.length} pieces and ${path.length - 1} connections exist in the graph`],
        ['Respond', 'highlight_path + animate_flow'],
      ],
      commands: [{ kind: 'highlight_path', nodes: path }, { kind: 'animate_flow', nodes: path }],
      answer: role === 'ProductManager'
        ? 'When a customer pays, we check the payment is allowed, screen it for fraud, then record it so their balance updates.'
        : role === 'Architect'
          ? 'One linear chain with an external fraud call. PAYPOST is the coupling point: online and batch both converge there before the ledger write.'
          : 'PAYENTRY builds the record, PAYVALID checks it against CUSTFILE, PAYPOST screens it with FRAUDCHK and calls LEDGER01, which updates ACCTFILE.',
    };
  }

  if (has('explain', 'what does', 'what is', 'where')) return explain(target);

  return {
    steps: [['Understand', `${who} asked something I can't map to the graph yet.`], ['Respond', 'No scene change']],
    commands: [],
    answer: 'I can trace a flow, show what breaks if something changes, or regroup the map by business feature.',
  };
}

const STEP_MICROS = 600_000n; // how long each step "works" before it is marked done
const GAP_MICROS = 120_000n;

function scheduleTick(ctx: Ctx, taskId: bigint, delayMicros: bigint) {
  ctx.db.agentTick.insert({
    scheduledId: 0n,
    scheduledAt: ScheduleAt.time(ctx.timestamp.microsSinceUnixEpoch + delayMicros),
    taskId,
  });
}

const MAX_TASKS = 10;

function pruneTasks(ctx: Ctx) {
  const tasks = [...ctx.db.agentTask.iter()].sort((a, b) => (a.id < b.id ? -1 : 1));
  for (const old of tasks.slice(0, Math.max(0, tasks.length - MAX_TASKS))) {
    for (const s of [...ctx.db.agentStep.taskId.filter(old.id)]) ctx.db.agentStep.id.delete(s.id);
    ctx.db.agentPlan.taskId.delete(old.id);
    ctx.db.agentTask.id.delete(old.id);
  }
}

export const runAgentTick = spacetimedb.reducer(
  { onSchedule: agentTick },
  { timer: agentTick.rowType },
  (ctx, { timer }) => {
    const plan = ctx.db.agentPlan.taskId.find(timer.taskId);
    const task = ctx.db.agentTask.id.find(timer.taskId);
    if (!plan || !task) return;

    const steps = JSON.parse(plan.stepsJson) as [string, string][];
    const total = steps.length * 2;
    const c = plan.cursor;

    if (c < total) {
      const i = Math.floor(c / 2);
      if (c % 2 === 0) {
        ctx.db.agentStep.insert({ id: 0n, taskId: task.id, stepIndex: i, phase: steps[i][0], text: steps[i][1], done: false });
        scheduleTick(ctx, task.id, STEP_MICROS);
      } else {
        const row = [...ctx.db.agentStep.taskId.filter(task.id)].find(s => s.stepIndex === i);
        if (row) ctx.db.agentStep.id.update({ ...row, done: true });
        scheduleTick(ctx, task.id, GAP_MICROS);
      }
      ctx.db.agentPlan.taskId.update({ ...plan, cursor: c + 1 });
      return;
    }

    for (const cmd of JSON.parse(plan.commandsJson) as Command[]) emitCommand(ctx, cmd);
    ctx.db.agentTask.id.update({ ...task, state: 'done', answer: plan.answer });
    ctx.db.agentPlan.taskId.delete(task.id);
  }
);

// ---------------------------------------------------------------- lifecycle

export const init = spacetimedb.init(ctx => {
  seedGraph(ctx);
});

export const onConnect = spacetimedb.clientConnected(ctx => {
  const me = ctx.db.participant.identity.find(ctx.sender);
  if (me) ctx.db.participant.identity.update({ ...me, online: true });
  else ctx.db.participant.insert({ identity: ctx.sender, name: 'Guest', role: 'Engineer', selectedNode: '', online: true });
});

export const onDisconnect = spacetimedb.clientDisconnected(ctx => {
  const me = ctx.db.participant.identity.find(ctx.sender);
  if (me) ctx.db.participant.identity.update({ ...me, online: false });
});

// ---------------------------------------------------------------- reducers

function me(ctx: Ctx) {
  const p = ctx.db.participant.identity.find(ctx.sender);
  if (!p) throw new SenderError('not joined');
  return p;
}

const ROLES = new Set(['Engineer', 'ProductManager', 'Architect']);

export const join = spacetimedb.reducer({ name: t.string(), role: t.string() }, (ctx, { name, role }) => {
  if (!ROLES.has(role)) throw new SenderError(`unknown role '${role}'`);
  const p = ctx.db.participant.identity.find(ctx.sender);
  const row = { identity: ctx.sender, name: name.trim() || 'Guest', role, selectedNode: p?.selectedNode ?? '', online: true };
  if (p) ctx.db.participant.identity.update(row);
  else ctx.db.participant.insert(row);
});

export const setRole = spacetimedb.reducer({ role: t.string() }, (ctx, { role }) => {
  if (!ROLES.has(role)) throw new SenderError(`unknown role '${role}'`);
  ctx.db.participant.identity.update({ ...me(ctx), role });
});

export const selectNode = spacetimedb.reducer({ nodeId: t.string() }, (ctx, { nodeId }) => {
  if (nodeId && !ctx.db.node.id.find(nodeId)) throw new SenderError(`unknown piece '${nodeId}'`);
  ctx.db.participant.identity.update({ ...me(ctx), selectedNode: nodeId });
});

export const moveNode = spacetimedb.reducer(
  { nodeId: t.string(), offsetX: t.f32(), offsetZ: t.f32(), held: t.bool() },
  (ctx, { nodeId, offsetX, offsetZ, held }) => {
    if (!ctx.db.node.id.find(nodeId)) throw new SenderError(`unknown piece '${nodeId}'`);
    const row = { nodeId, offsetX, offsetZ, held, heldBy: ctx.sender };
    if (ctx.db.nodePose.nodeId.find(nodeId)) ctx.db.nodePose.nodeId.update(row);
    else ctx.db.nodePose.insert(row);
  }
);

export const showBlastRadius = spacetimedb.reducer({ nodeId: t.string() }, (ctx, { nodeId }) => {
  if (!ctx.db.node.id.find(nodeId)) throw new SenderError(`unknown piece '${nodeId}'`);
  emitCommand(ctx, blastCommand(ctx, nodeId));
});

export const clearScene = spacetimedb.reducer(ctx => {
  emitCommand(ctx, { kind: 'clear' });
});

export const askQuestion = spacetimedb.reducer({ question: t.string() }, (ctx, { question }) => {
  const text = question.trim();
  if (!text) throw new SenderError('empty question');
  const p = me(ctx);

  // newest question wins: stop any plan still in progress
  for (const old of [...ctx.db.agentPlan.iter()]) {
    ctx.db.agentPlan.taskId.delete(old.taskId);
    const t0 = ctx.db.agentTask.id.find(old.taskId);
    if (t0) ctx.db.agentTask.id.update({ ...t0, state: 'done', answer: t0.answer || '(interrupted)' });
  }

  const task = ctx.db.agentTask.insert({
    id: 0n, question: text, askedBy: ctx.sender, askedByName: p.name, askedRole: p.role,
    state: 'thinking', answer: '', createdAt: ctx.timestamp,
  });
  const plan = planFor(ctx, text, p.role, p.selectedNode);
  ctx.db.agentPlan.insert({
    taskId: task.id, stepsJson: JSON.stringify(plan.steps), commandsJson: JSON.stringify(plan.commands),
    answer: plan.answer, cursor: 0,
  });
  scheduleTick(ctx, task.id, 300_000n);
  pruneTasks(ctx);
});

/** Restore the demo graph (and clear the scene) without republishing. */
export const resetDemo = spacetimedb.reducer(ctx => {
  seedGraph(ctx);
  for (const r of [...ctx.db.sceneCommand.iter()]) ctx.db.sceneCommand.id.delete(r.id);
  emitCommand(ctx, { kind: 'clear' });
});

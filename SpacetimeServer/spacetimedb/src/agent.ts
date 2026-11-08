// Nemotron multi-agent workflow, running *inside* SpacetimeDB as a procedure.
//
//   Orchestrator (Nemotron 3 Super, tool calling)
//     ├─ consult_structural_agent  → Nemotron specialist over structural findings
//     ├─ consult_operations_agent  → Nemotron specialist over risk graph + live sensors
//     ├─ consult_market_agent      → Nemotron specialist over valuation + location comps
//     ├─ simulate_fix              → deterministic what-if (cascade + price delta)
//     └─ dispatch_work_order       → transactional write: executes the fix
//
// Every plan / tool call / result is committed as an `agent_step` row so all
// subscribed clients watch the agent think in real time. The NVIDIA key lives
// in the database environment (NVIDIA_API_KEY) and never reaches a client.

import { t, type InferSchema, type ProcedureCtx } from 'spacetimedb/server';
import { TimeDuration } from 'spacetimedb';
import spacetimedb, { type Ctx } from './schema';
import { blueprintRisk, currentYear, riskModel, valuationModel, structuralAnalysis } from './engine';
import { createWorkOrderTx } from './ops';

const NIM_URL = 'https://integrate.api.nvidia.com/v1/chat/completions';
const DEFAULT_MODEL = 'nvidia/nemotron-3-super-120b-a12b';
const usd = (v: number) => '$' + Math.round(v).toLocaleString('en-US');
const pct = (v: number) => `${Math.round(v * 100)}%`;

// ───────────────────────────── Data digests (tool results) ──────────────────────────────

function structuralDigest(tx: Ctx, bpId: number): string {
  const s = tx.db.structuralSummary.blueprintId.find(bpId);
  const rooms = new Map([...tx.db.room.blueprintId.filter(bpId)].map(r => [r.id, r.name]));
  const f = [...tx.db.structuralFinding.blueprintId.filter(bpId)].filter(x => x.severity !== 'ok')
    .sort((a, b) => (a.severity === 'critical' ? 0 : 1) - (b.severity === 'critical' ? 0 : 1));
  return [
    `Integrity score ${s?.integrityScore ?? '?'} / 100 (${s?.criticalCount ?? 0} critical, ${s?.watchCount ?? 0} watch).`,
    ...f.map(x => `- [${x.severity.toUpperCase()}] ${x.title} (${rooms.get(x.roomId) ?? ''}). ${x.detail} Code: ${x.codeRef}. Fix ≈ ${usd(x.fixCost)}.`),
  ].join('\n');
}

function operationsDigest(tx: Ctx, bpId: number): string {
  const comps = new Map([...tx.db.component.blueprintId.filter(bpId)].map(c => [c.id, c]));
  const risks = [...tx.db.componentRisk.blueprintId.filter(bpId)].sort((a, b) => a.rank - b.rank).slice(0, 7);
  const paths = [...tx.db.cascadePath.blueprintId.filter(bpId)].slice(0, 5);
  const wos = [...tx.db.workOrder.blueprintId.filter(bpId)].filter(w => w.status !== 'done');
  const alerts = [...tx.db.alert.blueprintId.filter(bpId)].sort((a, b) => Number(b.id - a.id)).slice(0, 5);
  const total = risks.reduce((a, r) => a + r.expectedLoss, 0);
  return [
    `Expected 12-month loss (top systems): ${usd(total)}.`,
    'Systems ranked by expected loss:',
    ...risks.map(r => {
      const c = comps.get(r.componentId)!;
      return `- #${r.rank} ${c.name} [kind=${c.kind}] (${c.material}): standalone ${pct(r.baseProb)}, with cascade ${pct(r.cascadeProb)}, expected loss ${usd(r.expectedLoss)}, fixing it prevents ${usd(r.preventionValue)} across the graph, remaining life ${r.remainingLife.toFixed(1)} yr. Sensor ${c.sensorLabel}=${c.sensorValue}${c.sensorUnit} (warn ${c.sensorWarn}, crit ${c.sensorCritical}).${c.hazardFlag ? ' HAZARD: ' + c.hazardFlag : ''}`;
    }),
    'Most damaging cascade chains:',
    ...paths.map(p => `- ${p.label}: P=${pct(p.probability)}, downstream loss ${usd(p.loss)}. ${p.mechanism}`),
    wos.length ? 'Open work orders: ' + wos.map(w => `${w.title} (${w.status})`).join('; ') : 'No open work orders.',
    alerts.length ? 'Recent alerts: ' + alerts.map(a => a.message).join(' | ') : 'No recent alerts.',
  ].join('\n');
}

function marketDigest(tx: Ctx, bpId: number): string {
  const bp = tx.db.blueprint.id.find(bpId)!;
  const loc = tx.db.location.id.find(bp.locationId)!;
  const v = tx.db.valuation.blueprintId.find(bpId);
  const feats = [...tx.db.sellingFeature.blueprintId.filter(bpId)].sort((a, b) => b.valueImpact - a.valueImpact);
  const ups = [...tx.db.upgradeRec.blueprintId.filter(bpId)].sort((a, b) => b.roi - a.roi).slice(0, 5);
  const lp = [...tx.db.locationPricing.blueprintId.filter(bpId)].sort((a, b) => b.listPrice - a.listPrice);
  if (!v) return 'No valuation yet.';
  return [
    `${bp.name} — ${bp.sqft} sqft, ${bp.beds} bd / ${bp.baths} ba, built ${bp.yearBuilt}, ${loc.name}. Market: ${usd(loc.medianPpsf)}/sqft median, ${pct(loc.yoyAppreciation)} YoY, ${loc.daysOnMarket} DOM, schools ${loc.schoolRating}/10, walk ${loc.walkScore}.`,
    `Recommended list ${usd(v.listPrice)} (range ${usd(v.lowPrice)}–${usd(v.highPrice)}, ${usd(v.ppsf)}/sqft, confidence ${pct(v.confidence)}). Est. ${Math.round(v.daysOnMarket)} days on market.`,
    `Bridge: market base ${usd(v.marketBase)}, age adj ${pct(v.ageAdj)}, layout adj ${pct(v.layoutAdj)}, feature premium +${usd(v.featurePremium)}, condition discount −${usd(v.conditionDiscount)}, structural discount −${usd(v.structuralDiscount)}, 12-mo risk ${usd(v.expectedLoss12mo)}.`,
    `Pre-listing repairs ${usd(v.preListingRepairCost)} would unlock ${usd(v.valueUnlocked)} net. Annual carry ${usd(v.annualCarryCost)}.`,
    'Selling features: ' + feats.filter(f => f.present).slice(0, 6).map(f => `${f.name} (+${usd(f.valueImpact)}, demand ${pct(f.demandScore)})`).join('; '),
    'High-demand gaps: ' + feats.filter(f => !f.present).slice(0, 4).map(f => `${f.name} (demand ${pct(f.demandScore)})`).join('; '),
    'Best ROI moves: ' + ups.map(u => `${u.name} cost ${usd(u.cost)} → value ${usd(u.valueAdd)} + risk avoided ${usd(u.riskAvoided)} (ROI ${u.roi.toFixed(2)}x)`).join('; '),
    'Same model in other markets: ' + lp.map(p => `${p.locationName} ${usd(p.listPrice)} (${usd(p.ppsf)}/sqft, risk ${usd(p.riskLoss12mo)}/yr, ${p.topHazard})`).join('; '),
  ].join('\n');
}

function simulateFix(tx: Ctx, bpId: number, kind: string): string {
  const d = blueprintRisk(tx, bpId);
  if (!d) return 'Unknown blueprint';
  const c = d.comps.find(c => c.kind === kind);
  if (!c) return `No component of kind "${kind}". Valid kinds: ${d.comps.map(c => c.kind).join(', ')}`;
  const year = currentYear(tx);
  const after = riskModel(d.comps, d.loc, year, new Set([c.id]));
  const fixedComps = d.comps.map(x => (x.id === c.id ? { ...x, condition: 1, installYear: year, hazardFlag: '' } : x));
  const f0 = structuralAnalysis(d.bp, d.rooms, d.comps, d.loc);
  const f1 = structuralAnalysis(d.bp, d.rooms, fixedComps, d.loc);
  const v0 = valuationModel(d.bp, d.rooms, d.comps, d.loc, f0, d.risk, year);
  const v1 = valuationModel(d.bp, d.rooms, fixedComps, d.loc, f1, after, year);
  return `What-if: fix ${c.name} now for ${usd(c.replaceCost)}. 12-mo expected loss ${usd(d.risk.total)} → ${usd(after.total)} (−${usd(d.risk.total - after.total)}). ` +
    `List price ${usd(v0.listPrice)} → ${usd(v1.listPrice)} (${v1.listPrice >= v0.listPrice ? '+' : ''}${usd(v1.listPrice - v0.listPrice)}). Days on market ${Math.round(v0.daysOnMarket)} → ${Math.round(v1.daysOnMarket)}.`;
}

// ───────────────────────────── Persistence helpers ──────────────────────────────

type PCtx = ProcedureCtx<InferSchema<typeof spacetimedb>>;

function step(ctx: PCtx, runId: bigint, bpId: number, agent: string, kind: string, title: string, detail: string) {
  ctx.withTx(tx => {
    tx.db.agentStep.insert({ id: 0n, runId, blueprintId: bpId, agent, kind, title: title.slice(0, 160), detail: detail.slice(0, 2400), at: tx.timestamp });
  });
}

function read<T>(ctx: PCtx, fn: (tx: Ctx) => T): T {
  return ctx.withTx(tx => fn(tx as unknown as Ctx));
}

// ───────────────────────────── Nemotron client ──────────────────────────────

type ChatMsg = { role: string; content?: string | null; tool_calls?: any[]; tool_call_id?: string; name?: string };

function nim(ctx: PCtx, key: string, model: string, messages: ChatMsg[], tools?: any[]): { ok: true; msg: ChatMsg } | { ok: false; error: string } {
  const body: any = { model, messages, temperature: 0.3, top_p: 0.95, max_tokens: 2048 };
  if (tools) { body.tools = tools; body.tool_choice = 'auto'; }
  try {
    const res = ctx.http.fetch(NIM_URL, {
      method: 'POST',
      headers: { Authorization: `Bearer ${key}`, 'Content-Type': 'application/json', Accept: 'application/json' },
      body: JSON.stringify(body),
      timeout: TimeDuration.fromMillis(90_000),
    });
    const text = res.text();
    if (res.status !== 200) return { ok: false, error: `NIM HTTP ${res.status}: ${text.slice(0, 300)}` };
    const json = JSON.parse(text);
    const msg = json.choices?.[0]?.message as ChatMsg | undefined;
    if (!msg) return { ok: false, error: 'NIM returned no choices' };
    if (typeof msg.content === 'string') msg.content = msg.content.replace(/<think>[\s\S]*?<\/think>/g, '').trim();
    return { ok: true, msg };
  } catch (e) {
    return { ok: false, error: `NIM request failed: ${String(e)}` };
  }
}

const SPECIALISTS: Record<string, { title: string; system: string; digest: (tx: Ctx, bp: number) => string }> = {
  structural: {
    title: 'Structural Engineer agent',
    system: 'You are a licensed structural engineer reviewing a residential plan for a construction manager. Use ONLY the findings provided. Prioritise life-safety and load-path issues, cite the IRC section, give a concrete fix with cost. Max 120 words, plain text, bullets with "•".',
    digest: structuralDigest,
  },
  operations: {
    title: 'Operations & Failure-Prediction agent',
    system: 'You are a facilities reliability engineer. Using ONLY the risk graph data provided, explain which failure will cascade first, through which chain, and which single intervention prevents the most downstream loss. Quantify in dollars and probability. Max 120 words, plain text, bullets with "•".',
    digest: operationsDigest,
  },
  market: {
    title: 'Market & Valuation agent',
    system: 'You are a CBRE-style residential valuation analyst advising a listing agent. Using ONLY the data provided, give the recommended list price and range, the 3 features to lead marketing with, the best ROI pre-listing moves, and how location changes price. Max 140 words, plain text, bullets with "•".',
    digest: marketDigest,
  },
};

const TOOLS = [
  ...Object.entries(SPECIALISTS).map(([k, s]) => ({
    type: 'function',
    function: {
      name: `consult_${k}_agent`,
      description: `Hand off to the ${s.title}. It reads live data from the digital twin and returns an expert brief.`,
      parameters: { type: 'object', properties: { question: { type: 'string', description: 'Focused question for the specialist' } }, required: ['question'] },
    },
  })),
  {
    type: 'function',
    function: {
      name: 'simulate_fix',
      description: 'What-if simulation: fix one building system now and return the change in 12-month cascade loss, list price and days on market.',
      parameters: { type: 'object', properties: { component_kind: { type: 'string', enum: ['roof', 'gutters', 'foundation', 'plumbing', 'sewer', 'electrical', 'hvac', 'water_heater', 'windows', 'attic_insulation', 'drywall', 'mold'] } }, required: ['component_kind'] },
    },
  },
  {
    type: 'function',
    function: {
      name: 'dispatch_work_order',
      description: 'EXECUTE a preventive work order for a building system before the failure spreads. Only call when the user asks to act/fix/prevent/dispatch, or when a severe hazard is life-safety.',
      parameters: {
        type: 'object',
        properties: {
          component_kind: { type: 'string', enum: ['roof', 'gutters', 'foundation', 'plumbing', 'sewer', 'electrical', 'hvac', 'water_heater', 'windows', 'attic_insulation', 'drywall', 'mold'] },
          action: { type: 'string', description: 'Short description of the work, e.g. "Replace 100A FPE panel with 200A"' },
        },
        required: ['component_kind', 'action'],
      },
    },
  },
];

// ───────────────────────────── The procedure ──────────────────────────────

export const askAgent = spacetimedb.procedure(
  { blueprintId: t.u32(), prompt: t.string() },
  t.string(),
  (ctx, { blueprintId, prompt }) => {
    const key = ctx.env.NVIDIA_API_KEY ?? '';
    const model = ctx.env.NEMOTRON_MODEL || DEFAULT_MODEL;
    const specialistModel = ctx.env.NEMOTRON_SPECIALIST_MODEL || model;
    const cleanPrompt = prompt.trim().slice(0, 1200) || 'Give me the full briefing.';

    const setup = ctx.withTx(tx => {
      const profile = tx.db.userProfile.identity.find(tx.sender);
      const bp = tx.db.blueprint.id.find(blueprintId);
      if (!profile || !bp) return null;
      const loc = tx.db.location.id.find(bp.locationId)!;
      const run = tx.db.agentRun.insert({
        id: 0n, blueprintId, requesterName: profile.name, prompt: cleanPrompt, status: 'running',
        model: key ? model : 'deterministic-fallback', usedLlm: !!key, startedAt: tx.timestamp,
      });
      tx.db.agentMessage.insert({ id: 0n, blueprintId, runId: run.id, role: 'user', agent: 'user', authorName: profile.name, content: cleanPrompt, at: tx.timestamp });
      return { runId: run.id, name: profile.name, role: profile.role, bpName: bp.name, stage: bp.stage, locName: loc.name };
    });
    if (!setup) return 'Sign in and select a blueprint first.';
    const { runId } = setup;
    const persona = setup.role === 'construction_manager' ? 'a construction manager' : 'a real estate listing agent';

    let final = '';
    let failed = '';

    const runTool = (name: string, args: any): string => {
      if (name.startsWith('consult_')) {
        const k = name.replace('consult_', '').replace('_agent', '');
        const spec = SPECIALISTS[k];
        if (!spec) return 'Unknown specialist';
        const data = read(ctx, tx => spec.digest(tx, blueprintId));
        step(ctx, runId, blueprintId, k, 'handoff', `Orchestrator → ${spec.title}`, String(args?.question ?? ''));
        if (!key || failed) return data;
        const r = nim(ctx, key, specialistModel, [
          { role: 'system', content: spec.system + ' /no_think' },
          { role: 'user', content: `Question from ${persona}: ${args?.question ?? cleanPrompt}\n\nDIGITAL TWIN DATA:\n${data}` },
        ]);
        const brief = r.ok ? (r.msg.content ?? '') : `(specialist unavailable: ${r.error})\n${data}`;
        step(ctx, runId, blueprintId, k, 'tool_result', `${spec.title} brief`, brief);
        return brief;
      }
      if (name === 'simulate_fix') {
        const out = read(ctx, tx => simulateFix(tx, blueprintId, String(args?.component_kind ?? '')));
        step(ctx, runId, blueprintId, 'operations', 'tool_result', `What-if: fix ${args?.component_kind}`, out);
        return out;
      }
      if (name === 'dispatch_work_order') {
        const kind = String(args?.component_kind ?? '');
        const action = String(args?.action ?? 'Preventive repair').slice(0, 100);
        const out = ctx.withTx(tx => {
          const c = [...tx.db.component.blueprintId.filter(blueprintId)].find(c => c.kind === kind);
          if (!c) return `No component of kind ${kind}`;
          const wo = createWorkOrderTx(tx as unknown as Ctx, c.id, action, 'agent', `Nemotron agent (for ${setup.name})`);
          return `Work order #${wo.id} dispatched: ${wo.title}, budget ${usd(wo.cost)}, prevents ≈ ${usd(wo.lossAvoided)} of cascading loss. Status: ${wo.status}.`;
        });
        step(ctx, runId, blueprintId, 'orchestrator', 'action', 'Executed: work order dispatched', out);
        return out;
      }
      return `Unknown tool ${name}`;
    };

    if (key) {
      step(ctx, runId, blueprintId, 'orchestrator', 'plan', `Nemotron orchestrator (${model})`, `Request from ${setup.name} (${persona}) about ${setup.bpName}: "${cleanPrompt}"`);
      const messages: ChatMsg[] = [
        {
          role: 'system',
          content:
            `You are the CBRE Digital Twin orchestrator agent for ${persona}. You coordinate specialist agents over a live database ` +
            `that unifies plan geometry, building-system ages, sensor telemetry, hazard data and market comps. ` +
            `Always consult the specialists relevant to the question (usually all three for a full briefing) before answering; ` +
            `use simulate_fix to quantify interventions; call dispatch_work_order only when the user asks you to act/fix/prevent or a life-safety hazard exists. ` +
            `Final answer: plain text (no markdown tables), short headed sections, "•" bullets, concrete dollars and probabilities, end with a 1-line "Next action:". Max 260 words.`,
        },
        { role: 'user', content: `Property: ${setup.bpName} (${setup.stage}) in ${setup.locName}.\n${cleanPrompt}` },
      ];
      for (let i = 0; i < 6 && !final && !failed; i++) {
        const r = nim(ctx, key, model, messages, TOOLS);
        if (!r.ok) { failed = r.error; break; }
        const calls = r.msg.tool_calls ?? [];
        if (calls.length) {
          messages.push({ role: 'assistant', content: r.msg.content ?? '', tool_calls: calls });
          for (const call of calls) {
            let args: any = {};
            try { args = JSON.parse(call.function?.arguments || '{}'); } catch { /* keep empty */ }
            step(ctx, runId, blueprintId, 'orchestrator', 'tool_call', `${call.function?.name}`, JSON.stringify(args));
            const out = runTool(call.function?.name ?? '', args);
            messages.push({ role: 'tool', tool_call_id: call.id, name: call.function?.name, content: out });
          }
        } else {
          final = r.msg.content ?? '';
        }
      }
      if (failed) step(ctx, runId, blueprintId, 'orchestrator', 'error', 'Nemotron unavailable — switching to deterministic agents', failed);
    }

    if (!final) {
      // Deterministic agent graph (no API key / offline): same tools, rule-based synthesis.
      if (!key) step(ctx, runId, blueprintId, 'orchestrator', 'plan', 'Deterministic orchestrator (set NVIDIA_API_KEY for Nemotron)', `Request: "${cleanPrompt}"`);
      const wantsAction = /\b(fix|dispatch|work order|prevent|schedule|repair|execute|act)\b/i.test(cleanPrompt);
      const s = runTool('consult_structural_agent', { question: cleanPrompt });
      const o = runTool('consult_operations_agent', { question: cleanPrompt });
      const m = runTool('consult_market_agent', { question: cleanPrompt });
      const top = read(ctx, tx => {
        const r = [...tx.db.componentRisk.blueprintId.filter(blueprintId)].sort((a, b) => b.preventionValue - a.preventionValue)[0];
        const c = r ? tx.db.component.id.find(r.componentId) : undefined;
        return c && r ? { kind: c.kind, name: c.name, prevention: r.preventionValue } : null;
      });
      const what = top ? runTool('simulate_fix', { component_kind: top.kind }) : '';
      const act = top && wantsAction ? runTool('dispatch_work_order', { component_kind: top.kind, action: 'Preventive replacement before cascade' }) : '';
      const pick = (txt: string, n: number) => txt.split('\n').filter(l => l.startsWith('- ')).slice(0, n).map(l => '• ' + l.slice(2)).join('\n');
      final = [
        `Briefing for ${setup.bpName} (${setup.locName})`,
        '',
        'STRUCTURE',
        s.split('\n')[0],
        pick(s, 3) || '• No structural concerns.',
        '',
        'OPERATIONS — what fails first',
        o.split('\n')[0],
        pick(o.split('Most damaging')[1] ?? '', 2) || pick(o, 2),
        '',
        'MARKET',
        m.split('\n')[1] ?? m,
        m.split('\n')[3] ?? '',
        '',
        what ? `WHAT-IF\n${what}` : '',
        act ? `\nEXECUTED\n${act}` : '',
        '',
        top ? `Next action: ${wantsAction ? 'track' : 'dispatch'} the ${top.name.toLowerCase()} work order — it prevents ≈ ${usd(top.prevention)} of cascading loss.` : '',
      ].filter(x => x !== undefined).join('\n').replace(/\n{3,}/g, '\n\n').trim();
    }

    ctx.withTx(tx => {
      tx.db.agentMessage.insert({ id: 0n, blueprintId, runId, role: 'assistant', agent: 'orchestrator', authorName: key && !failed ? 'Nemotron' : 'Twin Agent', content: final, at: tx.timestamp });
      tx.db.agentStep.insert({ id: 0n, runId, blueprintId, agent: 'orchestrator', kind: 'final', title: 'Answer delivered', detail: final.slice(0, 400), at: tx.timestamp });
      const run = tx.db.agentRun.id.find(runId);
      if (run) tx.db.agentRun.id.update({ ...run, status: 'done', usedLlm: !!key && !failed });
    });
    return final;
  }
);

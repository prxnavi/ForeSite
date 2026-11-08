// Client-side benchmark for the Cascade SpacetimeDB module on Maincloud.
//   1. reducer → subscription round trip: call bench_ping(seq) and time until the
//      bench_echo event-table row for that seq arrives over the live subscription.
//   2. HTTP route latency: GET /route/health (module-served HTTP, no API server).
//   3. HTTP ingest latency: POST /route/sensor (IoT reading → transaction → live table).
// Prints JSON; record results with the owner-only record_metric reducer.
declare const process: any;
import { DbConnection } from './module_bindings';

const HOST = 'https://maincloud.spacetimedb.com';
const DB = process.env.DB ?? 'cascade-prxnavi';
const N = Number(process.env.N ?? 100);

const pct = (xs: number[], p: number) => { const s = [...xs].sort((a, b) => a - b); return s[Math.min(s.length - 1, Math.floor(p * s.length))]; };
const summary = (xs: number[]) => ({ n: xs.length, p50: +pct(xs, 0.5).toFixed(1), p95: +pct(xs, 0.95).toFixed(1), p99: +pct(xs, 0.99).toFixed(1), mean: +(xs.reduce((a, b) => a + b, 0) / xs.length).toFixed(1) });

async function http(n: number, method: string, path: string, body?: unknown) {
  const out: number[] = [];
  for (let i = 0; i < n; i++) {
    const t0 = performance.now();
    const r = await fetch(`${HOST}/v1/database/${DB}/route${path}`, { method, headers: body ? { 'content-type': 'application/json' } : {}, body: body ? JSON.stringify(body) : undefined });
    await r.text();
    if (!r.ok) throw new Error(`${path} → ${r.status}`);
    out.push(performance.now() - t0);
  }
  return summary(out);
}

function rtt(n: number): Promise<ReturnType<typeof summary>> {
  return new Promise((resolve, reject) => {
    const pending = new Map<number, number>();
    const times: number[] = [];
    let seq = 0;
    const conn = DbConnection.builder()
      .withUri(HOST)
      .withDatabaseName(DB)
      .onConnect(c => {
        c.db.benchEcho.onInsert((_ctx, row) => {
          const t0 = pending.get(row.seq);
          if (t0 === undefined) return;
          pending.delete(row.seq);
          times.push(performance.now() - t0);
          if (times.length >= n) { c.disconnect(); resolve(summary(times.slice(5))); } // drop warm-up
          else next(c);
        });
        c.subscriptionBuilder().onApplied(() => next(c)).subscribe(['SELECT * FROM bench_echo']);
      })
      .onConnectError((_c, e) => reject(e))
      .build();
    function next(c: typeof conn) { const s = seq++; pending.set(s, performance.now()); c.reducers.benchPing({ seq: s }); }
    setTimeout(() => reject(new Error('timeout')), 120000);
  });
}

(async () => {
  const health = await http(Math.min(N, 50), 'GET', '/health');
  const sensor = await http(Math.min(N, 50), 'POST', '/sensor', { asset: 'CH-1', value: 2.3 });
  const roundTrip = await rtt(N + 5);
  console.log(JSON.stringify({ db: DB, reducer_to_subscription_ms: roundTrip, http_route_ms: health, http_ingest_ms: sensor }, null, 2));
  process.exit(0);
})().catch(e => { console.error(e); process.exit(1); });

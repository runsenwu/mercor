// handleSummary: a text summary on stdout plus the full JSON summary in loadtest/results/.
import exec from 'k6/execution';
import { envStr } from './config.js';

// Script-relative, so it works on the host (loadtest/results) and in the container (/scripts/results).
const RESULTS_DIR = envStr('RESULTS_DIR', decodeURIComponent(import.meta.resolve('../results').replace(/^file:\/\//, '')));

const ms = (v) => (v === undefined ? '-' : `${v.toFixed(2)}ms`);
const pct = (v) => (v === undefined ? '-' : `${(v * 100).toFixed(3)}%`);

function metricLine(name, m) {
  const v = m.values;
  if (m.type === 'trend') return `${name}: avg=${ms(v.avg)} med=${ms(v.med)} p95=${ms(v['p(95)'])} p99=${ms(v['p(99)'])} max=${ms(v.max)}`;
  if (m.type === 'rate') return `${name}: ${pct(v.rate)} (${v.passes} of ${v.passes + v.fails})`;
  if (m.type === 'counter') return `${name}: ${v.count} (${v.rate.toFixed(1)}/s)`;
  return `${name}: ${v.value} (min ${v.min}, max ${v.max})`;
}

// phases: [{label, targetRps, sec, track}] from lib/mix.js; slo: SLO object.
export function makeHandleSummary(test, { phases = [], slo, config = {} } = {}) {
  return function handleSummary(data) {
    let testid;
    try { testid = (exec.test.options.tags || {}).testid; } catch (e) { /* not available */ }
    const stamp = new Date().toISOString().replace(/[:.]/g, '-');
    const file = `${RESULTS_DIR}/${testid || `${test}-${stamp}`}.json`;
    const M = data.metrics;

    const rows = phases.filter((p) => p.track).map((p) => {
      const reqs = M[`http_reqs{phase:${p.label}}`];
      const dur = M[`http_req_duration{endpoint:redirect,phase:${p.label}}`];
      const fail = M[`http_req_failed{phase:${p.label}}`];
      const count = reqs ? reqs.values.count : 0;
      const achievedRps = count / p.sec;
      const p99 = dur ? dur.values['p(99)'] : undefined;
      const errorRate = fail ? fail.values.rate : undefined;
      const reached = count > 0;
      const sloOk = reached && p99 < slo.redirectP99Ms && errorRate < slo.errorRate && achievedRps >= slo.minAchievedRatio * p.targetRps;
      return { phase: p.label, targetRps: p.targetRps, achievedRps: Math.round(achievedRps * 10) / 10,
        redirectP50Ms: dur && dur.values.med, redirectP95Ms: dur && dur.values['p(95)'], redirectP99Ms: p99, errorRate, reached, sloOk };
    });
    let knee = null;
    for (const r of rows) { if (!r.reached || !r.sloOk) break; knee = r; }
    const firstBreach = rows.find((r) => r.reached && !r.sloOk) || null;
    const analysis = { maxSustainedRps: knee && knee.targetRps, p99AtMaxSustainedMs: knee && knee.redirectP99Ms,
      peakAchievedRps: rows.reduce((a, r) => Math.max(a, r.achievedRps), 0), firstBreach, phases: rows };

    const out = [`\n=== ${test}${testid ? ` (${testid})` : ''} ===`];
    const failed = [];
    let passed = 0;
    for (const [name, m] of Object.entries(M)) {
      for (const [expr, t] of Object.entries(m.thresholds || {})) { if (t.ok) passed++; else failed.push(`${name} ${expr}`); }
    }
    out.push(`thresholds: ${passed} passed, ${failed.length} failed`);
    failed.slice(0, 15).forEach((f) => out.push(`  FAIL ${f}`));
    for (const c of data.root_group.checks || []) if (c.fails > 0) out.push(`  check failed ${c.fails}x: ${c.name}`);
    if (rows.length) {
      out.push('', `phase                 target  achieved   p50        p95        p99        errors     SLO (p99<${slo.redirectP99Ms}ms, err<${pct(slo.errorRate)})`);
      for (const r of rows) {
        out.push(`${r.phase.padEnd(20)} ${String(r.targetRps).padStart(7)} ${String(r.achievedRps).padStart(9)}   ${ms(r.redirectP50Ms).padEnd(10)} ${ms(r.redirectP95Ms).padEnd(10)} ${ms(r.redirectP99Ms).padEnd(10)} ${pct(r.errorRate).padEnd(10)} ${!r.reached ? 'not reached' : r.sloOk ? 'ok' : 'BREACH'}`);
      }
      out.push(`max sustained (SLO held): ${analysis.maxSustainedRps ?? 'none'} rps, p99 ${ms(analysis.p99AtMaxSustainedMs ?? undefined)}; peak achieved ${analysis.peakAchievedRps} rps`);
    }
    out.push('');
    for (const name of Object.keys(M).sort()) if (!name.includes('phase:') && !name.includes('window:')) out.push(metricLine(name, M[name]));
    out.push(`\nsummary json -> ${file}\n`);
    return { stdout: out.join('\n'), [file]: JSON.stringify({ test, testid, finishedAt: new Date().toISOString(), config, analysis, k6: data }, null, 1) };
  };
}

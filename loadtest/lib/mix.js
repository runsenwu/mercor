// The production-like traffic mix (default 90 % redirects, 5 % creates, 5 % API reads) as three
// open-model scenarios (ramping-arrival-rate) driven by a list of phases. Each iteration tags its
// metrics with `phase`, and per-phase thresholds make k6 report per-phase p99/error/throughput.
import exec from 'k6/execution';
import { SLO, SYSTEM_TAGS, SUMMARY_TREND_STATS, envFloat, envBool, envInt, k6Duration } from './config.js';
import { seededCode, seededLongUrl, zipfIndex } from './codes.js';
import { redirect, createLink, getLink, getStats, randomUrl } from './api.js';

export const MIX = {
  redirects: envFloat('MIX_REDIRECT', 0.9),
  creates: envFloat('MIX_CREATE', 0.05),
  reads: envFloat('MIX_READ', 0.05),
};

// phases: [{label, sec, rps, rpsBy?: {redirects, creates, reads}, track}]; rate jumps at phase start.
export function buildOptions(phases, { abortOnCollapse = false } = {}) {
  const scenarios = {};
  const thresholds = {
    'http_req_duration{endpoint:redirect}': [`p(99)<${SLO.redirectP99Ms}`],
    'http_req_duration{endpoint:create}': [`p(99)<${SLO.apiP99Ms}`],
    'http_req_duration{endpoint:get}': [`p(99)<${SLO.apiP99Ms}`],
    'http_req_duration{endpoint:stats}': [`p(99)<${SLO.apiP99Ms}`],
    http_req_failed: [`rate<${SLO.errorRate}`],
    checks: ['rate>0.999'],
  };
  const latencyBudget = envFloat('VU_LATENCY_BUDGET_MS', 250) / 1000;
  for (const [name, frac] of Object.entries(MIX)) {
    if (frac <= 0) continue;
    const rate = (p) => Math.max(0, Math.round(p.rpsBy && p.rpsBy[name] !== undefined ? p.rpsBy[name] : p.rps * frac));
    const stages = [];
    for (const p of phases) stages.push({ duration: '0s', target: rate(p) }, { duration: k6Duration(p.sec), target: rate(p) });
    const peak = Math.max(...phases.map(rate), 1);
    scenarios[name] = {
      executor: 'ramping-arrival-rate', exec: name, startRate: rate(phases[0]), timeUnit: '1s', stages,
      preAllocatedVUs: envInt('PRE_VUS', Math.max(5, Math.ceil(peak * 0.05))),
      maxVUs: envInt('MAX_VUS', Math.max(20, Math.ceil(peak * latencyBudget))), gracefulStop: '10s',
    };
    for (const p of phases) p.expected = (p.expected || 0) + rate(p);
  }
  for (const p of phases) {
    p.targetRps = p.expected;
    if (!p.track) continue;
    const abort = (expr) => ({ threshold: expr, abortOnFail: true, delayAbortEval: '10s' });
    thresholds[`http_req_duration{endpoint:redirect,phase:${p.label}}`] = [`p(99)<${SLO.redirectP99Ms}`]
      .concat(abortOnCollapse ? [abort(`p(99)<${envFloat('ABORT_P99_MS', 1000)}`)] : []);
    thresholds[`http_req_failed{phase:${p.label}}`] = [`rate<${SLO.errorRate}`]
      .concat(abortOnCollapse ? [abort(`rate<${envFloat('ABORT_ERROR_RATE', 0.05)}`)] : []);
    thresholds[`http_reqs{phase:${p.label}}`] = [`count>=${Math.floor(SLO.minAchievedRatio * p.expected * p.sec)}`];
  }
  return {
    scenarios, thresholds, systemTags: SYSTEM_TAGS, summaryTrendStats: SUMMARY_TREND_STATS,
    discardResponseBodies: true,
  };
}

export function makeExecs(phases, redirectSampler = zipfIndex) {
  const ends = [];
  let t = 0;
  for (const p of phases) { t += p.sec; ends.push(t); }
  const tagPhase = () => {
    const el = (Date.now() - exec.scenario.startTime) / 1000;
    let i = ends.findIndex((e) => el < e);
    if (i < 0) i = phases.length - 1;
    exec.vu.metrics.tags.phase = phases[i].label;
  };
  const statsRatio = envFloat('READ_STATS_RATIO', 0.5);
  return {
    redirects() { tagPhase(); const i = redirectSampler(); redirect(seededCode(i), { expectedLocation: seededLongUrl(i) }); },
    creates() { tagPhase(); createLink({ url: randomUrl() }); },
    reads() { tagPhase(); const c = seededCode(zipfIndex()); if (Math.random() < statsRatio) getStats(c); else getLink(c); },
  };
}

// Warm-up (untracked: the .NET JIT and connection pools are cold) then a step ramp.
// Defaults: 20 s warm-up + 10 steps of 16 s (500 -> 5000 rps) = 3 minutes.
export function steppedPhases() {
  const warmRate = envInt('WARMUP_RATE', 200);
  const warm = envFloat('WARMUP_SEC', 20);
  const start = envInt('START_RATE', 500);
  const step = envInt('STEP_RATE', 500);
  const max = envInt('MAX_RATE', 5000);
  const stepSec = envFloat('STEP_SEC', 16);
  const phases = [{ label: '00-warmup', sec: warm, rps: warmRate, track: false }];
  let i = 1;
  for (let r = start; r <= max; r += step, i++) phases.push({ label: `${String(i).padStart(2, '0')}-${r}rps`, sec: stepSec, rps: r, track: true });
  return phases;
}

export { envBool };

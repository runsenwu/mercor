// Steady baseline, sudden SPIKE_MULTIPLIER x jump of redirect traffic (a link goes viral), then back
// to baseline: per-phase table shows how fast latency/errors recover (click queue drains, etc.).
// Knobs: BASE_RATE (300) SPIKE_MULTIPLIER (10) WARMUP_SEC (30) BASELINE_SEC (60) SPIKE_SEC (60)
// RECOVERY_SEC (120, reported in 30 s windows) MIX_* ZIPF_S.
import { buildOptions, makeExecs, MIX } from './lib/mix.js';
import { makeHandleSummary } from './lib/summary.js';
import { SLO, BASE_URL, envInt, envFloat } from './lib/config.js';

const base = envInt('BASE_RATE', 300);
const spikeRedirects = Math.round(base * MIX.redirects * envFloat('SPIKE_MULTIPLIER', 10));
const phases = [
  { label: '00-warmup', sec: envFloat('WARMUP_SEC', 30), rps: base, track: false },
  { label: '01-baseline', sec: envFloat('BASELINE_SEC', 60), rps: base, track: true },
  { label: '02-spike', sec: envFloat('SPIKE_SEC', 60), rps: base, rpsBy: { redirects: spikeRedirects }, track: true },
];
const rec = envFloat('RECOVERY_SEC', 120);
for (let t = 0, i = 3; t < rec; t += 30, i++) phases.push({ label: `0${i}-recovery-${t}-${t + 30}s`.slice(-22), sec: Math.min(30, rec - t), rps: base, track: true });

export const options = buildOptions(phases);
const x = makeExecs(phases);
export const redirects = x.redirects;
export const creates = x.creates;
export const reads = x.reads;
export const handleSummary = makeHandleSummary('spike', { phases, slo: SLO, config: { BASE_URL, MIX, phases } });

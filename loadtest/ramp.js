// Step ramp of the arrival rate (open model) with the real traffic mix: finds the saturation point.
// Defaults: 20 s warm-up at 200 rps, then 500 rps +500 every 16 s up to 5000 rps (3 min, less if it
// collapses). Knobs: WARMUP_RATE WARMUP_SEC START_RATE STEP_RATE MAX_RATE STEP_SEC MIX_* ZIPF_S
// ABORT_ON_COLLAPSE ABORT_P99_MS ABORT_ERROR_RATE PRE_VUS MAX_VUS VU_LATENCY_BUDGET_MS.
import { buildOptions, makeExecs, steppedPhases, MIX, envBool } from './lib/mix.js';
import { makeHandleSummary } from './lib/summary.js';
import { SLO, BASE_URL } from './lib/config.js';

const phases = steppedPhases();
export const options = buildOptions(phases, { abortOnCollapse: envBool('ABORT_ON_COLLAPSE', true) });
const x = makeExecs(phases);
export const redirects = x.redirects;
export const creates = x.creates;
export const reads = x.reads;
export const handleSummary = makeHandleSummary('ramp', { phases, slo: SLO, config: { BASE_URL, MIX, phases, sampler: 'zipf' } });

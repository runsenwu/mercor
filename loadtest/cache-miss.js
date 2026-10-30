// Same as ramp.js, but redirects pick uniformly among all 100k seeded codes (long tail -> cache
// misses -> Postgres-bound). Compare with ramp.js to see what the cache is worth.
// collapses). Knobs: WARMUP_RATE WARMUP_SEC START_RATE STEP_RATE MAX_RATE STEP_SEC MIX_* ZIPF_S
// ABORT_ON_COLLAPSE ABORT_P99_MS ABORT_ERROR_RATE PRE_VUS MAX_VUS VU_LATENCY_BUDGET_MS.
import { buildOptions, makeExecs, steppedPhases, MIX, envBool } from './lib/mix.js';
import { makeHandleSummary } from './lib/summary.js';
import { SLO, BASE_URL } from './lib/config.js';
import { uniformIndex } from './lib/codes.js';

const phases = steppedPhases();
export const options = buildOptions(phases, { abortOnCollapse: envBool('ABORT_ON_COLLAPSE', true) });
const x = makeExecs(phases, uniformIndex);
export const redirects = x.redirects;
export const creates = x.creates;
export const reads = x.reads;
export const handleSummary = makeHandleSummary('cache-miss', { phases, slo: SLO, config: { BASE_URL, MIX, phases, sampler: 'uniform' } });

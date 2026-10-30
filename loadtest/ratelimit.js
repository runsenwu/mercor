// Rate limiting: an anonymous client creates faster than 20/min -> 201s then 429 + Retry-After;
// a keyed client in parallel is unaffected. All k6 traffic comes from one IP, so anonymous requests
// here share one budget (that is also why every other scenario sends the loadtest key).
// Knobs: DURATION (3m) ANON_PER_MIN (60) KEYED_PER_MIN (120) ANON_LIMIT (20).
import { check } from 'k6';
import exec from 'k6/execution';
import { Counter } from 'k6/metrics';
import { BASE_URL, SLO, SYSTEM_TAGS, SUMMARY_TREND_STATS, envStr, envInt } from './lib/config.js';
import { createLink, randomUrl } from './lib/api.js';
import { makeHandleSummary } from './lib/summary.js';

const DURATION = envStr('DURATION', '3m');
const LIMIT = envInt('ANON_LIMIT', 20);
const accepted = new Counter('ratelimit_anon_accepted');
const rejected = new Counter('ratelimit_anon_rejected');
const car = (fn, perMin) => ({ executor: 'constant-arrival-rate', exec: fn, rate: perMin, timeUnit: '1m', duration: DURATION, preAllocatedVUs: 5, maxVUs: 20 });

const thresholds = {
  'http_req_failed{scenario:keyed}': ['rate==0'],
  'checks{scenario:keyed}': ['rate==1'],
  'checks{scenario:anonymous}': ['rate==1'],
  ratelimit_anon_rejected: ['count>0'],
};
// Fixed one-minute windows aligned to the wall clock: at most LIMIT (+1 for boundary jitter) accepted each.
for (let w = 0; w <= 6; w++) thresholds[`ratelimit_anon_accepted{window:w${w}}`] = [`count<=${LIMIT + 1}`];

export const options = {
  scenarios: { anonymous: car('anonymous', envInt('ANON_PER_MIN', 60)), keyed: car('keyed', envInt('KEYED_PER_MIN', 120)) },
  thresholds, systemTags: SYSTEM_TAGS, summaryTrendStats: SUMMARY_TREND_STATS, discardResponseBodies: true,
};

export function anonymous() {
  const w = Math.floor(Date.now() / 60000) - Math.floor(exec.scenario.startTime / 60000);
  const r = createLink({ url: randomUrl() }, { apiKey: '', expected: [201, 429] });
  if (r.status === 201) accepted.add(1, { window: `w${w}` });
  if (r.status === 429) rejected.add(1, { window: `w${w}` });
  check(r, {
    'anonymous: 201 or 429': (x) => x.status === 201 || x.status === 429,
    'anonymous: 429 has Retry-After 1..60': (x) => x.status !== 429 || (+x.headers['Retry-After'] >= 1 && +x.headers['Retry-After'] <= 60),
  });
}

export function keyed() {
  createLink({ url: randomUrl() });
}

export const handleSummary = makeHandleSummary('ratelimit', { slo: SLO, config: { BASE_URL, DURATION, LIMIT } });

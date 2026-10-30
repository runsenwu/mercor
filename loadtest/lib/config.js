// Shared configuration. Every knob is an environment variable, so it can be set with
// `k6 run -e NAME=value ...`, a real env var, or `docker compose run -e NAME=value k6 ...`.

export function envStr(name, def) {
  const v = __ENV[name];
  return v === undefined || v === '' ? def : String(v);
}

export function envFloat(name, def) {
  const raw = envStr(name, undefined);
  if (raw === undefined) return def;
  const v = Number(raw);
  if (!Number.isFinite(v)) throw new Error(`env ${name}: "${raw}" is not a number`);
  return v;
}

export function envInt(name, def) {
  const v = envFloat(name, def);
  if (!Number.isInteger(v)) throw new Error(`env ${name}: "${v}" is not an integer`);
  return v;
}

export function envBool(name, def) {
  const raw = envStr(name, undefined);
  if (raw === undefined) return def;
  return ['1', 'true', 'yes', 'on'].includes(raw.toLowerCase());
}

// Durations: "90" (seconds), "45s", "5m", "1h", "1m30s".
export function parseDurationSec(str) {
  const s = String(str).trim();
  if (/^\d+(\.\d+)?$/.test(s)) return Number(s);
  const re = /(\d+(?:\.\d+)?)(ms|s|m|h)/g;
  let total = 0;
  let consumed = 0;
  let m;
  while ((m = re.exec(s)) !== null) {
    const n = Number(m[1]);
    total += m[2] === 'h' ? n * 3600 : m[2] === 'm' ? n * 60 : m[2] === 's' ? n : n / 1000;
    consumed += m[0].length;
  }
  if (consumed !== s.length || consumed === 0) throw new Error(`cannot parse duration "${str}"`);
  return total;
}

export function envDurationSec(name, def) {
  return parseDurationSec(envStr(name, def));
}

// "1h2m3s"-style string accepted by k6 for a number of seconds.
export function k6Duration(sec) {
  return `${Math.round(sec * 1000)}ms`;
}

// From the host the stack is reachable through nginx on :8080; inside the compose network the
// k6 service gets BASE_URL=http://nginx.
export const BASE_URL = envStr('BASE_URL', 'http://localhost:8080').replace(/\/+$/, '');

// Every scenario except ratelimit.js sends this key on /api/v1/* calls. All load comes from a
// single IP (the k6 host/container), and anonymous traffic is limited per client IP (20 creates
// and 300 reads per minute, shared by everything this machine sends). Load tests would measure
// the rate limiter instead of the service, so they authenticate with the `loadtest` key, which
// has a practically unlimited budget. Redirects are never rate-limited and need no key.
export const API_KEY = envStr('API_KEY', 'loadtest-key-change-me');
// A second valid key (the `demo` owner): smoke.js uses it to check that a non-owner gets 403.
export const DEMO_API_KEY = envStr('DEMO_API_KEY', 'demo-key-change-me');

// Seed data (loadtest/seed.sql): codes lt00000..lt99999, long_url https://example.com/<n>.
export const SEED_COUNT = envInt('SEED_COUNT', 100000);

// Service level objectives. A load step "holds" only while all of these hold.
export const SLO = {
  redirectP99Ms: envFloat('SLO_REDIRECT_P99_MS', 50),
  apiP99Ms: envFloat('SLO_API_P99_MS', 500),
  errorRate: envFloat('SLO_ERROR_RATE', 0.001),
  // An open-model test must actually deliver the load it asks for. Below this fraction of the
  // target rate, k6 ran out of VUs (dropped iterations), which means latency already exploded.
  minAchievedRatio: envFloat('SLO_MIN_ACHIEVED_RATIO', 0.95),
};

// A request that takes longer than this fails with a timeout and frees its VU.
export const REQUEST_TIMEOUT = envStr('REQUEST_TIMEOUT', '10s');

// Keep metric cardinality low: no `url` tag (one series per short code otherwise), no TLS/proto
// noise, and `error_code` instead of the free-text `error`. Requests get a fixed `name` tag.
export const SYSTEM_TAGS = ['status', 'method', 'name', 'check', 'error_code', 'scenario', 'expected_response'];

export const SUMMARY_TREND_STATS = ['avg', 'min', 'med', 'max', 'p(90)', 'p(95)', 'p(99)'];

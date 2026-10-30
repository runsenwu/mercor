// Correctness gate: low rate for ~1 min, every endpoint with checks. Exit code != 0 if any check fails.
import { check } from 'k6';
import http from 'k6/http';
import { BASE_URL, DEMO_API_KEY, SLO, SYSTEM_TAGS, SUMMARY_TREND_STATS, envStr, envInt } from './lib/config.js';
import { seededCode, seededLongUrl, zipfIndex } from './lib/codes.js';
import { redirect, createLink, getLink, getStats, listLinks, deleteLink, json, randomUrl, expect } from './lib/api.js';
import { makeHandleSummary } from './lib/summary.js';

const DURATION = envStr('DURATION', '1m');
const car = (exec, rate, timeUnit) => ({ executor: 'constant-arrival-rate', exec, rate, timeUnit, duration: DURATION, preAllocatedVUs: 5, maxVUs: 20 });
export const options = {
  scenarios: {
    redirects: car('redirects', envInt('REDIRECT_RATE', 10), '1s'),
    journey: car('journey', 1, '5s'),
    negatives: car('negatives', 1, '15s'),
    anonymous: car('anonymous', 1, '30s'),
  },
  thresholds: { checks: ['rate==1'], http_req_failed: ['rate==0'], 'http_req_duration{endpoint:redirect}': [`p(99)<${SLO.redirectP99Ms}`] },
  systemTags: SYSTEM_TAGS, summaryTrendStats: SUMMARY_TREND_STATS, discardResponseBodies: true,
};

export function redirects() {
  const i = zipfIndex();
  const r = redirect(seededCode(i), { expectedLocation: seededLongUrl(i) });
  check(r, { 'redirect: Cache-Control no-store': (x) => /no-store/.test(x.headers['Cache-Control'] || '') });
}

export function journey() {
  const url = randomUrl();
  const c = createLink({ url }, { parse: true });
  const link = json(c);
  if (!check(link, {
    'create: body has code/url/owner': (l) => l && /^[A-Za-z0-9]{7}$/.test(l.code) && l.url === url && l.owner === 'loadtest',
    'create: shortUrl ends with code': (l) => l && l.shortUrl.endsWith('/' + l.code),
  })) return;
  const code = link.code;
  redirect(code, { expectedLocation: url });
  check(json(getLink(code, { parse: true })), { 'get: body matches': (l) => l && l.code === code && l.url === url });
  check(json(getStats(code, { parse: true })), { 'stats: body shape': (s) => s && s.code === code && typeof s.totalClicks === 'number' && Array.isArray(s.daily) });
  const p1 = json(listLinks({ limit: 5 }, { parse: true }));
  check(p1, { 'list: page 1 has 5 items and a cursor': (p) => p && p.items.length === 5 && !!p.nextCursor });
  if (p1 && p1.nextCursor) {
    const p2 = json(listLinks({ limit: 5, cursor: p1.nextCursor }, { parse: true }));
    const seen = new Set(p1.items.map((l) => l.code));
    check(p2, { 'list: cursor page 2 is disjoint': (p) => p && p.items.length > 0 && p.items.every((l) => !seen.has(l.code)) });
  }
  deleteLink(code);
  check(redirect(code, { expected: [410] }), { 'redirect after delete: 410': (r) => r.status === 410 });
  check(getLink(code, { expected: [410] }), { 'get after delete: 410': (r) => r.status === 410 });
}

export function negatives() {
  check(redirect('nope' + Math.random().toString(36).slice(2, 8), { expected: [404] }), { 'unknown code: 404': (r) => r.status === 404 });
  const bad = createLink({ url: 'ftp://example.com/x' }, { expected: [400], parse: true });
  check(bad, { 'invalid url: 400 problem+json': (r) => r.status === 400 && /problem\+json/.test(r.headers['Content-Type'] || '') });
  check(createLink({ url: randomUrl() }, { apiKey: 'wrong-key', expected: [401] }), { 'invalid key: 401': (r) => r.status === 401 });
  check(getLink('lt00001', { apiKey: 'wrong-key', expected: [401] }), { 'invalid key on read: 401': (r) => r.status === 401 });
  const alias = 'smk-' + Math.random().toString(36).slice(2, 10);
  createLink({ url: randomUrl(), alias });
  check(createLink({ url: randomUrl(), alias }, { expected: [409] }), { 'alias taken: 409': (r) => r.status === 409 });
  deleteLink(alias);
  check(listLinks({}, { apiKey: '', expected: [401] }), { 'list without key: 401': (r) => r.status === 401 });
  check(listLinks({ limit: 101 }, { expected: [400] }), { 'list limit 101: 400': (r) => r.status === 400 });
  check(deleteLink('lt00002', { apiKey: DEMO_API_KEY, expected: [403] }), { 'delete by non-owner: 403': (r) => r.status === 403 });
  check(deleteLink('nope' + Math.random().toString(36).slice(2, 8), { expected: [404] }), { 'delete unknown: 404': (r) => r.status === 404 });
  check(http.get(`${BASE_URL}/openapi/v1.json`, { tags: { name: 'GET /openapi/v1.json', endpoint: 'docs' } }), { 'openapi: 200': (r) => r.status === 200 });
  check(http.get(`${BASE_URL}/healthz`, { redirects: 0, tags: { name: 'GET /healthz', endpoint: 'health' }, responseCallback: expect(403, 404) }),
    { 'healthz not public': (r) => r.status !== 200 });
}

// Anonymous create (20/min per IP, shared with everything else this machine sends): 201 or 429.
export function anonymous() {
  const r = createLink({ url: randomUrl() }, { apiKey: '', expected: [201, 429], parse: true });
  check(r, { 'anonymous create: 201 (owner null) or 429 with Retry-After': (x) =>
    (x.status === 201 && json(x).owner === null) || (x.status === 429 && !!x.headers['Retry-After']) });
  if (r.status === 201) {
    check(deleteLink(json(r).code, { expected: [403] }), { 'delete anonymous link: 403': (x) => x.status === 403 });
  }
}

export const handleSummary = makeHandleSummary('smoke', { slo: SLO, config: { BASE_URL, DURATION } });

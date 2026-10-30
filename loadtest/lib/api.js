// Request helpers. Fixed `name` tags (no per-code time series) and an `endpoint` tag per call.
import http from 'k6/http';
import { check } from 'k6';
import { BASE_URL, API_KEY, REQUEST_TIMEOUT } from './config.js';

const cbCache = {};
// Marks the given statuses as "expected" so they do not count in http_req_failed.
export function expect(...codes) {
  const k = codes.join(',');
  return cbCache[k] || (cbCache[k] = http.expectedStatuses(...codes));
}

function params(name, endpoint, { apiKey = API_KEY, expected, parse = false, headers = {}, tags = {} } = {}) {
  const h = { ...headers };
  if (apiKey) h['X-Api-Key'] = apiKey;
  const p = { headers: h, timeout: REQUEST_TIMEOUT, tags: { name, endpoint, ...tags } };
  if (expected) p.responseCallback = expect(...expected);
  if (parse) p.responseType = 'text';
  return p;
}

export function json(res) {
  try { return res.json(); } catch (e) { return null; }
}

// GET /{code}. redirects: 0 is essential: k6 follows redirects by default and would load-test
// example.com instead of us. No API key: redirects are public and never rate-limited.
export function redirect(code, { expectedLocation, expected = [302], tags } = {}) {
  const res = http.get(`${BASE_URL}/${code}`, {
    redirects: 0, timeout: REQUEST_TIMEOUT, tags: { name: 'GET /{code}', endpoint: 'redirect', ...tags },
    responseCallback: expect(...expected),
  });
  if (expected.length === 1 && expected[0] === 302) {
    check(res, {
      'redirect: status 302': (r) => r.status === 302,
      'redirect: Location is the long url': (r) =>
        expectedLocation ? r.headers['Location'] === expectedLocation : !!r.headers['Location'],
    });
  }
  return res;
}

export function createLink(body, opts = {}) {
  const p = params('POST /api/v1/links', 'create', { expected: [201], ...opts });
  p.headers['Content-Type'] = 'application/json';
  const res = http.post(`${BASE_URL}/api/v1/links`, JSON.stringify(body), p);
  if (!opts.expected) {
    check(res, {
      'create: status 201': (r) => r.status === 201,
      'create: Location header': (r) => /^\/api\/v1\/links\/[A-Za-z0-9_-]+$/.test(r.headers['Location'] || ''),
    });
  }
  return res;
}

export function getLink(code, opts = {}) {
  const res = http.get(`${BASE_URL}/api/v1/links/${code}`, params('GET /api/v1/links/{code}', 'get', { expected: [200], ...opts }));
  if (!opts.expected) check(res, { 'get: status 200': (r) => r.status === 200 });
  return res;
}

export function getStats(code, opts = {}) {
  const res = http.get(`${BASE_URL}/api/v1/links/${code}/stats`, params('GET /api/v1/links/{code}/stats', 'stats', { expected: [200], ...opts }));
  if (!opts.expected) check(res, { 'stats: status 200': (r) => r.status === 200 });
  return res;
}

export function listLinks({ limit = 20, cursor } = {}, opts = {}) {
  const q = `limit=${limit}` + (cursor ? `&cursor=${encodeURIComponent(cursor)}` : '');
  const res = http.get(`${BASE_URL}/api/v1/links?${q}`, params('GET /api/v1/links', 'list', { expected: [200], ...opts }));
  if (!opts.expected) check(res, { 'list: status 200': (r) => r.status === 200 });
  return res;
}

export function deleteLink(code, opts = {}) {
  const res = http.del(`${BASE_URL}/api/v1/links/${code}`, null, params('DELETE /api/v1/links/{code}', 'delete', { expected: [204], ...opts }));
  if (!opts.expected) check(res, { 'delete: status 204': (r) => r.status === 204 });
  return res;
}

export function randomUrl() {
  return `https://example.com/articles/${Date.now()}/${Math.random().toString(36).slice(2, 12)}?utm_source=k6`;
}

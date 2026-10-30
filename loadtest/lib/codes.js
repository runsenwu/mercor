// Short-code samplers over the seeded codes lt00000..lt99999.
import { SEED_COUNT, envFloat } from './config.js';

export const seededCode = (i) => 'lt' + String(i).padStart(5, '0');
export const seededLongUrl = (i) => 'https://example.com/' + i;

// Uniform: every seeded code equally likely -> long tail, many cache misses.
export function uniformIndex() {
  return Math.floor(Math.random() * SEED_COUNT);
}

// Zipf(s): rank k has probability ~ 1/k^s (s=1: top 10 codes ~24 % of traffic, top 1000 ~62 %),
// like viral links. Exact O(1) rejection-inversion sampler (Hoermann & Derflinger 1996, as in
// Apache Commons RNG) - no per-VU probability table.
export class ZipfSampler {
  constructor(n, s) {
    this.n = n;
    this.e = s;
    this.hX1 = this.hInt(1.5) - 1;
    this.hN = this.hInt(n + 0.5);
    this.s = 2 - this.hIntInv(this.hInt(2.5) - this.h(2));
  }
  h(x) { return Math.exp(-this.e * Math.log(x)); }
  hInt(x) { const lx = Math.log(x); return h2((1 - this.e) * lx) * lx; }
  hIntInv(x) { let t = x * (1 - this.e); if (t < -1) t = -1; return Math.exp(h1(t) * x); }
  sample() { // rank 1..n
    for (;;) {
      const u = this.hN + Math.random() * (this.hX1 - this.hN);
      const x = this.hIntInv(u);
      let k = Math.floor(x + 0.5);
      if (k < 1) k = 1; else if (k > this.n) k = this.n;
      if (k - x <= this.s || u >= this.hInt(k + 0.5) - this.h(k)) return k;
    }
  }
}
function h1(x) { return Math.abs(x) > 1e-8 ? Math.log1p(x) / x : 1 - x * (0.5 - x * (1 / 3 - 0.25 * x)); }
function h2(x) { return Math.abs(x) > 1e-8 ? Math.expm1(x) / x : 1 + x * 0.5 * (1 + x * (1 / 3) * (1 + 0.25 * x)); }

const zipf = new ZipfSampler(SEED_COUNT, envFloat('ZIPF_S', 1.0));
// Spread hot ranks over the keyspace (rank r -> index (r-1)*7919 mod N) so hot rows are not
// physically adjacent in Postgres. 7919 is prime and coprime with 100000.
const MULT = gcd(7919, SEED_COUNT) === 1 ? 7919 : 1;
function gcd(a, b) { return b === 0 ? a : gcd(b, a % b); }

export function zipfIndex() {
  return ((zipf.sample() - 1) * MULT) % SEED_COUNT;
}

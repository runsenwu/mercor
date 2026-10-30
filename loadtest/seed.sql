-- Load-test seed: 100,000 links lt00000..lt99999 owned by `loadtest`, long_url https://example.com/<n>.
-- Idempotent: the anti-join skips existing rows (so re-runs do not burn identity values) and
-- ON CONFLICT DO NOTHING covers concurrent runs. Usage: psql -U shortener -d shortener -f seed.sql
INSERT INTO links (code, long_url, owner)
SELECT 'lt' || lpad(n::text, 5, '0'), 'https://example.com/' || n, 'loadtest'
FROM generate_series(0, 99999) AS n
WHERE NOT EXISTS (SELECT 1 FROM links l WHERE l.code = 'lt' || lpad(n::text, 5, '0'))
ON CONFLICT (code) DO NOTHING;

ANALYZE links;

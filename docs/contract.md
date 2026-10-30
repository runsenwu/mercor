# Interface contract

Reference for how the components (API, worker, platform, load tests) fit together.
Defaults live in code (`Options.cs`, `WorkerOptions.cs`); keep this file in sync when they change.

## Repo layout

```
Shortener.slnx, Directory.Build.props, Directory.Packages.props (all NuGet versions), global.json
src/Shortener.Shared     shared contracts: ClickEvent, ClickTopology, MessagePropagation, TelemetryExtensions, DiagnosticNames
src/Shortener.Api        ASP.NET Core minimal API (redirects + public REST API)
src/Shortener.Worker     analytics worker (RabbitMQ consumer -> batched Postgres writes)
tests/Shortener.Api.Tests, tests/Shortener.Worker.Tests   xUnit v3 tests (Microsoft Testing Platform; `dotnet test`)
deploy/docker-compose.yml, deploy/nginx/, deploy/postgres/init/ (schema), deploy/otel-collector/,
deploy/prometheus/, deploy/tempo/, deploy/loki/, deploy/grafana/{provisioning,dashboards}/
loadtest/                k6 scripts + seed.sql (see loadtest/README.md)
Makefile
docs/contract.md        this file
```

## Compose services and ports

| Service | Image / build | Internal port | Host port |
|---|---|---|---|
| nginx | nginx | 80 | **8080** (the only public entry point) |
| api (scalable, no container_name, no host port) | build `src/Shortener.Api/Dockerfile` | 8080 | none |
| worker (scalable) | build `src/Shortener.Worker/Dockerfile` | none | none |
| postgres | postgres 18 | 5432 | none |
| redis | redis 8 | 6379 | none |
| rabbitmq | rabbitmq 4 with management | 5672, 15672 (UI), 15692 (Prometheus) | 127.0.0.1:15672 |
| otel-collector | otel/opentelemetry-collector-contrib | 4317 (gRPC), 4318 (HTTP), 8889 (Prometheus exporter) | none |
| prometheus | prom/prometheus v3 | 9090 | 127.0.0.1:9090 |
| tempo | grafana/tempo | 3200, 4317 | none |
| loki | grafana/loki | 3100 | none |
| grafana | grafana/grafana | 3000 | 127.0.0.1:3000 |
| postgres-exporter, redis-exporter, cadvisor | exporters | — | none |
| k6 (profile `loadtest`) | grafana/k6:2.3.0 | — | none |

Credentials (dev only): Postgres `shortener/shortener`, database `shortener`. RabbitMQ `shortener/shortener`, vhost `/`.

## Configuration (environment variables; .NET `__` = `:`)

Shared by api and worker:

| Variable | Default | Meaning |
|---|---|---|
| `ConnectionStrings__Postgres` | `Host=postgres;Database=shortener;Username=shortener;Password=shortener;Maximum Pool Size=100` | Npgsql |
| `ConnectionStrings__RabbitMq` | `amqp://shortener:shortener@rabbitmq:5672/` | |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `http://otel-collector:4317` | all three signals |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc` | |
| `OTEL_METRIC_EXPORT_INTERVAL` | `5000` | ms; the default (60 s) is too slow for a live dashboard |
| `Telemetry__TraceSampleRatio` | `1.0` | head sampling ratio (parent-based) |

API only:

| Variable | Default | Meaning |
|---|---|---|
| `ConnectionStrings__Redis` | `redis:6379` | StackExchange.Redis |
| `Shortener__PublicBaseUrl` | `http://localhost:8080` | used to build `shortUrl` |
| `Cache__Enabled` | `true` | `false` = every redirect reads Postgres (to show what the cache is worth) |
| `Cache__TtlSeconds` | `3600` | positive entries; never longer than the link's remaining lifetime |
| `Cache__NegativeTtlSeconds` | `60` | "not found" / "gone" entries |
| `Clicks__Mode` | `queue` | `queue` = in-memory buffer -> RabbitMQ; `sync` = `UPDATE` in the request (to demonstrate hot-row contention); `off` |
| `Clicks__BufferCapacity` | `10000` | bounded in-memory buffer; when full, new clicks are dropped and counted |
| `RateLimits__AnonymousCreatesPerMinute` | `20` | per client IP |
| `RateLimits__AnonymousReadsPerMinute` | `300` | per client IP, for `/api/v1/*` GETs |
| `ApiKeys__{n}__Name` / `__Key` / `__PermitsPerMinute` | see below | static API keys |

Default keys (dev only): `demo` / `demo-key-change-me` / 600 per minute, and
`loadtest` / `loadtest-key-change-me` / 100000000 per minute.

Worker only:

| Variable | Default | Meaning |
|---|---|---|
| `Worker__Prefetch` | `1000` | RabbitMQ QoS prefetch per consumer |
| `Worker__BatchSize` | `500` | flush when this many clicks are buffered... |
| `Worker__BatchMaxWaitMs` | `1000` | ...or when the oldest buffered click is this old |

## HTTP API (served by `api`, reached through nginx at http://localhost:8080)

JSON uses camelCase. Errors use RFC 9457 Problem Details (`application/problem+json`).
API key header: `X-Api-Key`. A present-but-invalid key returns **401**; it never falls back to anonymous.

| Method and path | Auth | Success | Errors |
|---|---|---|---|
| `GET /{code}` | none | **302**, `Location: <long url>`, `Cache-Control: no-store` | 404 unknown, 410 deleted or expired |
| `POST /api/v1/links` | optional key | **201**, `Location: /api/v1/links/{code}`, body = Link | 400 validation, 401, 409 alias taken, 429 |
| `GET /api/v1/links/{code}` | optional key | **200**, Link | 404, 410, 401, 429 |
| `GET /api/v1/links/{code}/stats` | optional key | **200**, Stats | 404, 410, 401, 429 |
| `GET /api/v1/links?limit=20&cursor=...` | **key required** | **200**, Page | 400, 401, 429 |
| `DELETE /api/v1/links/{code}` | **key required**, owner only | **204** | 401, 403 not owner, 404, 429 |
| `GET /docs` | none | Scalar API reference UI | |
| `GET /openapi/v1.json` | none | OpenAPI document | |
| `GET /healthz` | none | 200 if the process is up | (blocked by nginx) |
| `GET /readyz` | none | 200 if Postgres, Redis and RabbitMQ are reachable, else 503 | (blocked by nginx) |

Create request: `{ "url": "https://...", "alias": "optional", "expiresAt": "optional ISO-8601, must be in the future" }`
- `url`: absolute, scheme `http` or `https` only, max 2048 chars, host must not be our own public host.
- `alias`: 3-32 chars of `[A-Za-z0-9_-]`; not a reserved word (`api`, `docs`, `openapi`, `healthz`, `readyz`, `metrics`, `favicon.ico`, `robots.txt`, `scalar`).
- Generated codes: 7 chars, base62, `RandomNumberGenerator`; on unique-constraint conflict, regenerate (max 5 attempts, count conflicts in a metric).

Link: `{ "code", "shortUrl", "url", "createdAt", "expiresAt", "clicks", "owner" }` (`owner` is null for anonymous links).
Stats: `{ "code", "totalClicks", "daily": [ { "date": "2026-10-30", "clicks": 12 } ] }` (last 30 days, oldest first).
Page: `{ "items": [Link...], "nextCursor": "opaque string or null" }`, newest first; `limit` 1-100, default 20.

Rate limiting: fixed one-minute windows counted in Redis (`INCR` + `EXPIRE`), so limits hold across all api replicas.
Partition = API key name if a valid key is sent, else client IP (taken from `X-Forwarded-For`, set by nginx).
Anonymous creates and anonymous API reads have separate budgets. Redirects are never rate-limited in the app.
On 429: `Retry-After` header (seconds until the window resets).

## Data

Schema: `deploy/postgres/init/001_schema.sql` (tables `links`, `link_clicks_daily`). Seed data for load tests:
`loadtest/seed.sql` inserts 100,000 links with codes `lt00000`..`lt99999`, owner `loadtest`,
`long_url = 'https://example.com/' || n`.

Redis keys (api only):
- `link:{code}` -> long URL, or the sentinel `~404` (unknown) / `~410` (deleted or expired)
- `rl:{bucket}:{partition}:{unixMinute}` -> rate-limit counter, `bucket` in `create`, `read`

## Click events (RabbitMQ)

Topology in `Shortener.Shared.ClickTopology` (exchange `clicks`, queue `clicks.analytics`, DLQ `clicks.analytics.dlq`).
Body: `ClickEvent` JSON via `SharedJsonContext`. Headers: W3C `traceparent` via `MessagePropagation`.
Persistent delivery mode. Delivery is at-least-once; clicks are best-effort (a redirect never fails because of analytics).

## Telemetry

Resource: `service.name` = `shortener-api` | `shortener-worker`, `service.instance.id` = container hostname.
In Prometheus these appear as labels `service_name` and `service_instance_id` (collector `resource_to_telemetry_conversion`).

Custom metrics (Meter `Shortener.Api` / `Shortener.Worker`). Prometheus names in brackets.
Counters have **no unit**; durations use unit `s`.

API:
- `shortener.cache.requests`, tag `result` = `hit` | `miss` | `negative_hit` [`shortener_cache_requests_total`]
- `shortener.cache.lookup.duration` (s) [`shortener_cache_lookup_duration_seconds`]
- `shortener.redirects`, tag `result` = `found` | `not_found` | `gone` [`shortener_redirects_total`]
- `shortener.links.created`, tag `auth` = `anonymous` | `api_key` [`shortener_links_created_total`]
- `shortener.code.collisions` [`shortener_code_collisions_total`]
- `shortener.ratelimit.rejected`, tag `bucket` = `create` | `read`, `auth` = `anonymous` | `api_key` [`shortener_ratelimit_rejected_total`]
- `shortener.clicks.enqueued` / `shortener.clicks.dropped` / `shortener.clicks.published` / `shortener.clicks.publish_failures` [`..._total`]
- `shortener.clicks.buffer.size` (observable gauge) [`shortener_clicks_buffer_size`]

Worker:
- `shortener.worker.clicks.processed` [`shortener_worker_clicks_processed_total`]
- `shortener.worker.batch.size` (histogram, no unit) [`shortener_worker_batch_size`]
- `shortener.worker.batch.duration` (s) [`shortener_worker_batch_duration_seconds`]
- `shortener.worker.click.lag` (s), time from redirect to stored [`shortener_worker_click_lag_seconds`]
- `shortener.worker.batch.failures` [`shortener_worker_batch_failures_total`]
- `shortener.worker.dead_lettered` [`shortener_worker_dead_lettered_total`]

Built-in metrics we rely on: ASP.NET Core `http.server.request.duration` (tags `http.route`,
`http.response.status_code`), Kestrel connection metrics, `System.Runtime` (thread pool, GC, CPU, memory),
Npgsql pool and command metrics (meter `Npgsql`).

Traces: ASP.NET Core, HttpClient, Npgsql (`AddNpgsql()`), Redis, and our ActivitySources `Shortener.Api` /
`Shortener.Worker`. A redirect trace continues into the worker via `traceparent`; a worker batch span
**links** to the traces of the clicks it contains (max 100 links per batch span).

Logs: `ILogger` -> OTLP -> Loki, with trace and span ids. Do not log successful redirects at Information.

Grafana datasource UIDs: `prometheus`, `tempo`, `loki`. Main dashboard UID `shortener-overview`.

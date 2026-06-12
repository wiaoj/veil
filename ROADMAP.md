# Veil — Roadmap

---

## Phase 1 — Foundation

### 1.1 Repository & Tooling
- [x] Solution structure: `Veil.sln`, module projects, `Apps/` projects
- [x] `docker-compose.yml` — PostgreSQL, Redis (3 instances: rate-limit, tokens, config), ClickHouse
- [ ] `.env.example` with all required variables (edge done, control plane pending)
- [x] CI pipeline (GitHub Actions) — edge cargo test (+clippy advisory), full .NET solution build (checks out wiaoj/libraries sibling), dashboard bun build

### 1.2 Veil.Shared
- [ ] `PagedList<T>` 
- [ ] `ICurrentUser`
- [x] Snowflake ID generator via Wiaoj.Primitives.Snowflake
- [x] Prefixed + obfuscated ID helpers (prefix registry, Hashids encoding/decoding)
- [x] `Wiaoj.Results` — `Result<T>` type, error types, Minimal API extension (`ToHttpResult()`)
- [x] RFC 9457 `ProblemDetails` error response shaping (`ToProblemDetails()` via Wiaoj.Results.AspNetCore)

### 1.3 Veil.Zones — Domain & Persistence
- [x] `Zone` aggregate, `ZoneId` (prefixed Snowflake), `ZoneStatus`
- [x] `Rule` entity, `RuleId`, `RuleCondition` (all condition types), `RuleAction`
- [x] `UpstreamConfig`, `ChallengeConfig` value objects
- [x] `ZoneConfigChangedEvent` domain event
- [x] `ZonesDbContext` with own schema (`zones`)
- [x] EF Core configuration + initial migration (jsonb columns via persistence DTOs)

### 1.4 Veil.Auth — Domain & Persistence
- [x] `User` entity, `UserId`, `UserRole`
- [x] `ApiKey` entity, `ApiKeyId`, scopes
- [x] JWT issuance (HMAC-SHA256, obfuscated sub) + refresh token rotation (hashed, single-use, replay-detectable)
- [x] API key hashing + verification (SHA-256, shown once)
- [x] `AuthDbContext` with own schema (`auth`) + outbox
- [x] EF Core configuration + initial migration
- [x] Seed: default admin user (`Auth:AdminPassword` unset → generated + logged once)

### 1.5 Veil.EdgeNodes — Domain & Persistence
- [x] `EdgeNode` entity, `EdgeNodeId` (hashed node token, status, last-seen)
- [x] `ConfigPushLog` entity
- [x] `EdgeNodesDbContext` with own schema (`edge_nodes`)
- [x] EF Core configuration + initial migration

### 1.6 Veil.Zones — Application
> Implemented as vertical-slice endpoint features (no separate command/handler layer) — the control plane is low-traffic, so endpoints live inside each feature.
- [x] `CreateZone` (+ `UpdateChallenge`)
- [x] `UpdateZoneUpstream`
- [x] `PauseZone` / `ResumeZone`
- [x] `AddRule`
- [x] `UpdateRule` (priority / enabled)
- [x] `DeleteRule`
- [x] `ReorderRules`
- [x] `GetZone`
- [x] `ListZones` (paginated)
- [x] `GetZoneRules`

### 1.7 Veil.Auth — Application
> Endpoint slices, same pattern as Zones.
- [x] `Login`
- [x] `RefreshAccessToken` (rotation)
- [x] `CreateApiKey` (JWT-only — an API key cannot mint keys)
- [x] `ListApiKeys`
- [x] `RevokeApiKey`

### 1.8 Veil.EdgeNodes — Application
> Endpoint slices, same pattern as Zones.
- [x] `RegisterEdgeNode` — issues one-time node token (SHA-256 hash stored)
- [x] `ListEdgeNodes` (paginated)

### 1.9 Veil.Api
- [x] `Program.cs` — DI wiring (Modulith), string enum binding, middleware pipeline
- [x] JWT + API key authentication (fallback policy: everything protected by default; login/refresh/internal endpoints opt out)
- [x] Zones endpoint group — full CRUD, pause/resume
- [x] Rules endpoint group — full CRUD, reorder
- [x] Auth endpoint group — login, refresh, API key management
- [x] EdgeNodes endpoint group — register, list
- [x] RFC 9457 error responses throughout

---

## Phase 2 — Edge Node (Rust)

### 2.1 Project skeleton
- [x] `Cargo.toml` — Tokio, Hyper (Rustls pending with TLS work)
- [x] Env var loading (dotenvy), runtime setup
- [x] Structured logging via `tracing` (JSON formatter pending)

### 2.2 Core proxy
- [x] TCP listener (configurable HTTP address; `:443` comes with TLS)
- [x] TLS termination (Rustls/ring, in-memory cert material, ALPN h2+http/1.1; `VEIL_TLS_CERT`/`VEIL_TLS_KEY`)
- [x] HTTP/1.1 + HTTP/2 (Hyper)
- [x] `RequestContext` type
- [x] Upstream connection pooling
- [x] Basic passthrough — end-to-end request works, no rules yet

### 2.3 Config sync client
- [x] Startup config pull (`GET /internal/config/{nodeId}`) with retry/backoff
- [x] Two explicit modes: control plane (no silent dev-file fallback — fail fast) vs local file
- [x] Last-known-good snapshot cache (opt-in `VEIL_CONFIG_CACHE`; written on pull/push, read when control plane unreachable at startup)
- [x] `Arc<RwLock<Config>>` in-memory store (`ConfigStore`)
- [x] Push receiver (`POST /_veil/internal/config`, constant-time node token check)
- [x] HMAC signature verification over push body (`VEIL_PUSH_HMAC_KEY`); push path hardened: credential-header precheck before body read + per-IP rate limit
- [x] Atomic config swap

### 2.4 Request pipeline
- [x] `Inspector` — real IP (XFF), user-agent (GeoIP MMDB + ASN pending)
- [x] `RuleEngine` — priority-ordered short-circuit eval (compiled decision tree + cheapest-first ordering pending)
- [x] `ActionDispatcher` — allow / block / challenge / rate_limit

### 2.5 Rate limiting
- [ ] Redis INCR + EXPIRE (Lua, atomic)
- [x] Sliding window (two adjacent fixed windows)
- [x] Per-rule key namespace

### 2.6 Request log emission
- [x] Structured log record per request
- [x] In-memory ring buffer (100k max, drop-oldest, opt-in via `VEIL_ANALYTICS_URL`)
- [x] Batch flush to `Veil.Analytics` — every 500ms or 1000 records, fire-and-forget

---

## Phase 3 — Config Push Pipeline

### 3.1 Internal config endpoint (Veil.Api)
- [x] `GET /internal/config/{nodeId}` — full zone snapshot in the edge's canonical format (fail-safe mapping: unsupported conditions/actions drop the whole rule)
- [x] `X-Veil-Node-Token` authentication (SHA-256 hash compare, marks node seen)

### 3.2 Veil.ConfigSync worker
> Hosted in Veil.Api for now (the change signal is in-process). Moves to the standalone worker once an outbox + Redis queue exist.
- [x] Event-driven change signal — domain events → transactional outbox (Wiaoj.Ddd) → Tyto in-memory bus → `ZoneConfigChanged` / `EdgeNodeRegistered` handlers (replaces the `SaveChangesInterceptor` + channel plumbing); new node registration triggers an immediate initial push
- [x] Zone config snapshot serialisation (shared with 3.1)
- [x] HTTP POST to each registered edge node, HMAC-SHA256 signed body (`ConfigSync:PushHmacKey`); per-node snapshot-hash dedupe; 5-min reconcile pass
- [x] Push result recorded in `config_push_log`
- [ ] Redis retry queue (sorted set, next-attempt score)
- [x] Exponential backoff, 3 retries per cycle (dead-letter comes with the Redis queue)
- [ ] Redis leader election lock (single active instance in K8s)

---

## Phase 4 — Challenge Engine

### 4.1 WASM PoW module (`challenge/pow_wasm/`)
- [x] Rust crate: SHA-256 iterator, nonce + counter, difficulty check
- [x] `wasm-pack build --target web --release`
- [x] Unit tests: known solutions, difficulty boundaries

### 4.2 PoW challenge flow
- [x] Challenge page — EN/TR, branded UI, progress indicator, WASM bootstrapper
- [x] Web Worker — WASM solver, posts result to main thread
- [x] `/_veil/challenge/verify` endpoint
- [x] Nonce deduplication (In-Memory implemented, pending Redis)
- [ ] Risk score evaluation (ASN, header fingerprint, timing)
- [x] Challenge token issuance (HMAC-SHA256, HttpOnly Secure cookie, 10min TTL)
- [x] Token verification on subsequent requests (in-process)

### 4.3 hCaptcha (Tier 2)
- [ ] Tier 2 challenge page + hCaptcha widget
- [ ] hCaptcha token verification
- [ ] Risk threshold config per zone

---

## Phase 5 — Certificate Lifecycle

### 5.1 Veil.Certificates — Domain & Persistence
- [x] `Certificate` entity, `CertificateId` (`crt` prefix), `CertificateStatus`, `CertificateIssued` domain event
- [x] `CertificatesDbContext` with own schema (`certificates`) + outbox
- [x] EF Core configuration + migration

### 5.2 Veil.Certificates — Application
> Endpoint slices, same pattern as Zones (no separate command/handler layer).
- [x] `RequestCertificate` (`POST /v1/certificates` — creates a Pending order; ACME worker provisions)
- [x] `GetCertificate` / `ListCertificates`
- [ ] Renewal trigger (comes with 5.4 background service)

### 5.3 ACME provisioning
- [x] Certes ACME v2 client (`AcmeProvisioningService` hosted in Veil.Api; directory URL configurable, Pebble-friendly `AcmeAllowUntrustedTls` for dev; badNonce retry on finalize)
- [x] HTTP-01 challenge token registration with edge node (`EdgeChallengePublisher` → `POST /_veil/internal/acme-challenge`, HMAC-signed, all enabled nodes)
- [x] Edge serves `/.well-known/acme-challenge/{token}` (before zone/rule logic — block rules can't break issuance)
- [x] Order polling (max 60s, configurable)
- [x] Private key encryption (AES-256-GCM) before storage (`PrivateKeyProtector`, `Certificates:EncryptionKey`)
- [x] Config push after successful provisioning — `CertificateIssued` outbox event wakes ConfigSync; snapshots carry per-zone `tls` material; edge SNI resolver (`DynamicCertResolver`) swaps certificates without listener restart

### 5.4 Certificate renewal
- [x] Renewal in the ACME worker loop — renews Active certs within `RenewBeforeDays` (30) of expiry; failed renewal keeps serving the old material

---

## Phase 6 — Analytics Pipeline

### 6.1 Veil.Analytics worker
- [x] Log ingestion endpoint (`POST /ingest`) — edge node token auth
- [x] Batch validation + enrichment (length caps, clock-drift clamp, node id stamping)
- [x] ClickHouse bulk insert (`INSERT FORMAT JSONEachRow`, schema ensured at startup)
- [x] Fire-and-forget — drop on outage, log metric
- [ ] Nightly aggregation → daily summary into PostgreSQL

### 6.2 Veil.Analytics — Query model & Application
> No EF read model — `ClickHouseReader` queries the HTTP interface directly
> (JSONEachRow, server-side parameter binding), same approach as the writer.
- [x] `ClickHouseReader` — parameterised SELECTs over the HTTP interface
- [x] `GetAnalyticsSummary` (totals + volume time series, auto bucket width)
- [x] `GetTopIps`
- [x] `GetVerdictBreakdown`
- [x] `GetChallengeStats` (issue/pass funnel + pass rate)

### 6.3 Veil.Api — Analytics endpoints
- [x] Analytics endpoint group — `AnalyticsQueryModule` (read side) hosted in Veil.Api; ingestion stays in the worker

---

## Phase 7 — Dashboard (React)

### 7.1 Scaffold
- [x] Vite + React + TypeScript (TanStack Start, Tailwind 4, bun)
- [x] TanStack Router (Query, Form + Zod pending — plain fetch hook for now)
- [ ] shadcn/ui setup
- [x] JWT auth flow against `Veil.Api` — login, localStorage tokens, refresh-and-retry on 401, dev Vite proxy (`/v1` → :5210, no CORS)

### 7.2 Zones & Rules
- [x] Zone list, zone detail (upstream/challenge cards, pause/resume, rule enable/disable/delete)
- [ ] Rule builder — condition composer (single-condition form done; multi-condition + drag-and-drop priority pending)
- [x] Create / edit / delete flows (zone create, rule add/toggle/delete, pause/resume)

### 7.3 Analytics
- [x] Request volume chart (time series — CSS bars on the overview; chart lib later)
- [x] Top IPs, verdict breakdown, challenge stats

### 7.4 Edge node management
- [x] Node list with per-node sync status (last push result surfaced on `GET /v1/edge-nodes`)
- [x] Config push log viewer (`GET /v1/edge-nodes/{id}/push-log`, paginated)

### 7.5 Real-time traffic view
- [ ] WebSocket connection
- [ ] Live request log stream

### 7.6 Certificates
- [x] Certificate list (status, expiry countdown) + request form

---

## Phase 8 — Hardening & Observability

- [ ] Prometheus metrics — edge (RPS, latency histograms, verdict counts)
- [ ] Prometheus metrics — control plane (push rate, ClickHouse write rate)
- [ ] Health check endpoints (`/healthz`, `/readyz`) on all HTTP services
- [ ] Graceful shutdown — drain in-flight requests
- [ ] Integration test suite (Testcontainers — PostgreSQL, Redis, ClickHouse)
- [ ] Edge load test baseline (`wrk2`) — validate 100k req/s target

---

## Phase 9 — Deployment

- [ ] `docker-compose.prod.yml` — resource limits, restart policies
- [ ] Kubernetes manifests — edge `DaemonSet`, control plane `Deployment`s
- [ ] Redis Cluster manifests (3 isolated clusters)
- [ ] HPA for `Veil.Api` and `Veil.Analytics`
- [ ] `Veil.ConfigSync` leader election verified under multi-replica
- [ ] TLS between internal services
- [ ] Zero-downtime deployment verified under load

---

## Phase 10 — Post-Launch

- [ ] IP reputation feed integration
- [ ] Multi-tenant zone ownership (organisations, member roles)
- [ ] Terraform provider
- [ ] Webhook support (attack detection, challenge threshold breach)
- [ ] Log export to external SIEM
- [ ] Shadow mode — simulate rule set without enforcing
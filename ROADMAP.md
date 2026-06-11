# Veil — Roadmap

---

## Phase 1 — Foundation

### 1.1 Repository & Tooling
- [x] Solution structure: `Veil.sln`, module projects, `Apps/` projects
- [x] `docker-compose.yml` — PostgreSQL, Redis (3 instances: rate-limit, tokens, config), ClickHouse
- [ ] `.env.example` with all required variables (edge done, control plane pending)
- [ ] CI pipeline (GitHub Actions) — build, test, lint on PR

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
- [ ] `User` entity, `UserId`, `UserRole`
- [ ] `ApiKey` entity, `ApiKeyId`, scopes
- [ ] JWT issuance + refresh token logic
- [ ] API key hashing + verification
- [ ] `AuthDbContext` with own schema (`auth`)
- [ ] EF Core configuration + initial migration
- [ ] Seed: default admin user

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
- [ ] `LoginCommand` + handler
- [ ] `RefreshTokenCommand` + handler
- [ ] `CreateApiKeyCommand` + handler
- [ ] `RevokeApiKeyCommand` + handler

### 1.8 Veil.EdgeNodes — Application
> Endpoint slices, same pattern as Zones.
- [x] `RegisterEdgeNode` — issues one-time node token (SHA-256 hash stored)
- [x] `ListEdgeNodes` (paginated)

### 1.9 Veil.Api
- [x] `Program.cs` — DI wiring (Modulith), string enum binding, middleware pipeline
- [ ] JWT + API key authentication middleware
- [x] Zones endpoint group — full CRUD, pause/resume
- [x] Rules endpoint group — full CRUD, reorder
- [ ] Auth endpoint group — login, refresh, API key management
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
- [ ] TLS termination (Rustls, cert from memory)
- [x] HTTP/1.1 + HTTP/2 (Hyper)
- [x] `RequestContext` type
- [x] Upstream connection pooling
- [x] Basic passthrough — end-to-end request works, no rules yet

### 2.3 Config sync client
- [x] Startup config pull (`GET /internal/config/{nodeId}`, local file fallback)
- [x] `Arc<RwLock<Config>>` in-memory store (`ConfigStore`)
- [x] Push receiver (`POST /_veil/internal/config`, constant-time node token check)
- [ ] HMAC signature verification over push body (with ConfigSync worker, Phase 3.2)
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
- [ ] Structured log record per request
- [ ] In-memory ring buffer (100k max)
- [ ] Batch flush to `Veil.Analytics` — every 500ms or 1000 records

---

## Phase 3 — Config Push Pipeline

### 3.1 Internal config endpoint (Veil.Api)
- [x] `GET /internal/config/{nodeId}` — full zone snapshot in the edge's canonical format (fail-safe mapping: unsupported conditions/actions drop the whole rule)
- [x] `X-Veil-Node-Token` authentication (SHA-256 hash compare, marks node seen)

### 3.2 Veil.ConfigSync worker
- [ ] In-process event bus — subscribe to `ZoneConfigChangedEvent`
- [ ] Zone config snapshot serialisation
- [ ] HTTP POST to each registered edge node
- [ ] Push result recorded in `config_push_log`
- [ ] Redis retry queue (sorted set, next-attempt score)
- [ ] Exponential backoff, 3 retries, dead-letter after exhaustion
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
- [ ] `Certificate` entity, `CertificateId`, `CertificateStatus`
- [ ] `CertificatesDbContext` with own schema (`certificates`)
- [ ] EF Core configuration + migration

### 5.2 Veil.Certificates — Application
- [ ] `ProvisionCertificateCommand` + handler
- [ ] `RenewCertificateCommand` + handler
- [ ] `GetCertificateQuery` + handler

### 5.3 ACME provisioning
- [ ] Certes ACME v2 client
- [ ] HTTP-01 challenge token registration with edge node
- [ ] Edge serves `/.well-known/acme-challenge/{token}`
- [ ] Order polling (max 60s)
- [ ] Private key encryption (AES-256-GCM) before storage
- [ ] Config push after successful provisioning

### 5.4 Certificate renewal
- [ ] `CertificateRenewalBackgroundService` — daily check, renew within 30 days

---

## Phase 6 — Analytics Pipeline

### 6.1 Veil.Analytics worker
- [ ] Log ingestion endpoint (`POST /ingest`) — edge node token auth
- [ ] Batch validation + enrichment
- [ ] ClickHouse bulk insert (`INSERT FORMAT JSONEachRow`)
- [ ] Fire-and-forget — drop on outage, log metric
- [ ] Nightly aggregation → daily summary into PostgreSQL

### 6.2 Veil.Analytics — Query model & Application
- [ ] `AnalyticsDbContext` — ClickHouse read model
- [ ] `GetAnalyticsSummaryQuery` + handler
- [ ] `GetTopIpsQuery` + handler
- [ ] `GetVerdictsBreakdownQuery` + handler
- [ ] `GetChallengeStatsQuery` + handler

### 6.3 Veil.Api — Analytics endpoints
- [ ] Analytics endpoint group — wire all queries

---

## Phase 7 — Dashboard (React)

### 7.1 Scaffold
- [ ] Vite + React + TypeScript
- [ ] TanStack Router, TanStack Query, TanStack Form + Zod
- [ ] shadcn/ui setup
- [ ] JWT auth flow against `Veil.Api`

### 7.2 Zones & Rules
- [ ] Zone list, zone detail
- [ ] Rule builder — condition composer, action picker, priority drag-and-drop
- [ ] Create / edit / delete flows

### 7.3 Analytics
- [ ] Request volume chart (time series)
- [ ] Top IPs, verdict breakdown, challenge stats

### 7.4 Edge node management
- [ ] Node list with per-node sync status
- [ ] Config push log viewer

### 7.5 Real-time traffic view
- [ ] WebSocket connection
- [ ] Live request log stream

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
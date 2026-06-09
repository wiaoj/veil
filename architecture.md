# Veil — Architecture

This document explains the system-level design of Veil: how the layers fit together, why certain decisions were made, and what the tradeoffs are. For per-layer details, see the layer-specific READMEs.

---

## Design Principles

**1. The edge never calls the control plane at request time.**  
All configuration is pushed to edge nodes and held in memory. A control plane outage does not degrade proxying. The control plane is a management system, not a request router.

**2. Decisions are made once, applied everywhere.**  
When a zone's config changes, a consistent snapshot is pushed to all edge nodes atomically. There is no eventual consistency in rule evaluation — every edge node runs the same version of the same rules, or it is aware that it is running a stale version and will surface that in the dashboard.

**3. Storage is partitioned by access pattern.**  
- PostgreSQL owns anything that needs transactional guarantees (zone config, certificates, users, audit log).
- Redis owns anything that needs sub-millisecond writes across many processes (rate limit counters, challenge tokens, IP reputation, config cache). Redis is split into isolated clusters by failure domain — rate limiting, challenge tokens, and config cache each use a dedicated cluster.
- ClickHouse owns anything that is append-only and queried analytically (request logs, aggregated metrics).
- In-memory (in the edge process) owns anything that is read millions of times per second and changes rarely (active config snapshot, compiled rule tree).

**4. Failure modes are explicit.**  
Every component knows what to do when its dependencies are unavailable. Edge nodes continue proxying on control plane outage. The analytics pipeline drops log batches on ClickHouse outage rather than back-pressuring the edge. The config sync worker retries with backoff and dead-letters after exhaustion.

---

## Request Lifecycle

This is the full path of a single HTTPS request through the system.

```
1.  Client DNS resolves zone hostname → Veil edge node IP
2.  Client opens TCP connection to edge node :443
3.  Edge node accepts; TLS handshake (Rustls, certificates loaded from memory)
4.  HTTP request decoded (Hyper)
5.  RequestContext created — request metadata extracted:
      real IP (XFF trust chain), country (MMDB lookup), ASN, user-agent, path, method
6.  Rule engine evaluates zone's compiled rule tree:
      a. For each rule in priority order:
           - Evaluate conditions (IP, country, rate limit, etc.)
           - First match → apply action and stop
      b. No match → implicit allow
7.  Action dispatched:
      allow     → Router selects upstream, forwards request, streams response
      block     → 403 response generated in-process
      challenge → Challenge engine invoked (see below)
      rate_limit → 429 response, Retry-After header set
8.  Response sent to client
9.  Request log record queued for async delivery to Veil.Analytics
```

### Challenge Lifecycle

When the rule engine issues a `challenge` verdict:

```
1.  Edge checks for valid challenge cookie on the request
      → if present and signature valid: treat as allow, proceed
      → if absent or invalid: continue below

2.  Edge returns Tier 1 challenge page (HTML + WASM bundle)
      JS loads the compiled WebAssembly PoW module (veil_pow.wasm)
      WASM spawns a Web Worker and iterates: SHA256(nonce + counter) until leading N bits are zero
      Browser POSTs solution to /_veil/challenge/verify

3.  Edge verifies solution:
      - Reconstructs expected hash, checks difficulty
      - Checks nonce hasn't been used before (Redis SET NX, TTL = challenge TTL)
      - Evaluates request risk score (header fingerprint, timing, ASN)

4.  Risk score below threshold:
      → Issue challenge token (HMAC-SHA256, zone key, 10min TTL)
      → Set as HttpOnly Secure cookie
      → 302 redirect to original URL

    Risk score above threshold:
      → Serve Tier 2 challenge page (hCaptcha widget)
      → On CAPTCHA pass: verify token with hCaptcha API
      → If valid: issue challenge cookie, redirect
      → If invalid: 403
```

---

## Config Push Flow

When an operator changes a zone's rules via the dashboard:

```
1.  Dashboard POSTs to Veil.Api (PATCH /zones/{id}/rules)
2.  Command handler validates, updates PostgreSQL, commits
3.  Domain event ZoneConfigChangedEvent raised in-process
4.  Veil.ConfigSync handles the event:
      a. Serialises full zone config snapshot (rules, upstreams, cert material)
      b. Fetches list of registered edge nodes from PostgreSQL
      c. POSTs snapshot to each edge node's /_veil/internal/config endpoint
      d. Records push result (success/failure) in config_push_log
5.  Edge node receives snapshot:
      a. Validates signature (shared HMAC key)
      b. Deserialises, compiles rule tree
      c. Acquires write lock, swaps config, releases lock
      d. Returns 204
6.  Dashboard polls config_push_log and shows sync status per edge node
```

If step 5 fails (edge unreachable), Veil.ConfigSync queues a retry in Redis (sorted set, scored by next-attempt timestamp) and processes it on the next worker tick. After 3 failures, the push is dead-lettered and a `ConfigSyncFailedAlert` is raised.

Edge nodes also perform a full config pull on startup (`GET /internal/config/{nodeId}` with their node token), so a node that was offline during a push recovers automatically on restart.

---

## Certificate Lifecycle

```
1.  Zone created with hostname "api.example.com"
2.  Veil.Api triggers ACME provisioning asynchronously
3.  CertificateProvisioner:
      a. Creates ACME account (or reuses existing)
      b. Places order for "api.example.com"
      c. ACME server issues HTTP-01 challenge (well-known token)
      d. CertificateProvisioner registers token with edge node
         (edge node serves token at /.well-known/acme-challenge/{token})
      e. Signals ACME server to validate
      f. Polls for order completion (max 60s)
      g. Downloads certificate chain and private key
      h. Encrypts private key (AES-256-GCM) and stores in PostgreSQL
      i. Pushes cert + encrypted key to edge nodes via config push
4.  CertificateRenewalBackgroundService runs daily:
      - Queries PostgreSQL for certs expiring within 30 days
      - Triggers RenewCertificateCommand for each
      - Renewal follows the same flow as initial provisioning
```

---

## Rate Limiting Implementation

Rate limiting is implemented at the edge node using Redis atomic operations. The control plane defines the rule parameters; the edge executes them.

For a rule `rate_limit: 100 requests per 60 seconds per IP`:

```
key = "rl:{zone_id}:{rule_id}:{client_ip}:{window}"
where window = floor(unix_timestamp / 60)

MULTI
  INCR key
  EXPIRE key 60
EXEC

if result[0] > 100:
    verdict = rate_limit
```

The sliding window approximation uses two adjacent fixed windows weighted by position within the current window. This avoids the thundering-herd problem at window boundaries without requiring a sorted set per IP.

Counter keys are stored in a Redis cluster dedicated to rate limiting (separate from the config cache and challenge token store) to isolate failure domains.

---

## Edge Node Registration

Edge nodes are registered in the control plane before they can receive config pushes or serve traffic. Registration is manual (via the dashboard or API):

```
POST /api/v1/edge-nodes
{
  "name": "edge-eu-01",
  "url": "http://10.0.0.10:9000",   ← internal control plane endpoint
  "token": "<shared secret>"
}
```

The token is stored as a HMAC key in the database. All internal communication between the control plane and edge nodes uses this token (sent as `X-Veil-Node-Token`). Edge nodes verify it on config push; the control plane verifies it on log ingestion.

---

## Deployment Topology

### Docker Compose (single host)

All components run on one machine. Suitable for development and low-traffic production:

```
┌─────────────────────────────────────────────────┐
│  Docker host                                    │
│                                                 │
│  edge (Rust)          :80, :443                 │
│  veil-api (.NET)      :5000 (internal)          │
│  veil-config-sync     (worker)                  │
│  veil-analytics       :5001 (internal)          │
│  dashboard (nginx)    :5173                     │
│  postgres             :5432                     │
│  redis                :6379                     │
│  clickhouse           :8123                     │
└─────────────────────────────────────────────────┘
```

### Kubernetes (multi-node)

Edge nodes run as a `DaemonSet` — one per cluster node — so each node handles its own ingress traffic. Control plane components run as `Deployments` with horizontal scaling. Storage is managed services or operators.

```
┌─────────────────────────────────────────────────────────────────┐
│  Kubernetes cluster                                             │
│                                                                 │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐          │
│  │  node-eu-01  │  │  node-eu-02  │  │  node-us-01  │  ...     │
│  │  edge (DS)   │  │  edge (DS)   │  │  edge (DS)   │          │
│  └──────────────┘  └──────────────┘  └──────────────┘          │
│                                                                 │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │  Control plane namespace                                 │   │
│  │  veil-api (Deployment, 2+ replicas)                      │   │
│  │  veil-config-sync (Deployment, 1 replica — leader elect) │   │
│  │  veil-analytics (Deployment, 2+ replicas)                │   │
│  │  dashboard (Deployment)                                  │   │
│  └──────────────────────────────────────────────────────────┘   │
│                                                                 │
│  Managed: PostgreSQL, Redis Cluster, ClickHouse                 │
└─────────────────────────────────────────────────────────────────┘
```

`Veil.ConfigSync` uses a Redis-based leader election lock so only one instance processes config push events at a time, avoiding duplicate pushes.

---

## Architecture Decision Records

ADRs are stored in `docs/adr/`. Key decisions:

| # | Decision | Status |
|---|---|---|
| 001 | Rust for data plane, .NET for control plane | Accepted |
| 002 | Push-based config sync (not pull polling) | Accepted |
| 003 | ClickHouse for request log analytics | Accepted |
| 004 | Two-tier challenge (PoW + hCaptcha, not Turnstile) | Accepted |
| 007 | WebAssembly for PoW solver (Rust → wasm-pack, replaces pure JS) | Accepted |
| 008 | Flat domain modules (Veil.Zones, Veil.Certificates, etc.) instead of shared Domain/Application layers | Accepted |
| 005 | Zone as root aggregate (rules owned by zone) | Accepted |
| 006 | Encrypted cert key storage in PostgreSQL | Accepted |

See individual ADR files for context, alternatives considered, and consequences.
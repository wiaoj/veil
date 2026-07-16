# Veil — Usage Guide

How the system works end to end, and how to configure and operate it.

---

## 1. How it works

Veil is a Layer-7 WAF / bot-mitigation reverse proxy with a separate control
plane. Three independently deployable parts:

```
                         ┌─────────────────────────────┐
   Browser / client      │        Control plane        │
        │                │  Veil.Api  (config, auth,   │
        ▼                │            certs, analytics │
┌──────────────┐  config │            read API)        │
│   Edge node  │◀────────│  Veil.Analytics.Worker      │
│   (Rust)     │  push   │            (log ingest)     │
│              │────────▶│  PostgreSQL · Redis ·       │
│  TLS │ WAF   │ logs    │  ClickHouse                 │
└──────┬───────┘         └─────────────────────────────┘
       │ allow                         ▲
       ▼                               │ JWT
   Upstream origin              Dashboard (React)
```

**Request path on the edge** (in-process, microseconds, no control-plane round trip):

```
TCP/TLS → Inspector → Rule engine → Managed signatures → Router → Upstream
                          │                  │
                    Challenge (PoW / Tier-2 interaction) · Block · Rate-limit
```

1. **TLS termination** — rustls; per-zone certs picked by SNI. On HTTPS the
   ClientHello is peeked to compute a **JA3** fingerprint.
2. **Inspector** — extracts client IP (XFF-aware), host, method, path, query,
   headers; enriches with **GeoIP country/ASN** and JA3 when available.
3. **Rule engine** — per-zone rules in priority order, short-circuit. Conditions
   AND within a rule (see §4).
4. **Managed signatures** — if custom rules allow, an OWASP-CRS-style set scans
   for SQLi / XSS / traversal (§4.3).
5. **Verdict** → forward to upstream, **block**, **challenge**, or **rate-limit**.

The control plane is never in the hot path: it stores config and **pushes**
signed snapshots to edge nodes on change.

---

## 2. Running locally (development)

Prereqs: .NET 10 SDK, Rust (stable), bun, and PostgreSQL + Redis + ClickHouse
(via `docker compose` or local containers — the dev infra runs on Postgres
:5432, ClickHouse :8123, Redis :6379).

### Fastest path: .NET Aspire

`Veil.AppHost` starts the infra (Postgres, ClickHouse), applies migrations + seed
(`Veil.DbMigrator`), and runs `Veil.Api` + `Veil.Analytics.Worker` wired for the
Tyto bus/RPC — one command. Stop any hand-started `veil-*` containers first.

```bash
dotnet run --project src/Apps/Veil.AppHost
# then run the edge node and dashboard (steps 5 and 4 below)
```

### Manual (run each piece by hand)

```bash
# 1. Start infra (PostgreSQL, Redis, ClickHouse)
docker compose up -d

# 2. Apply database migrations (control plane)
dotnet ef database update --project src/Veil.Auth     --startup-project src/Apps/Veil.Api --context AuthDbContext
dotnet ef database update --project src/Veil.Zones    --startup-project src/Apps/Veil.Api --context ZonesDbContext
dotnet ef database update --project src/Veil.EdgeNodes --startup-project src/Apps/Veil.Api --context EdgeNodesDbContext
dotnet ef database update --project src/Veil.Certificates --startup-project src/Apps/Veil.Api --context CertificatesDbContext

# 3. Run the control plane (uses appsettings.Development.json)
dotnet run --project src/Apps/Veil.Api            # :5210
dotnet run --project src/Apps/Veil.Analytics.Worker  # :5001

# 4. Run the dashboard
cd dashboard && bun install && bun run dev         # :3000 (proxies /v1 → :5210)

# 5. Run an edge node (local-file config mode)
cd edge && cargo run                               # reads veil.json (VEIL_CONFIG_PATH)
```

Default dev admin (from `appsettings.Development.json`):
`admin@veil.local` / `admin-dev-password`.

For production, copy [`.env.example`](../.env.example) → `.env` and
[`edge/.env.example`](../edge/.env.example) and fill in real secrets. See
[deploy/k8s/README.md](../deploy/k8s/README.md) for Kubernetes.

---

## 3. Authentication & API

```bash
# Log in → access + refresh tokens
curl -X POST http://localhost:5210/v1/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"email":"admin@veil.local","password":"admin-dev-password"}'
# → { "accessToken": "...", "refreshToken": "...", "expiresInSeconds": 900 }

# Use the access token
curl http://localhost:5210/v1/zones -H "Authorization: Bearer $ACCESS"

# Rotate a refresh token (single-use)
curl -X POST http://localhost:5210/v1/auth/refresh -d '{"refreshToken":"..."}'

# API keys (machine credentials; created by a user session, not by another key)
curl -X POST http://localhost:5210/v1/api-keys -H "Authorization: Bearer $ACCESS" \
  -d '{"name":"ci","scopes":["zones:read"]}'   # plaintext key shown once
```

**Brute-force protection:** after `Auth:MaxFailedLoginAttempts` (default 5)
consecutive failures an account is locked for `Auth:LockoutMinutes` (default 15).
Locked accounts return the same `401` as a wrong password (no enumeration).

**Audit log:** logins (success / failure / lockout) and API-key create/revoke
are recorded append-only in `auth.audit_events` (actor, source IP, target,
outcome).

---

## 4. Zone configuration

A zone routes a set of hostnames to an upstream and carries the rules. Manage it
from the dashboard, or over the API as below. Anything you change is pushed to
the edge fleet within a second (see 5.1).

### Managing zones over the API

All calls need a session token (§3). Enums are sent as strings; bodies are
camelCase JSON.

```bash
H="Authorization: Bearer $ACCESS"
API=http://localhost:5210
JSON='Content-Type: application/json'

# ── Create ────────────────────────────────────────────────────────────
curl -X POST $API/v1/zones -H "$H" -H "$JSON" -d '{
  "hostname": "example.com",
  "upstream": {
    "targets": [{ "url": "http://10.0.0.5:3000", "weight": 1 }],
    "strategy": "RoundRobin",
    "connectTimeoutMs": 5000,
    "responseTimeoutMs": 30000,
    "passHostHeader": true
  },
  "challenge": { "enabled": true, "difficulty": 20, "expirationSeconds": 600, "riskThreshold": 70 }
}'
# → { "id": "zon_…", "hostname": "example.com", "status": "Provisioning" }
ZONE=zon_…

# ── Inspect ───────────────────────────────────────────────────────────
curl $API/v1/zones        -H "$H"           # list (paginated)
curl $API/v1/zones/$ZONE  -H "$H"           # detail: upstream, challenge, rules,
                                            #   cacheEnabled, shadow, managedRules
curl $API/v1/zones/$ZONE/rules -H "$H"      # rules only

# ── Rules ─────────────────────────────────────────────────────────────
# Lower priority is evaluated first; the first matching terminal rule wins.
curl -X POST $API/v1/zones/$ZONE/rules -H "$H" -H "$JSON" -d '{
  "name": "block RU", "priority": 20, "action": "Block",
  "conditions": [{ "type": "country", "value": "RU" }]
}'
# → { "id": "rul_…", … }

curl -X POST $API/v1/zones/$ZONE/rules -H "$H" -H "$JSON" -d '{
  "name": "rate limit the API", "priority": 40, "action": "RateLimit",
  "conditions": [{ "type": "path_match", "value": "/api", "mode": "prefix" }],
  "rateLimit": { "requests": 100, "windowSecs": 60 }
}'

# Conditions are AND-ed: this one needs both to match.
curl -X POST $API/v1/zones/$ZONE/rules -H "$H" -H "$JSON" -d '{
  "name": "challenge scrapers on /search", "priority": 30, "action": "Challenge",
  "conditions": [
    { "type": "path_match", "value": "/search", "mode": "prefix" },
    { "type": "user_agent", "value": "python" }
  ]
}'

curl -X PUT    $API/v1/zones/$ZONE/rules/order -H "$H" -H "$JSON" \
     -d '{ "ruleIds": ["rul_a", "rul_b", "rul_c"] }'      # renumbers priorities
curl -X PATCH  $API/v1/zones/$ZONE/rules/rul_a -H "$H" -H "$JSON" \
     -d '{ "isEnabled": false }'                          # or { "priority": 15 }
curl -X DELETE $API/v1/zones/$ZONE/rules/rul_a -H "$H"

# ── Zone settings (each replaces the whole block) ──────────────────────
curl -X PUT $API/v1/zones/$ZONE/shadow        -H "$H" -H "$JSON" -d '{ "enabled": true }'
curl -X PUT $API/v1/zones/$ZONE/cache         -H "$H" -H "$JSON" -d '{ "enabled": true }'
curl -X PUT $API/v1/zones/$ZONE/managed-rules -H "$H" -H "$JSON" -d '{
  "sqlInjection": true, "xss": true, "pathTraversal": true,
  "inspectBody": true, "action": "block"
}'
curl -X PUT $API/v1/zones/$ZONE/challenge -H "$H" -H "$JSON" -d '{
  "enabled": true, "difficulty": 22, "expirationSeconds": 600,
  "requireCaptcha": false, "riskThreshold": 60
}'
# Add a second target → weighted round-robin (4.7)
curl -X PUT $API/v1/zones/$ZONE/upstream -H "$H" -H "$JSON" -d '{
  "targets": [
    { "url": "http://10.0.0.5:3000", "weight": 3 },
    { "url": "http://10.0.0.6:3000", "weight": 1 }
  ],
  "strategy": "RoundRobin", "connectTimeoutMs": 5000,
  "responseTimeoutMs": 30000, "passHostHeader": true
}'

# ── Lifecycle ─────────────────────────────────────────────────────────
curl -X POST   $API/v1/zones/$ZONE/activate -H "$H"   # Provisioning → Active
curl -X POST   $API/v1/zones/$ZONE/pause    -H "$H"   # pass traffic through unfiltered
curl -X POST   $API/v1/zones/$ZONE/resume   -H "$H"
curl -X DELETE $API/v1/zones/$ZONE          -H "$H"   # edges drop it on the next push
```

**Values:** `action` = `Allow` · `Block` · `Challenge` · `RateLimit` · `Log` —
`strategy` = `RoundRobin` · `IpHash` · `LeastConnections` — condition `type` see
4.2 (`asn` uses the `asn` field, `header`/`header_regex` also need `name`,
`path_match` takes `mode`: `prefix`/`exact`).

### Rolling out a rule set safely

```bash
curl -X PUT  $API/v1/zones/$ZONE/shadow -H "$H" -H "$JSON" -d '{ "enabled": true }'
# … add rules, then watch what they *would* have done:
#   dashboard /live, or the verdicts shadow_block / shadow_challenge /
#   shadow_rate_limited in the request log. Nothing is enforced yet.
curl -X PUT  $API/v1/zones/$ZONE/shadow -H "$H" -H "$JSON" -d '{ "enabled": false }'
```

### The pushed config shape (reference)

This is what the control plane pushes to edge nodes — and the same shape
`veil.json` takes in local-file mode:

```jsonc
{
  "trust_forwarded_headers": false,
  "zones": [{
    "name": "example",
    "hosts": ["example.com", "www.example.com"],

    // Either a bare URL (single target) …
    "upstream": "http://127.0.0.1:3000",
    // … or several, load balanced (see 4.7):
    // "upstream": {
    //   "targets": [
    //     { "url": "http://10.0.0.5:3000", "weight": 3 },
    //     { "url": "http://10.0.0.6:3000", "weight": 1 }
    //   ],
    //   "strategy": "round_robin"     // round_robin | ip_hash | least_connections
    // },

    "rules": [
      { "id": "allow-office", "priority": 10, "action": "allow",
        "conditions": [{ "type": "ip", "value": "203.0.113.0/24" }] },

      { "id": "block-ru", "priority": 20, "action": "block",
        "conditions": [{ "type": "country", "value": "RU" }] },

      { "id": "challenge-login", "priority": 30, "action": "challenge",
        "conditions": [{ "type": "path_exact", "value": "/login" }] },

      { "id": "api-rate", "priority": 40, "action": "rate_limit",
        "conditions": [{ "type": "path_prefix", "value": "/api" }],
        "rate_limit": { "requests": 100, "window_secs": 60 } },

      { "id": "block-badbot", "priority": 50, "action": "block",
        "conditions": [{ "type": "ja4", "value": "t13d1516h2_8daaf6152771_b186095e22b6" }] }
    ],

    "managed_rules": {
      "sql_injection": true, "xss": true, "path_traversal": true,
      "inspect_body": true, "action": "block"
    },

    "challenge": {
      "tier2_risk_threshold": 70,
      "base_difficulty": 20,    // optional; else VEIL_POW_DIFFICULTY
      "token_ttl_secs": 600     // optional; else VEIL_CHALLENGE_TTL
    },

    "shadow": false,            // dry-run the whole zone (4.5)
    "cache": {}                 // presence enables response caching (4.8)
  }]
}
```

> Every field above is also settable from the dashboard (zone detail cards), which
> pushes it to the fleet. `veil.json` is the same shape, for local-file mode.

> **Two vocabularies — don't mix them.** The management API and the edge config
> are different contracts, and 4.1–4.2 below describe the **edge** one:
>
> | | Management API (curl / dashboard) | Edge config (`veil.json`, pushed snapshot) |
> |---|---|---|
> | actions | `Block`, `RateLimit`, … (PascalCase) | `block`, `rate_limit`, … |
> | single IP / CIDR | `ip_match` / `ip_range` | `ip` (accepts both) |
> | path | `path_match` + `mode: prefix\|exact` | `path_prefix` / `path_exact` |
> | user agent | `user_agent` | `user_agent_contains` |
>
> The control plane translates as it builds the snapshot. Conditions the edge
> cannot enforce are dropped **fail-safe** — the whole rule is skipped rather than
> enforced in a weakened form.

### 4.1 Actions
`allow` · `block` · `challenge` · `rate_limit` (requires `rate_limit` params).
Rules are evaluated by ascending `priority`; the first matching terminal rule
wins. A `rate_limit` rule only terminates when the limit is exceeded.

### 4.2 Conditions (AND-ed within a rule)
| Type | Matches |
|---|---|
| `ip` | client IP / CIDR |
| `path_prefix`, `path_exact` | request path |
| `method` | HTTP method |
| `header` `{name,value}` | exact header value |
| `user_agent_contains` | substring (case-insensitive) |
| `path_regex`, `query_regex`, `header_regex`, `body_regex` | regex (compiled at load) |
| `country` | GeoIP ISO country code (needs `VEIL_GEOIP_PATH`) |
| `asn` | GeoIP ASN as a decimal string, e.g. `"64500"` (needs `VEIL_GEOIP_ASN_PATH`) |
| `ja3` | TLS JA3 fingerprint (MD5 hex) — **HTTPS only** |
| `ja4` | TLS JA4 fingerprint (FoxIO) — **HTTPS only**; more robust than JA3 against extension-order randomisation |

`body_regex` and `managed_rules.inspect_body` buffer the request body (≤256 KiB).

> **`ja3`/`ja4` only work when the edge terminates TLS itself.** Behind a
> TLS-terminating load balancer there is no ClientHello to fingerprint, so both
> are absent and any rule using them silently never matches.

### 4.3 Managed signatures (WAF)
`managed_rules` enables built-in SQLi / XSS / traversal signature families,
scanned over URL, query, headers and (optional) body — raw and percent-decoded.
Runs after custom rules; a match yields `block` or `challenge`. Starter set, not
a full CRS port.

### 4.4 Rate limiting
Per-rule, per-IP sliding window. Set `VEIL_RATELIMIT_REDIS_URL` on edge nodes to
share counters across the fleet (atomic Lua); unset → per-process. Redis errors
fail open.

### 4.5 Shadow mode & IP reputation
- **Shadow mode** — set `"shadow": true` on a zone to dry-run it: rules and
  managed signatures are evaluated and the would-be verdict is logged
  (`shadow_block`, `shadow_challenge`, `shadow_rate_limited`) but every request
  is forwarded. Use it to validate a new rule set against live traffic risk-free.
- **IP reputation** — point `VEIL_IP_REPUTATION_PATH` at a file of bad IPs/CIDRs
  (one per line, `#` comments). Listed client IPs are blocked (`ip_reputation`)
  *after* the zone's own rules, so allow/block rules keep precedence.

### 4.6 Challenge tiers
- **Tier 1** — Proof-of-Work (WASM), difficulty scaled by a risk score.
- **Tier 2** — when the risk score ≥ `challenge.tier2_risk_threshold`: elevated
  PoW **plus** a behavioural interaction check (mouse/touch telemetry scored
  server-side). Self-hosted, no third party. The behavioural signal is a
  cost/friction layer; the hard floor is the PoW.

`base_difficulty` and `token_ttl_secs` override the engine defaults per zone. The
difficulty a challenge was issued at is bound to its nonce, so a client cannot
solve below the level it was served; the token TTL rides on the nonce too, so the
(pre-zone) verify endpoint honours the zone's setting.

**Risk score** (`0..100`, from the header fingerprint + TLS):

| Signal | Points |
|---|---|
| no `User-Agent` | 45 |
| UA contains a bot token (`curl`, `python`, `scrapy`, `headless`, …) | 35 |
| UA shorter than 16 chars | 10 |
| missing `Accept` / `Accept-Language` / `Accept-Encoding` | 15 / 15 / 10 |
| **UA claims a browser but the TLS ClientHello disagrees** (no SNI, or ALPN ≠ `h2`) | 30 |

The header signals are trivially forged — a bot sending a full browser header set
scores 0 on all of them. The last one is not: the fingerprint is a property of the
client's TLS stack, so a tool forging a Chrome UA while handshaking with its
library defaults contradicts itself. Score → up to +4 PoW bits (each bit doubles
the work).

### 4.7 Load balancing
A zone may list several upstream targets with weights:

| Strategy | Behaviour |
|---|---|
| `round_robin` (default) | weighted round-robin; a target with `weight: 3` gets 3× the requests |
| `ip_hash` | sticky per client IP |
| `least_connections` | **currently behaves as `round_robin`** — the edge has no upstream in-flight counters yet |

Bound the origin with `VEIL_UPSTREAM_CONNECT_TIMEOUT_SECS` (default 10) and
`VEIL_UPSTREAM_TIMEOUT_SECS` (default 30, time-to-response-headers; body streaming
is not capped).

### 4.8 Response caching (opt-in)
Presence of `"cache": {}` enables it. Deliberately strict — a security proxy must
not turn caching into a leak or a poisoning vector, so it caches **only**:

- `GET` requests with **no** `Authorization` and **no** `Cookie`
- `200` responses with **no** `Set-Cookie` and **no** `Vary`
- responses with an **explicit** `Cache-Control: s-maxage`/`max-age`
  (no heuristic freshness; `no-store`/`no-cache`/`private` disable it)
- bodies with a known `Content-Length` within `max_body_bytes` (default 1 MiB)

Responses carry `X-Veil-Cache: HIT|MISS`. The lookup happens **after** rule
evaluation, so a blocked or challenged request never serves from cache.

### 4.9 Origin error pages
Upstream failures render the branded error page (HTML for browsers,
`application/problem+json` otherwise, EN/TR) and carry an `X-Veil-Error` header:

| Reason | Status |
|---|---|
| `web_server_down` (connection refused) | 502 |
| `origin_unreachable` (DNS/route) | 502 |
| `origin_ssl_handshake` / `origin_ssl_invalid` (https origin) | 502 |
| `origin_bad_response` / `bad_gateway` | 502 |
| `origin_timeout` | 504 |
| `edge_not_ready` (no config loaded) | 503 |

Standard status codes on the wire — not Cloudflare-style 52x, which confuse
intermediaries and monitoring. The origin's *own* 5xx passes through untouched.

---

## 5. Edge node modes

- **Control-plane mode** — set `VEIL_CONTROL_PLANE_URL` + `VEIL_NODE_ID` +
  `VEIL_NODE_TOKEN`. The node pulls config at startup (with retry/backoff +
  last-known-good cache) and receives signed pushes at runtime.
- **Local-file mode** — none of the above set → reads `VEIL_CONFIG_PATH`
  (`veil.json`). For development.

Register a node to get its id + one-time token:
```bash
curl -X POST http://localhost:5210/v1/edge-nodes -H "Authorization: Bearer $ACCESS" \
  -d '{"name":"edge-1","address":"10.0.0.5"}'   # → edg_… id + vnt_… token (shown once)
```

See [`edge/.env.example`](../edge/.env.example) for all edge variables (TLS,
GeoIP, rate-limit Redis, analytics, challenge tuning).

### 5.1 How config reaches a node

The control plane **pushes**; it is never in the request path. A change raises a
domain event → outbox → in-process bus → the push loop wakes (a burst of changes
coalesces into one push), builds the snapshot, HMAC-SHA256 signs the body and
POSTs it to each node. A 5-minute reconcile pass converges nodes that missed one.

Pushing requires knowing *where* a node is — and a registered node carries two
facts with different lifetimes: its **identity** (token hash: durable, revocable,
in PostgreSQL) and its **location** (address: ephemeral as soon as the fleet is
dynamic). `ConfigSync:Discovery:Mode` picks how location is resolved:

| Mode | Resolves to | Use for |
|---|---|---|
| `Static` (default) | the address recorded at registration | VMs, bare metal, docker-compose |
| `Dns` | every A record behind `Discovery:DnsName` | Kubernetes: point it at a **headless** Service and the push reaches every ready pod. Kubernetes is already the registry — no API access or RBAC needed |
| `Redis` | TTL'd self-registrations under `Discovery:RedisKeyPrefix` (`{prefix}{id}` → `{"address":"…"}`) | dynamic non-Kubernetes fleets; a node that stops renewing expires on its own, so there is no reaper |

> In Kubernetes a DaemonSet shares one node identity across N ephemeral pod IPs,
> so `Static` would only ever reach one pod and the rest would serve stale config
> until restart (the edge pulls only at startup). Use `Dns` — see
> [`deploy/k8s/README.md`](../deploy/k8s/README.md).

---

## 6. Certificates (ACME)

Set `Certificates:AcmeDirectoryUrl` + `Certificates:EncryptionKey` to enable the
ACME worker. Request a cert; the worker provisions via HTTP-01 (served by the
edge before any rule), then pushes the material to edge nodes (picked by SNI).

```bash
curl -X POST http://localhost:5210/v1/certificates -H "Authorization: Bearer $ACCESS" \
  -d '{"hostname":"example.com"}'
```

Active certs renew automatically within `Certificates:RenewBeforeDays` (30).

---

## 7. Observability

- **Prometheus** — edge and control plane expose `GET /metrics`:

  | Metric | Where | Notes |
  |---|---|---|
  | `veil_requests_total{verdict}` | edge | `allow`, `block`, `challenge`, `challenge_pass`, `rate_limited`, `no_zone`, `not_ready` |
  | `veil_request_duration_seconds` | edge | histogram |
  | `veil_upstream_errors_total{reason}` | edge | labelled by the classified origin failure (see 4.9), so 502-vs-504-vs-DNS is distinguishable |
  | `veil_config_push_total{result}` | control plane | `success` / `failure` |
  | `veil_clickhouse_rows_written_total`, `veil_clickhouse_write_failures_total` | worker | |

- **Health** — `GET /healthz` (liveness), `GET /readyz` (readiness). Edge
  readiness requires a loaded zone config.
- **OpenTelemetry** — set `OTEL_EXPORTER_OTLP_ENDPOINT` to export distributed
  traces + metrics from the control plane and worker (opt-in; no-op when unset).
- **Analytics** — edge ships request logs to the worker → ClickHouse; query via
  the control plane analytics endpoints, or watch the live SSE stream at
  `/v1/analytics/stream` (dashboard `/live`).
- **SIEM export** — set `Siem:Endpoint` on the analytics worker to mirror every
  request-log batch as NDJSON to an external SIEM (fire-and-forget; a SIEM
  outage never affects ingestion).
- **Attack webhooks** — set `VEIL_WEBHOOK_URL` on edge nodes to get a webhook
  when enforced attack verdicts in a zone cross `VEIL_WEBHOOK_THRESHOLD` within
  `VEIL_WEBHOOK_WINDOW_SECS`, then quiet for `VEIL_WEBHOOK_COOLDOWN_SECS`
  (threshold+cooldown avoids flooding; optional HMAC signature).

---

## 8. Operating notes

- **Signing-key rotation (zero-downtime):** add a new entry to `Auth:SigningKeys`,
  point `Auth:ActiveSigningKeyId` at it (new tokens carry its `kid`), then drop
  the old key once outstanding access tokens have expired. All ring keys validate
  meanwhile.
- **Migrations are manual:** run `dotnet ef database update` per context after
  deploying schema changes (no auto-migrate).
- **Scope:** Veil is L7 only — front it with network-layer DDoS protection for
  volumetric (L3/L4) attacks.

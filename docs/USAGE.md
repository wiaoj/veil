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

A zone routes a set of hostnames to an upstream and carries the rules. This is
the canonical shape pushed to edge nodes (and the local `veil.json` in dev):

```jsonc
{
  "trust_forwarded_headers": false,
  "zones": [{
    "name": "example",
    "hosts": ["example.com", "www.example.com"],
    "upstream": "http://127.0.0.1:3000",

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
        "conditions": [{ "type": "ja3", "value": "<ja3-md5-hash>" }] }
    ],

    "managed_rules": {
      "sql_injection": true, "xss": true, "path_traversal": true,
      "inspect_body": true, "action": "block"
    },

    "challenge": { "tier2_risk_threshold": 70 }
  }]
}
```

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
| `ja3` | TLS JA3 fingerprint hash (HTTPS only) |

`body_regex` and `managed_rules.inspect_body` buffer the request body (≤256 KiB).

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

- **Prometheus** — edge and control plane expose `GET /metrics`.
- **Health** — `GET /healthz` (liveness), `GET /readyz` (readiness).
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

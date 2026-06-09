# veil/src — Control Plane

The .NET 9 control plane. It owns the authoritative state of every zone, rule, certificate, and user in the system. It does not touch the request hot path — its job is to make the edge nodes as smart as possible so they can operate independently.

---

## Responsibilities

- Zone and rule CRUD (the source of truth)
- API key and user authentication (JWT issuance, RBAC)
- Automatic TLS certificate issuance and renewal via ACME
- Pushing config snapshots to edge nodes when anything changes
- Ingesting structured request logs from edge nodes and writing them to ClickHouse
- Serving the REST API consumed by the dashboard

---

## Project Structure

This layer is organised as a flat collection of domain modules. Each module owns its own entities, use cases, and infrastructure concerns. Modules communicate only through their public contracts — no module reaches into another module's internals.

```
src/
├── Veil.Zones/            # Zone and rule management — the core domain
├── Veil.Certificates/     # TLS certificate lifecycle (ACME provisioning, renewal)
├── Veil.Analytics/        # Request log ingestion and query model
├── Veil.Auth/             # Users, API keys, JWT issuance, RBAC
├── Veil.EdgeNodes/        # Edge node registry, config push, sync log
├── Veil.Shared/           # Cross-cutting utilities (no domain logic)
│
└── Apps/
    ├── Veil.Api/          # ASP.NET Core — REST API host
    ├── Veil.ConfigSync/   # Worker service — pushes config to edge nodes
    └── Veil.Analytics/    # Worker service — ingests edge logs into ClickHouse
```

Each module (`Veil.Zones`, `Veil.Certificates`, etc.) contains its own:
- `Domain/` — entities, value objects, domain events, repository interfaces
- `Application/` — CQRS command/query handlers, service interfaces, DTOs
- `Infrastructure/` — EF Core configurations, Redis, external HTTP clients
- `Contracts/` — public types exposed to other modules and to the Apps layer

Dependencies flow inward within each module. The Apps layer wires everything together but owns no domain logic.

---

## Domain Model

### Veil.Zones

The core module. A zone represents one protected domain or subdomain.

```csharp
public sealed class Zone : AggregateRoot
{
    public ZoneId Id { get; }
    public string Hostname { get; }          // "api.example.com"
    public UpstreamConfig Upstream { get; }  // target origin(s)
    public ChallengeConfig Challenge { get; }
    public IReadOnlyList<Rule> Rules { get; }
    public ZoneStatus Status { get; }        // Active, Paused, Provisioning
}
```

Rules are owned by the zone and ordered by priority. Adding, removing, or reordering rules raises a `ZoneConfigChangedEvent`, which triggers a config push to edge nodes.

```csharp
public sealed class Rule : Entity
{
    public RuleId Id { get; }
    public string Name { get; }
    public int Priority { get; }
    public IReadOnlyList<RuleCondition> Conditions { get; }
    public RuleAction Action { get; }        // Allow, Block, Challenge, RateLimit, Log
    public bool IsEnabled { get; }
}
```

Available condition types: `IpMatch`, `IpRangeMatch`, `CountryMatch`, `AsnMatch`, `PathMatch`, `PathRegexMatch`, `HeaderMatch`, `UserAgentMatch`, `RequestRateExceeds`.

### Veil.Certificates

Represents a TLS certificate for a zone. Certificates are provisioned automatically via ACME when a zone is created (if DNS is pointed at Veil). Renewal is handled by `CertificateRenewalBackgroundService`, which checks expiry daily and renews any certificate within 30 days of expiry.

```csharp
public sealed class Certificate : Entity
{
    public CertificateId Id { get; }
    public ZoneId ZoneId { get; }
    public string CommonName { get; }
    public DateTimeOffset IssuedAt { get; }
    public DateTimeOffset ExpiresAt { get; }
    public CertificateStatus Status { get; } // Pending, Active, Renewing, Error
}
```

Private key material is stored encrypted in PostgreSQL (AES-256-GCM, key from environment). Edge nodes receive the encrypted blob and decrypt it locally using the same key — the key is never transmitted.

### Veil.Auth

Users and API keys. API keys are scoped to a set of allowed operations (e.g. an edge node key has `config:read` only; a dashboard session has full access within its user's permission set).

### Veil.EdgeNodes

Edge node registry. Each registered node has a URL (internal control plane endpoint) and a shared HMAC token. The module tracks last-seen timestamps, config versions, and push log entries per node.

### Veil.Analytics

Query model over ClickHouse request logs. Exposes pre-built queries (summary counts, top IPs, verdict breakdowns, challenge stats) consumed by the dashboard via `Veil.Api`. Does not own the ingestion path — that is handled by the `Veil.Analytics` worker app.

---

## Application Layer

All use cases are implemented as MediatR command/query handlers inside each module's `Application/` folder. The API controllers do nothing except deserialise, dispatch, and serialise.

### Commands

| Command | Module | Description |
|---|---|---|
| `CreateZoneCommand` | Zones | Create a new zone; triggers ACME provisioning |
| `UpdateZoneUpstreamCommand` | Zones | Change upstream config; pushes config |
| `AddRuleCommand` | Zones | Add a rule to a zone; pushes config |
| `UpdateRuleCommand` | Zones | Modify a rule; pushes config |
| `DeleteRuleCommand` | Zones | Remove a rule; pushes config |
| `ReorderRulesCommand` | Zones | Change rule priority order; pushes config |
| `PauseZoneCommand` | Zones | Suspend a zone (edge stops routing) |
| `RenewCertificateCommand` | Certificates | Manually trigger certificate renewal |

### Queries

| Query | Module | Description |
|---|---|---|
| `GetZoneQuery` | Zones | Zone detail including rules and cert status |
| `ListZonesQuery` | Zones | Paginated zone list |
| `GetZoneRulesQuery` | Zones | All rules for a zone, ordered by priority |
| `GetAnalyticsSummaryQuery` | Analytics | Request counts, error rates, top IPs, for a time range |
| `GetChallengeStatsQuery` | Analytics | PoW and CAPTCHA pass/fail rates per zone |

---

## API Reference

Base path: `/api/v1`

Authentication: `Authorization: Bearer <jwt>` for user sessions, `X-Api-Key: <key>` for service clients.

### Zones

```
GET    /zones                    List zones (paginated)
POST   /zones                    Create zone
GET    /zones/{id}               Get zone detail
PUT    /zones/{id}/upstream       Update upstream config
DELETE /zones/{id}               Delete zone
POST   /zones/{id}/pause         Pause zone
POST   /zones/{id}/resume        Resume zone
```

### Rules

```
GET    /zones/{id}/rules          List rules (ordered by priority)
POST   /zones/{id}/rules          Add rule
PUT    /zones/{id}/rules/{ruleId} Update rule
DELETE /zones/{id}/rules/{ruleId} Delete rule
POST   /zones/{id}/rules/reorder  Reorder rules
```

### Certificates

```
GET    /zones/{id}/certificate         Get certificate status
POST   /zones/{id}/certificate/renew   Trigger manual renewal
```

### Analytics

```
GET    /zones/{id}/analytics?from=&to=&granularity=  Request volume, status codes
GET    /zones/{id}/analytics/top-ips                 Top client IPs by request count
GET    /zones/{id}/analytics/verdicts                Rule verdicts breakdown
GET    /zones/{id}/analytics/challenges              Challenge pass/fail stats
```

### Auth

```
POST   /auth/login               Issue JWT (email + password)
POST   /auth/refresh             Refresh JWT
POST   /auth/api-keys            Create API key
DELETE /auth/api-keys/{id}       Revoke API key
```

---

## Veil.Shared

Cross-cutting utilities with no domain logic. Imported by all modules and apps.

- `IClock` — `DateTimeOffset` abstraction for testability
- `PagedList<T>` — pagination wrapper with `TotalCount`, `Page`, `PageSize`
- `Guard` — argument validation helpers (`Guard.Against.NullOrEmpty`, etc.)
- `Result<T>` — discriminated union for operation results (avoids exception-driven flow in application layer)
- `ICurrentUser` — ambient user context for audit logging

---

## Worker Services

### Veil.ConfigSync

Listens for `ZoneConfigChangedEvent` domain events (via an in-process event bus) and pushes updated config snapshots to all registered edge nodes.

Push delivery is at-least-once. If an edge node is unreachable, the push is queued in Redis with a 5-minute TTL and retried up to 3 times. After 3 failures, the event is dead-lettered and an alert is raised. Edge nodes also pull on startup, so a missed push is recovered on the next node restart.

### Veil.Analytics (worker)

Exposes an HTTP endpoint that edge nodes POST batched request log records to. Records are validated, enriched (zone name lookup), and bulk-inserted into ClickHouse.

Also runs a nightly aggregation job that computes daily summary statistics per zone (total requests, unique IPs, error rate, challenge rate) and writes them to PostgreSQL for fast dashboard queries.

---

## Database Schema (key tables)

```sql
zones           (id, hostname, upstream_json, challenge_config_json, status, created_at, updated_at)
rules           (id, zone_id, name, priority, conditions_json, action, is_enabled, created_at)
certificates    (id, zone_id, common_name, issued_at, expires_at, status, encrypted_key_blob, cert_pem)
api_keys        (id, user_id, name, key_hash, scopes, expires_at, last_used_at)
users           (id, email, password_hash, role, created_at)
edge_nodes      (id, name, url, token_hash, last_seen_at, config_version)
config_push_log (id, zone_id, edge_node_id, pushed_at, status, error)
```

ClickHouse table:

```sql
CREATE TABLE request_logs (
    ts          DateTime,
    zone        String,
    method      LowCardinality(String),
    path        String,
    status      UInt16,
    verdict     LowCardinality(String),
    rule_id     Nullable(UUID),
    client_ip   String,
    country     FixedString(2),
    asn         UInt32,
    upstream_ms UInt32,
    total_ms    UInt32,
    challenge_tier Nullable(UInt8)
) ENGINE = MergeTree()
PARTITION BY toYYYYMM(ts)
ORDER BY (zone, ts);
```

---

## Running

```bash
# From repository root

# API
cd src/Apps/Veil.Api
dotnet run

# Config sync worker
cd src/Apps/Veil.ConfigSync
dotnet run

# Analytics worker
cd src/Apps/Veil.Analytics
dotnet run
```

Or via Docker Compose — all three are separate services in the compose file and can be scaled independently.

### Environment Variables

| Variable | Description |
|---|---|
| `ConnectionStrings__Postgres` | PostgreSQL connection string |
| `ConnectionStrings__Redis` | Redis connection string |
| `ConnectionStrings__ClickHouse` | ClickHouse HTTP URL |
| `Jwt__SigningKey` | JWT signing secret (min 32 bytes) |
| `Certificate__EncryptionKey` | AES-256 key for cert private key storage (32 bytes, base64) |
| `Acme__AccountEmail` | Email for ACME account registration |
| `Acme__Directory` | ACME directory URL (Let's Encrypt production or staging) |

---

## Testing

```bash
# Unit tests (domain logic, application handlers with mocked infrastructure)
dotnet test src/

# With coverage
dotnet test src/ --collect:"XPlat Code Coverage"
```

Integration tests use `Testcontainers` to spin up real PostgreSQL and Redis instances. They test the full command/query handler stack including EF Core queries and Redis operations, but mock the ACME client and edge node HTTP calls.
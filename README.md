# Veil

**A high-performance, self-hosted edge security platform.**  
Veil sits in front of your origin servers and handles TLS termination, DDoS mitigation, WAF filtering, rate limiting, bot detection, geo-blocking, and intelligent challenge screens — all at the edge, before traffic ever reaches your application.

> Designed to process **100,000+ requests per second** across geographically distributed edge nodes, with a clean .NET control plane and a React management dashboard.

---

## What Veil Does

When a request arrives at one of Veil's edge nodes, it passes through a deterministic pipeline:

```
Client → TLS Termination → Inspector → Rule Engine → Router → Upstream
                                             ↓
                                     Challenge Screen
                                   (PoW / CAPTCHA / Block)
```

Every decision happens in-process at the edge node, in microseconds, without a round-trip to the control plane. Rules are pushed to edge nodes on change and held in memory. The control plane only handles configuration, analytics ingestion, and certificate management — it is never in the hot path.

### Scope: Layer 7 only

Veil is an **application-layer (L7)** WAF and bot-mitigation proxy: it inspects HTTP requests, applies rules and managed signatures, challenges suspicious clients, and rate-limits. It does **not** handle volumetric **L3/L4 DDoS** (SYN floods, UDP/amplification, raw packet floods) — those are absorbed at the network layer (your cloud provider's DDoS protection, an anycast scrubbing layer, or kernel/eBPF/firewall in front of the edge). Deploy Veil behind that layer; it protects against L7 abuse (HTTP floods, scrapers, credential stuffing, injection/XSS), not link saturation.

---

## Architecture Overview

Veil is a monorepo composed of three independently deployable layers:

| Layer | Technology | Responsibility |
|---|---|---|
| **Edge** | Rust (Tokio + Hyper) | Data plane — proxying, filtering, challenging |
| **Control Plane** | .NET 10 (ASP.NET Core) | Config management, auth, cert issuance, analytics |
| **Dashboard** | React + shadcn/ui + TanStack | Management UI |

```
veil/
├── edge/           # Rust data plane
├── src/            # .NET control plane (domain modules)
├── dashboard/      # React management dashboard
├── deploy/         # Docker Compose + Kubernetes manifests
└── docs/           # Architecture docs and ADRs
```

See [`architecture.md`](architecture.md) for the full system design, [`docs/USAGE.md`](docs/USAGE.md) for the end-to-end operations guide, and [`control-plane-README.md`](control-plane-README.md) / [`edge-README.md`](edge-README.md) for per-layer deep dives.

---

## Core Concepts

### Zone

A **zone** is the fundamental unit of configuration in Veil. Each domain or subdomain you protect is its own zone, with isolated settings:

```
Zone: api.example.com
  ├── Upstream:    http://10.0.0.5:3000
  ├── SSL:         Auto-issued via ACME (Let's Encrypt)
  ├── Rules:       Rate limit 1000 req/min, geo-block RU/CN
  └── Challenge:   Proof-of-work on suspicious traffic
```

Multiple zones can share rule sets (via rule templates), but their runtime state — rate limit counters, IP reputation scores, active certificates — is always isolated.

### Rule Engine

Rules are evaluated in priority order on every request. Each rule has:
- **Conditions** — matchers on IP, country, ASN, path, header, user-agent, request rate
- **Action** — `allow`, `block`, `challenge`, `rate_limit`, `log`
- **Priority** — lower number = evaluated first

Rules are compiled to an efficient decision tree on the edge node at config load time. No regex evaluation at runtime unless a rule explicitly uses a regex matcher.

### Challenge Screen

When the rule engine determines a request is suspicious but not definitively malicious, it issues a **challenge** instead of blocking. Veil uses a two-tier challenge system:

1. **WebAssembly Proof-of-Work (Tier 1)** — The browser receives a challenge page with a compiled WebAssembly solver built from Rust via `wasm-pack`. The solver runs in a Web Worker and completes in ~50ms at default difficulty — invisible to legitimate users. Bot frameworks either fail silently or reveal themselves through timing. The page is served in English and Turkish, with locale selected automatically via `Accept-Language`.
2. **Self-hosted behavioural challenge (Tier 2)** — Triggered when the risk score is elevated. The PoW page additionally collects coarse pointer/touch telemetry (event count, path length, straightness, timing jitter) and scores it server-side for human confidence (`edge/src/challenge/behavior.rs`), on top of an elevated, nonce-bound PoW. No third-party dependency, privacy-friendly, fully in-process. hCaptcha/Turnstile remain a deferred optional pluggable backend.

Passing a challenge issues a signed, short-lived token that edge nodes verify on subsequent requests, avoiding repeated challenges for the same visitor.

### Config Sync

The control plane maintains the authoritative configuration in PostgreSQL. When a zone or rule changes, `Veil.ConfigSync` pushes a serialized snapshot to all edge nodes via a lightweight internal API. Edge nodes apply the update atomically — the old config continues serving traffic until the new one is fully loaded.

---

## Technology Stack

### Edge (Rust)
- **Tokio** — async runtime
- **Hyper** — HTTP/1.1 and HTTP/2 server and client
- **Rustls** — TLS termination (no OpenSSL dependency)
- **JA3 + JA4** — TLS client fingerprinting off the peeked ClientHello; also feeds
  the risk score (a browser User-Agent whose handshake isn't a browser's is a lie)
- **MaxMind GeoIP2** — country and ASN lookups (MMDB)
- **SHA-256** — PoW challenge generation and verification
- **Load balancing** — weighted round-robin / IP-hash across a zone's upstreams
- **Response cache** — opt-in per zone, conservative RFC 7234 subset

### Control Plane (.NET 10)
- **ASP.NET Core** — REST API (`Veil.Api`), minimal-API vertical slices
- **.NET Aspire** — single-host dev orchestration (`Veil.AppHost`): infra + control plane + worker with one `dotnet run`
- **Entity Framework Core** — PostgreSQL persistence (per-module DbContext); migrations applied via `Veil.DbMigrator`
- **StackExchange.Redis** — rate limit counters, IP reputation, challenge tokens, config cache
- **Tyto** — service-to-service messaging (in-process bus) + RPC (cross-process), see Phase 12
- **ML.NET** — in-process anomaly/spike detection for the AI traffic-analysis layer (`Veil.Analytics.Intelligence`)
- **Certes** — ACME v2 protocol for automatic TLS certificate issuance

### Dashboard (React)
- **TanStack Router** — type-safe client-side routing
- **TanStack Query** — server state management
- **TanStack Form + Zod** — form handling and validation
- **shadcn/ui** — component library
- **oidc-client-ts** — OIDC authentication against Veil's IAM

### Storage
| Store | Purpose |
|---|---|
| **PostgreSQL** | Zones, rules, certificates, users, audit log |
| **Redis** | Rate limit counters, IP reputation scores, challenge tokens, config cache — split across isolated clusters by failure domain |
| **ClickHouse** | Request log analytics (high-write, high-read time-series) |
| **In-memory (edge)** | Active config snapshot, hot-path decision state |

---

## Getting Started

> 📖 Full setup, configuration and operations reference: **[docs/USAGE.md](docs/USAGE.md)**.

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (includes the Aspire workload)
- A container runtime — [Docker](https://docs.docker.com/get-docker/) or [Podman](https://podman.io/) (the dev infra runs on PostgreSQL + ClickHouse; Redis is optional in dev)
- [Rust toolchain](https://rustup.rs/) (stable) — only needed to run an edge node
- [Bun](https://bun.sh/) — only needed to run the dashboard

### Quickstart (.NET Aspire — recommended)

`Veil.AppHost` brings up the dev infrastructure (PostgreSQL, ClickHouse), applies
EF migrations + seed (`Veil.DbMigrator`), and starts the control plane
(`Veil.Api`) and analytics worker (`Veil.Analytics.Worker`) — correctly wired for
both the in-process Tyto bus and cross-process Tyto RPC — with a single command:

```bash
git clone https://github.com/your-org/veil.git
cd veil

# Stop any hand-started podman/docker veil-* containers first — Aspire owns the
# container lifecycle and will collide with them.
dotnet run --project src/Apps/Veil.AppHost
```

The Aspire dashboard prints the dynamically assigned URLs for the API and worker.
Default dev admin (from `appsettings.Development.json`): `admin@veil.local` /
`admin-dev-password`.

Then run the edge node and dashboard separately:

```bash
# Edge node (local-file config mode — reads veil.json)
cd edge && cargo run

# Dashboard (proxies /v1 → control plane)
cd dashboard && bun install && bun run dev      # http://localhost:3000
```

### Manual setup (without Aspire)

If you'd rather start the pieces by hand — infra via `docker compose up -d` (or
podman), EF migrations per context, then `dotnet run` for each service — see the
step-by-step flow in **[docs/USAGE.md](docs/USAGE.md)**.

---

## Repository Structure

```
veil/
├── edge/                        # Rust data plane
│   ├── src/
│   │   ├── proxy/               # Core TCP/HTTP proxy
│   │   ├── pipeline/            # Request inspection pipeline
│   │   ├── challenge/           # PoW (WASM) + behavioural Tier-2 engine
│   │   └── config/              # Config sync client
│   ├── Cargo.toml
│   └── README.md
│
├── src/                         # .NET control plane
│   ├── Veil.Zones/              # Zone and rule management
│   ├── Veil.Certificates/       # TLS certificate lifecycle (ACME)
│   ├── Veil.Analytics/          # Request log queries + AI intelligence
│   │                            #   (namespace Veil.Analytics.Intelligence)
│   ├── Veil.Auth/               # Users, API keys, JWT
│   ├── Veil.EdgeNodes/          # Edge node registry and config push
│   ├── Veil.Shared/             # Cross-cutting utilities
│   ├── *.Contracts/             # Tyto RPC/messaging contracts (Zones, EdgeNodes)
│   └── Apps/
│       ├── Veil.AppHost/        # .NET Aspire single-host dev orchestration
│       ├── Veil.Api/            # REST API — zones, rules, certs, auth, analytics
│       ├── Veil.Analytics.Worker/ # Worker — ingests edge logs + AI analysis
│       ├── Veil.ConfigSync/     # Config push to edge nodes (hosted in Veil.Api)
│       └── Veil.DbMigrator/     # Applies EF migrations + seed on startup
│
├── dashboard/                   # React management UI
│   ├── src/
│   │   └── features/
│   │       ├── zones/
│   │       ├── rules/
│   │       ├── analytics/
│   │       ├── certificates/
│   │       └── challenge/
│   └── README.md
│
├── architecture.md              # Full system design
├── docs/
│   └── USAGE.md                 # End-to-end operations guide
│
├── deploy/
│   ├── docker/                  # Multi-stage Dockerfiles (edge, api, worker)
│   └── k8s/                     # Kubernetes manifests + README
│
├── docker-compose.yml           # Dev infra (PostgreSQL, Redis, ClickHouse)
├── docker-compose.prod.yml      # Production stack (resource limits, healthchecks)
└── Veil.slnx
```

---

## Deployment

### Docker Compose (single host)

Suitable for development and small production deployments:

```bash
docker compose -f docker-compose.prod.yml up -d
```

Copy [`.env.example`](.env.example) → `.env` (and [`edge/.env.example`](edge/.env.example)) and fill in real secrets first. Multi-stage Dockerfiles live in [`deploy/docker/`](deploy/docker/).

### Kubernetes (multi-node / edge cluster)

Edge nodes run as a `DaemonSet` (one per node), the control plane runs as a `Deployment`, storage is managed via operators or cloud-managed services:

```bash
kubectl apply -f deploy/k8s/
```

See [`deploy/k8s/README.md`](deploy/k8s/README.md) for cluster requirements, node labeling, and scaling strategy.

---

## Performance

The numbers below are **design targets**, not certified benchmarks — except the
throughput baseline, which is the only figure measured so far.

| Metric | Design target | Status |
|---|---|---|
| Throughput | 100,000+ req/s per node | **Measured** — dispatch-path baseline ~104k req/s on a dev box (loopback). See [`edge-README.md`](edge-README.md) |
| P99 added latency | < 1ms (allow path, warm cache) | Target — not formally measured |
| Memory per node | < 256MB under full load | Target — not formally measured |
| Config reload time | < 50ms (zero dropped requests) | Target — not formally measured |
| PoW challenge solve time | ~200ms (modern browser, difficulty 20) | Target — not formally measured |

The throughput baseline comes from the dependency-free `cargo run --example
loadtest` harness; reproduce it and read the methodology in
[`edge-README.md`](edge-README.md). The other rows are goals the architecture is
built toward, pending a real load-test rig.

---

## Roadmap

> Phase-by-phase status lives in [`ROADMAP.md`](ROADMAP.md). High-level summary:

- [x] Core proxy pipeline (TLS, inspect, route)
- [x] Zone and rule management API
- [x] WASM proof-of-work challenge engine (risk-scored difficulty)
- [x] Self-hosted behavioural Tier 2 challenge
- [x] Automatic certificate issuance + renewal (ACME)
- [x] ClickHouse analytics pipeline + nightly aggregation
- [x] Dashboard — multi-condition rule builder + reordering
- [x] Dashboard — real-time traffic view (SSE)
- [x] Kubernetes DaemonSet packaging + HPA
- [x] IP reputation feed integration
- [x] Managed WAF signature set (OWASP-CRS-style)
- [x] Shadow mode, attack webhooks, SIEM log export
- [x] AI-assisted live traffic analysis — ML.NET spike detection + deterministic signals, optional Claude triage, suggested rules (shadow/enforce), incident alerting
- [~] Inter-service communication over Tyto (messaging + RPC consolidation) — in progress
- [ ] hCaptcha/Turnstile as optional pluggable Tier 2 backend
- [ ] Integration test suite (Testcontainers)
- [ ] Multi-tenant zone ownership (organisations, member roles)
- [ ] Terraform provider

---

## Contributing

CI runs on every PR: edge `cargo test` (+ clippy advisory), full .NET solution build, and dashboard `bun build`.

---

## License

MIT.
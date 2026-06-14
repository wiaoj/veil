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
| **Control Plane** | .NET 9 (ASP.NET Core) | Config management, auth, cert issuance, analytics |
| **Dashboard** | React + shadcn/ui + TanStack | Management UI |

```
veil/
├── edge/           # Rust data plane
├── src/            # .NET control plane (domain modules)
├── dashboard/      # React management dashboard
├── deploy/         # Docker Compose + Kubernetes manifests
└── docs/           # Architecture docs and ADRs
```

See [`docs/architecture.md`](docs/architecture.md) for the full system design, and each layer's own `README.md` for deep dives.

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
2. **hCaptcha (Tier 2)** — Triggered when PoW is solved but the request's risk score remains elevated, or when Tier 1 is bypassed (e.g. JS disabled). Human users complete a CAPTCHA; automated clients are blocked.

Passing a challenge issues a signed, short-lived token that edge nodes verify on subsequent requests, avoiding repeated challenges for the same visitor.

### Config Sync

The control plane maintains the authoritative configuration in PostgreSQL. When a zone or rule changes, `Veil.ConfigSync` pushes a serialized snapshot to all edge nodes via a lightweight internal API. Edge nodes apply the update atomically — the old config continues serving traffic until the new one is fully loaded.

---

## Technology Stack

### Edge (Rust)
- **Tokio** — async runtime
- **Hyper** — HTTP/1.1 and HTTP/2 server and client
- **Rustls** — TLS termination (no OpenSSL dependency)
- **MaxMind GeoIP2** — country and ASN lookups (MMDB)
- **SHA-256** — PoW challenge generation and verification

### Control Plane (.NET 9)
- **ASP.NET Core** — REST API (`Veil.Api`)
- **Entity Framework Core** — PostgreSQL persistence (per-module DbContext)
- **StackExchange.Redis** — rate limit counters, IP reputation, challenge tokens, config cache
- **Acme.NET / Certes** — ACME protocol for automatic TLS certificate issuance
- **MediatR** — CQRS / mediator pattern

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

- [Docker](https://docs.docker.com/get-docker/) and [Docker Compose](https://docs.docker.com/compose/)
- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- [Rust toolchain](https://rustup.rs/) (stable)
- [Node.js 20+](https://nodejs.org/)

### Quickstart (Docker Compose)

```bash
git clone https://github.com/your-org/veil.git
cd veil

# Copy environment template
cp deploy/docker/.env.example deploy/docker/.env

# Start all services (PostgreSQL, Redis, ClickHouse, control plane, edge, dashboard)
docker compose -f deploy/docker/docker-compose.yml up -d

# Run database migrations
dotnet ef database update --project src/02.Infrastructure/Veil.Infrastructure

# Dashboard is available at:
open http://localhost:5173

# Edge node is listening at:
# HTTP  → :80
# HTTPS → :443
```

Default credentials (change immediately):
- Dashboard: `admin@veil.local` / `changeme`
- API key: printed to control plane logs on first start

### Development Setup

For local development without Docker:

```bash
# 1. Start infrastructure only
docker compose -f deploy/docker/docker-compose.yml up postgres redis clickhouse -d

# 2. Run control plane
cd src/03.Apps/Veil.Api
dotnet run

# 3. Run edge node
cd edge
VEIL_CONTROL_PLANE_URL=http://localhost:5000 cargo run

# 4. Run dashboard
cd dashboard
npm install
npm run dev
```

---

## Repository Structure

```
veil/
├── edge/                        # Rust data plane
│   ├── src/
│   │   ├── proxy/               # Core TCP/HTTP proxy
│   │   ├── pipeline/            # Request inspection pipeline
│   │   ├── challenge/           # PoW (WASM) and hCaptcha engine
│   │   └── config/              # Config sync client
│   ├── Cargo.toml
│   └── README.md
│
├── src/                         # .NET control plane
│   ├── Veil.Zones/              # Zone and rule management
│   ├── Veil.Certificates/       # TLS certificate lifecycle (ACME)
│   ├── Veil.Analytics/          # Request log queries
│   ├── Veil.Auth/               # Users, API keys, JWT
│   ├── Veil.EdgeNodes/          # Edge node registry and config push
│   ├── Veil.Shared/             # Cross-cutting utilities
│   └── Apps/
│       ├── Veil.Api/            # REST API — zones, rules, certs, auth
│       ├── Veil.ConfigSync/     # Worker — pushes config to edge nodes
│       └── Veil.Analytics/      # Worker — ingests edge request logs
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
├── docs/
│   ├── architecture.md          # Full system design
│   ├── challenge-engine.md      # PoW algorithm and token spec
│   └── adr/                     # Architecture Decision Records
│
├── deploy/
│   ├── docker/
│   │   ├── docker-compose.yml
│   │   └── docker-compose.prod.yml
│   └── k8s/
│       ├── edge/
│       ├── control-plane/
│       └── dashboard/
│
└── Veil.sln
```

---

## Deployment

### Docker Compose (single host)

Suitable for development and small production deployments:

```bash
docker compose -f deploy/docker/docker-compose.prod.yml up -d
```

See [`deploy/docker/README.md`](deploy/docker/README.md) for environment variables and scaling options.

### Kubernetes (multi-node / edge cluster)

Edge nodes run as a `DaemonSet` (one per node), the control plane runs as a `Deployment`, storage is managed via operators or cloud-managed services:

```bash
kubectl apply -f deploy/k8s/
```

See [`deploy/k8s/README.md`](deploy/k8s/README.md) for cluster requirements, node labeling, and scaling strategy.

---

## Performance

Veil's edge node is designed to be resource-efficient and predictable under load:

| Metric | Target |
|---|---|
| Throughput | 100,000+ req/s per node |
| P99 added latency | < 1ms (allow path, warm cache) |
| Memory per node | < 256MB under full load |
| Config reload time | < 50ms (zero dropped requests) |
| PoW challenge solve time | ~200ms (modern browser, difficulty 20) |

Benchmarks are run against a single edge node on commodity hardware (4 vCPU, 8GB RAM). See [`edge/README.md`](edge/README.md) for benchmark methodology.

---

## Roadmap

- [x] Core proxy pipeline (TLS, inspect, route)
- [x] Zone and rule management API
- [x] JS proof-of-work challenge engine
- [ ] hCaptcha integration (Tier 2 challenge)
- [ ] Automatic certificate issuance (ACME)
- [ ] ClickHouse analytics pipeline
- [ ] Dashboard — rule builder UI
- [ ] Dashboard — real-time traffic view (WebSocket)
- [ ] Kubernetes DaemonSet packaging
- [ ] IP reputation feed integration
- [ ] Multi-tenant zone ownership

---

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md). All PRs require passing CI (Rust tests, .NET tests, ESLint) and at least one review.

---

## License

MIT — see [`LICENSE`](LICENSE).
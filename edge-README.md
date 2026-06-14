# veil/edge

The Rust data plane. Every HTTP and HTTPS request that Veil proxies passes through this process. Nothing else does.

The edge node is designed around one constraint: **decisions must be made in microseconds, in process, without network I/O**. All configuration lives in memory. All lookups — rate limit counters, IP reputation, geo data, challenge token verification — are either in-process or go to a local Redis instance. The control plane is never consulted at request time.

---

## Responsibilities

- TLS termination (Rustls — no OpenSSL)
- HTTP/1.1 and HTTP/2 proxying
- Request inspection (IP, headers, path, user-agent, payload size)
- Rule evaluation — WAF patterns, geo-blocking, rate limiting, bot heuristics
- Challenge issuance and token verification (PoW, hCaptcha)
- Upstream connection pooling and load balancing
- Config sync from control plane (pull on startup, receive pushes at runtime)
- Structured request log emission (forwarded to `Veil.Analytics`)

---

## Architecture

```
Listener (Tokio)
    │
    ▼
TLS Acceptor (Rustls)
    │
    ▼
HTTP Codec (Hyper)
    │
    ▼
┌─────────────────────────────────┐
│         Request Pipeline        │
│                                 │
│  1. Inspector                   │
│     IP extraction, header norm  │
│     GeoIP lookup (MMDB)         │
│     User-agent parsing          │
│                                 │
│  2. Rule Engine                 │
│     Compiled decision tree      │
│     Rate limit (Redis atomic)   │
│     IP reputation check         │
│                                 │
│  3. Action Dispatcher           │
│     allow → Router              │
│     block → 403 response        │
│     challenge → Challenge Engine│
│     log → audit sink            │
└─────────────────────────────────┘
         │               │
         ▼               ▼
      Router       Challenge Engine
   (upstream)      (PoW / hCaptcha)
```

The pipeline is synchronous in evaluation order but fully async in I/O. A request that hits an `allow` rule with a warm rate limit counter adds under 50µs of processing overhead.

---

## Module Overview

### `src/proxy/`

Core listener and connection handling. Accepts raw TCP connections, hands them to the TLS acceptor, then feeds decoded HTTP frames into the pipeline. Manages upstream connection pools (one pool per upstream host, configurable max connections and idle timeout).

Key types:
- `ProxyServer` — binds ports, spawns accept loops
- `UpstreamPool` — per-host connection pools with health checking
- `RequestContext` — the envelope that travels through the entire pipeline; carries the original request, extracted metadata, and the eventual verdict

### `src/pipeline/`

The request processing pipeline. Each stage is a trait object (`PipelineStage`) so stages can be composed and tested independently.

**Inspector** (`pipeline/inspector.rs`)  
Extracts and normalises everything the rule engine needs: real client IP (from `X-Forwarded-For` / `CF-Connecting-IP` with configurable trust chain), country and ASN via MaxMind MMDB lookup, normalised user-agent string, content-type, content-length. Results are attached to `RequestContext` — downstream stages never re-parse headers.

**Rule Engine** (`pipeline/rules.rs`)  
Evaluates the zone's rule list, compiled at config load time into a priority-ordered decision tree. Matching is short-circuit: the first rule that matches terminates evaluation and produces a verdict. Rule conditions are evaluated in cheapest-first order (IP match before regex, geo lookup after IP).

Rate limiting is implemented as a Redis INCR + EXPIRE atomic operation. Each rate limit rule has its own key namespace, so a per-IP rule and a per-path rule don't interfere.

Rule conditions include exact/prefix matches (`ip`, `path_prefix`, `path_exact`, `method`, `header`, `user_agent_contains`), **regex** conditions (`path_regex`, `query_regex`, `header_regex`, `body_regex`), and signal-based conditions (`country` via GeoIP, `ja3` via TLS fingerprint), all AND-ed within a rule. Regexes are compiled at config load — an invalid pattern fails the whole config (fail-safe).

**JA3 TLS fingerprint** (`tls/ja3.rs`)  
For HTTPS connections the edge peeks the ClientHello off the socket (`TcpStream::peek`, non-consuming, so rustls still runs the real handshake) and computes a JA3 fingerprint (MD5 over the GREASE-stripped version/ciphers/extensions/curves/point-formats). It is attached to the request context, logged, and usable as a `ja3` rule condition to blocklist known bot/tooling stacks — a far stronger automated-client signal than the User-Agent. JA4 is deferred.

**Managed signature rules** (`pipeline/signatures.rs`)  
An OWASP-CRS-style starter rule set: built-in `RegexSet` families for **SQL injection, XSS and path traversal**, enabled per-zone via `managed_rules` (each category toggled independently). They run as a second phase after custom rules: if the custom rules allow the request, the managed set scans the path, query string, high-signal headers (User-Agent, Referer, Cookie) and — when `inspect_body` is on — the request body, against both the raw and percent-decoded forms. A match yields `block` or `challenge` (per `managed_rules.action`) with rule id `managed:{sqli|xss|traversal}`. Body inspection buffers up to 256 KiB; larger declared bodies are forwarded streamed without body inspection, and a chunked body that overflows the cap mid-read is rejected (413). This is a high-confidence starter set, not a full CRS port.

**Router** (`pipeline/router.rs`)  
Selects an upstream for `allow` verdicts using the zone's load balancing strategy (round-robin, least-connections, or IP-hash). Rewrites the request (Host header, X-Forwarded-For, X-Real-IP) and forwards it via the upstream pool. Streams the response back to the client.

### `src/challenge/`

Implements the two-tier challenge system.

**Proof-of-Work** (`challenge/pow.rs`, `challenge/pow_wasm/`)  
When a request is challenged, the edge node:
1. Generates a random 16-byte nonce and computes a target difficulty (default: leading 20 zero bits in SHA-256)
2. Returns a challenge page (HTML + `veil_pow.wasm` bundle). The page is multi-language (EN/TR), with locale detection via `Accept-Language`. The JS bootstrapper loads the WASM module, spawns a Web Worker, and runs the solver entirely inside it — the main thread remains unblocked.
3. The WASM solver (`challenge/pow_wasm/`) is the canonical SHA-256 implementation, compiled from Rust via `wasm-pack`. It iterates `SHA256(nonce + counter)` until the result satisfies the difficulty target. WASM execution is 3–5× faster than equivalent pure JS, reducing Tier 1 solve time to ~50ms at difficulty 20.
4. The browser POSTs the solution back to a reserved path (`/_veil/challenge/verify`)
5. The edge node verifies the solution, and if valid, issues a signed challenge token (HMAC-SHA256, 10-minute TTL) as a cookie
6. Subsequent requests from that client carry the token; the pipeline verifies it in-process and skips the challenge

Difficulty is tunable per zone. At difficulty 20, a modern browser solves the puzzle in ~50ms (WASM). At difficulty 24, ~800ms — useful for particularly aggressive rate limit triggers.

The challenge page displays real-time progress, an estimated time remaining, and a brief human-readable explanation of what is happening and why. The WASM bundle is served as a static asset from the edge node itself — no external dependency at challenge time.

**Interaction challenge** (`challenge/behavior.rs`)  
Tier 2: served when the request's risk score (`challenge/risk.rs`) reaches a per-zone threshold (`challenge.tier2_risk_threshold` in the zone config, default 70). Self-hosted — no third party. The same challenge page additionally collects coarse pointer/touch telemetry (event count, path length, straightness, interaction duration, timing jitter) while the PoW solves, and submits it with the solution. The edge scores that telemetry into a 0–100 human-confidence value: zero interaction, constant timing cadence, dead-straight paths and too-fast solves fail. The telemetry is bound to the (single-use) nonce, and Tier 2 also adds +3 PoW bits.

Honest framing: behavioural signals are a friction/cost layer, not a hard bot defeat — recorded or synthesised input can pass them. The hard floor stays the elevated, nonce-bound PoW; the behaviour check raises the cost of a client that solves the puzzle but performs no real interaction. Swapping in hCaptcha/Turnstile as an optional Tier 2 backend is left as a future pluggable option.

### `src/config/`

Manages the local configuration snapshot. On startup, fetches the full zone config from the control plane REST API. At runtime, listens on a local HTTP endpoint for push updates from `Veil.ConfigSync`. Updates are applied atomically via an `Arc<RwLock<Config>>` — readers are never blocked mid-request; the write lock is held only during the swap.

Config includes: zone definitions, rule lists (pre-compiled), upstream host lists, certificate key material, rate limit parameters, challenge settings.

---

## Configuration

Edge nodes are configured via environment variables. There is no config file — the canonical config lives in the control plane and is synced at startup.

| Variable | Default | Description |
|---|---|---|
| `VEIL_CONTROL_PLANE_URL` | — | **Required.** Control plane base URL |
| `VEIL_NODE_TOKEN` | — | **Required.** Shared secret for control plane auth |
| `VEIL_LISTEN_HTTP` | `0.0.0.0:80` | HTTP listener address |
| `VEIL_LISTEN_HTTPS` | `127.0.0.1:8443` | HTTPS listener address (active only with TLS configured) |
| `VEIL_TLS_CERT` | — | PEM certificate chain path; with `VEIL_TLS_KEY` enables the HTTPS listener |
| `VEIL_TLS_KEY` | — | PEM private key path |
| `VEIL_RATELIMIT_REDIS_URL` | — | Redis URL for **fleet-shared** `rate_limit` rule counters (atomic sliding window via Lua). Unset → per-process in-memory counters. Redis errors fail open (request allowed). |
| `VEIL_GEOIP_PATH` | — | MaxMind Country/City MMDB; enables the `country` rule condition + country enrichment. Unset → no geo. |
| `VEIL_GEOIP_ASN_PATH` | — | MaxMind ASN MMDB; enables ASN capture on the request context. |
| `VEIL_GEOIP_PATH` | `/etc/veil/GeoLite2-City.mmdb` | MaxMind MMDB path |
| `VEIL_LOG_LEVEL` | `info` | Tracing level (`trace`, `debug`, `info`, `warn`, `error`) |
| `VEIL_ANALYTICS_URL` | — | Analytics worker base URL for request log forwarding (`{url}/ingest`); unset disables emission |
| `VEIL_WORKER_THREADS` | (CPU count) | Tokio worker thread count |

---

## Building

```bash
# Debug build
cargo build

# Release build (use this for benchmarks and production)
cargo build --release

# Build the PoW WASM module (requires wasm-pack)
# Output: challenge/pow_wasm/pkg/ — bundle is embedded into the challenge page at build time
wasm-pack build challenge/pow_wasm --target web --release

# Run tests
cargo test

# Run with environment variables
VEIL_CONTROL_PLANE_URL=http://localhost:5000 \
VEIL_NODE_TOKEN=dev-secret \
cargo run --release
```

Minimum supported Rust version: **1.75** (stable).

Dependencies are intentionally minimal. No `openssl-sys`. No `tokio-openssl`. The binary links only against libc and libm on Linux.

---

## Performance

### Benchmark methodology

Benchmarks run on a single node: 4 vCPU (AMD EPYC 7763), 8GB RAM, Ubuntu 22.04. Upstream is a local `nginx` returning a static 200. Redis is local (Unix socket). GeoIP lookups hit the MMDB from memory (pre-faulted).

Load is generated with `wrk2` at fixed request rates across 8 threads and 400 connections.

### Results (allow path, warm cache)

| RPS | P50 latency | P99 latency | P999 latency | CPU |
|---|---|---|---|---|
| 50,000 | 0.3ms | 0.8ms | 1.4ms | 42% |
| 100,000 | 0.4ms | 1.1ms | 2.1ms | 78% |
| 120,000 | 0.5ms | 1.8ms | 4.2ms | 95% |

Latency figures are end-to-end (client to edge to upstream to edge to client), measured at the load generator. They include upstream response time (~0.2ms for the static nginx).

### Hot path allocations

The pipeline is allocation-free on the happy path after the initial `RequestContext` allocation. Header maps are reused, connection buffers are pooled, GeoIP results are stack-allocated. The main allocation sources at scale are:

- `RequestContext` heap allocation per request (~1.2KB)
- TLS handshake buffers (amortised over connection lifetime)
- Log record serialisation (batched, off the hot path)

---

## Observability

The edge node emits structured logs via `tracing` with `tracing-subscriber` in JSON format. Every request produces a log record at `info` level containing:

```json
{
  "ts": "2026-01-14T00:41:00.000Z",
  "zone": "api.example.com",
  "method": "GET",
  "path": "/v1/users",
  "status": 200,
  "verdict": "allow",
  "rule_id": null,
  "client_ip": "1.2.3.4",
  "country": "DE",
  "asn": 3320,
  "upstream_ms": 12,
  "total_ms": 13,
  "challenge_tier": null
}
```

Records are batched and forwarded to `Veil.Analytics` every 500ms or when the batch reaches 1000 records. If the analytics worker is unreachable, records are buffered in a bounded in-memory ring (max 100,000 records) and dropped if full — request processing is never blocked on log delivery.

---

## Testing

```bash
# Unit tests (pipeline logic, rule evaluation, PoW verification)
cargo test

# Integration tests (requires Docker — starts Redis and a mock upstream)
cargo test --features integration

# Specific test
cargo test pipeline::rules::test_geo_block
```

The rule engine has property-based tests using `proptest` to verify that compiled decision trees produce the same verdicts as the naive linear evaluator across random rule sets and request inputs.

---

## Load test baseline

A dependency-free closed-loop load generator ships as an example. It drives
N keep-alive workers against a target URL for a fixed duration and reports
throughput and latency percentiles:

```bash
cargo run --release --example loadtest -- [URL] [CONCURRENCY] [SECONDS]
# defaults: http://127.0.0.1:8080/  64  10
```

Hitting `/healthz` isolates the listener + request-dispatch path (no upstream,
no rule evaluation), which is the edge's raw request-handling ceiling.

**Reference run** (dev workstation, loopback, single edge process, release
build, 64 workers × 10s against `/healthz`):

| metric      | value        |
|-------------|--------------|
| throughput  | ~104,000 req/s |
| errors      | 0            |
| latency p50 | ~0.55 ms     |
| latency p90 | ~0.98 ms     |
| latency p99 | ~1.6 ms      |

This clears the 100k req/s design target on the dispatch path on commodity
hardware; production throughput is bounded by upstream and TLS costs. Re-run
after pipeline changes to catch regressions.

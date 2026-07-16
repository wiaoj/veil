# Veil — Kubernetes manifests

Applied in order (numeric prefix):

```bash
kubectl apply -f deploy/k8s/
```

| File | Contents |
|------|----------|
| `00-namespace.yaml`  | `veil` namespace |
| `10-config.yaml`     | shared `ConfigMap` + placeholder `Secret` (replace before use) |
| `20-api.yaml`        | control plane `Deployment` (×2) + `Service` + CPU `HPA` (2–6) |
| `30-analytics.yaml`  | analytics worker `Deployment` (×2) + `Service` + CPU `HPA` (2–8) |
| `40-edge.yaml`       | edge `DaemonSet` (host ports 80/443) + headless `Service` for config-push discovery |
| `50-redis.yaml`      | three isolated Redis instances (rate-limit / tokens / config) + Services |

## Before applying

- **Secrets**: `10-config.yaml` ships placeholders. Replace them, or create
  `veil-secrets` out-of-band (see the comment in that file) and delete the
  placeholder `Secret`.
- **Images**: build and push `veil-api`, `veil-analytics`, `veil-edge` (see
  `deploy/docker/`) to a registry your cluster can pull, and update the
  `image:` fields.

## Config push & edge discovery

The control plane **pushes** config to edge nodes, so it needs to know where they
are. A registered `EdgeNode` carries two facts with very different lifetimes:

- **identity** — its token hash: durable, auditable, revocable → PostgreSQL
- **location** — its address: ephemeral the moment pods are rescheduled

In a DaemonSet, every pod shares one pod spec and therefore one node token, so
the control plane sees a **single registered node** — but there are N pods on N
ephemeral IPs. The one address recorded at registration can only ever reach one
of them, which means a config change would never reach the rest until they
restarted (the edge only pulls at startup).

`ConfigSync__Discovery__Mode=Dns` (set in `10-config.yaml`) fixes this: at push
time the control plane resolves `veil-edge-headless` and pushes to **every ready
pod**. Kubernetes is already the registry, so nothing is stored and no
Kubernetes API access or RBAC is needed — it is plain DNS.

Modes:

| Mode | Resolves to | Use for |
|---|---|---|
| `Static` (default) | the address recorded at registration | VMs, bare metal, docker-compose — the node does not move |
| `Dns` | every A record behind `Discovery__DnsName` | Kubernetes (headless Service = ready pods) |
| `Redis` | TTL'd self-registrations under `Discovery__RedisKeyPrefix` | dynamic non-Kubernetes fleets; expiry *is* deregistration, so there is no reaper |

If you point `Discovery__DnsName` at a normal (ClusterIP) Service instead of the
headless one, DNS returns the single virtual IP and the push lands on **one**
pod — the original bug. Keep `clusterIP: None`.

## Data stores

PostgreSQL and ClickHouse are expected as in-cluster Services named
`postgres` and `clickhouse` (managed operator, StatefulSet, or external
endpoint via a headless Service). They are intentionally **not** part of
these manifests — production data stores want their own lifecycle, backups
and storage classes. The Redis clusters (rate-limit / tokens / config) are
tracked separately on the roadmap.

## Redis

`50-redis.yaml` provisions the three isolated single-replica instances the
roadmap calls for (`redis-ratelimit`, `redis-tokens`, `redis-config`). Wire
them up via env:

- `ConfigSync__RedisConnection=redis-config:6379` on `veil-api` enables
  leader election + the retry queue (single active pusher across replicas).
- The edge rate-limit / token Redis backends point at `redis-ratelimit` and
  `redis-tokens` once enabled (`VEIL_REDIS_URL`).

For real HA replace the single-replica StatefulSets with a Redis Cluster or
Sentinel operator and keep the same Service names.

## Internal TLS

These manifests speak plaintext HTTP **inside** the cluster (edge→api,
edge→analytics, api→edge pushes), relying on the cluster network. To encrypt
in-cluster traffic, run a service mesh in mTLS mode (Linkerd/Istio) — no app
change needed — or terminate TLS per service and flip the URLs to `https://`
(`VEIL_CONTROL_PLANE_URL`, `VEIL_ANALYTICS_URL`, the push path). Public edge
TLS is already handled by the SNI cert resolver (Phase 5), independent of
this.

## Zero-downtime rollouts

- Control plane `Deployment`s use the default `RollingUpdate`; with
  `replicas: 2` and readiness gated on `/readyz` (DB reachable), a rollout
  never drops to zero ready pods. Add a `PodDisruptionBudget`
  (`minAvailable: 1`) for node drains.
- The edge `DaemonSet` uses `maxUnavailable: 1` and a 40s termination grace
  so each node drains in-flight connections (graceful shutdown, Phase 8)
  before the new pod takes over.
- ConfigSync leader election means a rolling control-plane update hands the
  push lock to whichever replica holds the lease — at most one pusher at a
  time, no duplicate pushes mid-rollout.

## Notes

- Liveness uses `/healthz`, readiness uses `/readyz` on every workload.
- The edge `DaemonSet` shares one node token; for per-node identity, front
  the fleet with a self-registration step instead of the shared secret.
- HPAs need `metrics-server` installed in the cluster.

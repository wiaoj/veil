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
| `40-edge.yaml`       | edge `DaemonSet` (host ports 80/443) |

## Before applying

- **Secrets**: `10-config.yaml` ships placeholders. Replace them, or create
  `veil-secrets` out-of-band (see the comment in that file) and delete the
  placeholder `Secret`.
- **Images**: build and push `veil-api`, `veil-analytics`, `veil-edge` (see
  `deploy/docker/`) to a registry your cluster can pull, and update the
  `image:` fields.

## Data stores

PostgreSQL and ClickHouse are expected as in-cluster Services named
`postgres` and `clickhouse` (managed operator, StatefulSet, or external
endpoint via a headless Service). They are intentionally **not** part of
these manifests — production data stores want their own lifecycle, backups
and storage classes. The Redis clusters (rate-limit / tokens / config) are
tracked separately on the roadmap.

## Notes

- Liveness uses `/healthz`, readiness uses `/readyz` on every workload.
- The edge `DaemonSet` shares one node token; for per-node identity, front
  the fleet with a self-registration step instead of the shared secret.
- HPAs need `metrics-server` installed in the cluster.

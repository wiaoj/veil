# Veil.IntegrationTests

Integration tests for the analytics module's thin data clients, run against
**real** PostgreSQL and ClickHouse spun up on demand with
[Testcontainers](https://dotnet.testcontainers.org/). These cover the SQL/DDL and
serialization that unit tests can't:

- `IncidentArchiveTests` — `NpgsqlIncidentArchive` schema, jsonb payload
  round-trip, newest-first ordering (PostgreSQL)
- `DailySummaryStoreTests` — `DailySummaryStore` schema + idempotent `(day, zone)`
  upsert (PostgreSQL)
- `ClickHouseLogsTests` — `ClickHouseWriter`/`ClickHouseReader` schema (incl. the
  `asn` column) + a JSONEachRow insert/read round-trip (ClickHouse)

## Requirements

A Docker-compatible container runtime. Testcontainers auto-detects Docker.
Disabling the Ryuk reaper (`TESTCONTAINERS_RYUK_DISABLED=true`) isn't required but
is recommended on Podman.

### Docker Desktop
Just run the tests — no extra setup.

### Podman (Windows)
Point Testcontainers at the Podman socket and disable Ryuk:

```bash
export DOCKER_HOST=tcp://127.0.0.1:2375        # or your forwarded Podman socket
export TESTCONTAINERS_RYUK_DISABLED=true
```

If you don't already expose a TCP/pipe Docker endpoint, forward the machine's
socket (root connection from `podman system connection list`):

```bash
ssh -i <machine-key> -p <port> -N -L 2375:/run/podman/podman.sock root@127.0.0.1
```

## Run

```bash
dotnet test tests/Veil.IntegrationTests
```

Each test starts from a clean slate (the PostgreSQL tests truncate between runs);
containers are created per collection and torn down automatically.

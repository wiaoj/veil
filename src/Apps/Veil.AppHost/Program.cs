// Veil .NET single-host orchestration (Aspire). Brings up the dev infrastructure
// (Postgres, ClickHouse) AND the .NET control plane + analytics worker with one
// `dotnet run`, correctly wired so the Phase 12 Tyto RPC (cross-process) and the
// in-memory bus (per-process) work end to end.
//
// Aspire owns container/process lifecycle and assigns ports dynamically, so we
// inject every endpoint into the apps (connection string, ClickHouse URL, the
// cross-service RPC URLs) rather than relying on fixed localhost ports. Data
// volumes persist the seeded admin user / schema across restarts.
//
// NOTE: stop any hand-started podman veil-* containers first — they'd collide
// with Aspire's container runtime.

var builder = DistributedApplication.CreateBuilder(args);

// Credentials match appsettings.Development (veil/veil). The database resource
// is named "Default" so WithReference injects ConnectionStrings__Default — the
// exact key every module reads.
var pgUser = builder.AddParameter("pg-user", "veil");
var pgPassword = builder.AddParameter("pg-password", "veil", secret: true);

var postgres = builder.AddPostgres("veil-pg", userName: pgUser, password: pgPassword)
    .WithDataVolume("veil-pg-data");
var veilDb = postgres.AddDatabase("Default", databaseName: "veil");

// ClickHouse has no first-class Aspire integration — run it as a plain container
// (same image/creds as docker-compose.yml) and inject its URL into the apps.
var clickhouse = builder.AddContainer("veil-ch", "clickhouse/clickhouse-server", "24-alpine")
    .WithEnvironment("CLICKHOUSE_USER", "veil")
    .WithEnvironment("CLICKHOUSE_PASSWORD", "veil")
    .WithEnvironment("CLICKHOUSE_DB", "veil")
    .WithEnvironment("CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT", "1")
    .WithEndpoint(targetPort: 8123, scheme: "http", name: "http")
    .WithVolume("veil-ch-data", "/var/lib/clickhouse");
var clickhouseHttp = clickhouse.GetEndpoint("http");

// Apply EF migrations + seed once, before the apps start.
var migrator = builder.AddProject<Projects.Veil_DbMigrator>("veil-migrator")
    .WithReference(veilDb)
    .WaitFor(postgres);

// Analytics worker: ingest + AI analysis. RPC server (incidents) + client (rules)
// + in-memory alert bus.
var worker = builder.AddProject<Projects.Veil_Analytics_Worker>("veil-worker")
    // Bind the app's own fixed port directly (no DCP proxy) — the apps read each
    // other's URL from config, and a proxy on the same launchSettings port
    // collides with the app's bind.
    .WithEndpoint("http", e => e.IsProxied = false)
    .WithReference(veilDb)
    .WithEnvironment("ClickHouse__Url", clickhouseHttp)
    .WithEnvironment("ClickHouse__Username", "veil")
    .WithEnvironment("ClickHouse__Password", "veil")
    .WithEnvironment("ClickHouse__Database", "veil")
    .WithEnvironment("Intelligence__Enabled", "true")
    .WaitFor(clickhouse)
    .WaitForCompletion(migrator);

// Control plane. RPC client (incidents) + server (rules). Also reads ClickHouse
// for the analytics query endpoints.
var api = builder.AddProject<Projects.Veil_Api>("veil-api")
    .WithEndpoint("http", e => e.IsProxied = false)
    .WithReference(veilDb)
    .WithEnvironment("ClickHouse__Url", clickhouseHttp)
    .WithEnvironment("ClickHouse__Username", "veil")
    .WithEnvironment("ClickHouse__Password", "veil")
    .WithEnvironment("ClickHouse__Database", "veil")
    .WaitForCompletion(migrator)
    .WaitFor(worker);

// Cross-service Tyto RPC URLs — injected from the actual allocated endpoints, so
// no hardcoded ports. (Set after both exist to avoid a definition cycle.)
api.WithEnvironment("Intelligence__WorkerUrl", worker.GetEndpoint("http"));
worker.WithEnvironment("Intelligence__ControlPlaneUrl", api.GetEndpoint("http"));

builder.Build().Run();

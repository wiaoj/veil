using Microsoft.EntityFrameworkCore;
using Tyto.DependencyInjection;
using Tyto.Rpc;
using Tyto.Rpc.Hosting.AspNetCore;
using Tyto.Rpc.Server;
using Veil.Analytics;
using Veil.Analytics.Worker.Internal;
using Veil.EdgeNodes.Infrastructure.Persistence;
using Veil.Shared;
using Veil.Shared.Observability;

var builder = WebApplication.CreateBuilder(args);

// Enums serialize as their string names (e.g. IncidentAction "Shadowed") so the
// dashboard reads stable labels rather than ordinals.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.AddModulith(builder.Configuration, builder.Environment, modules => {
    modules.AddModule<SharedModule>();
    modules.AddModule<AnalyticsModule>();
});
builder.Services.AddModulithAspNetCore();

// Tyto RPC server (Phase 12): serves the live AI incident feed to the control
// plane over RPC-over-HTTP under the /rpc prefix, replacing the bespoke
// /intelligence/incidents HTTP proxy. Handlers resolve from DI (IncidentStore).
builder.AddTyto(tyto =>
    tyto.AddRpc(rpc =>
        rpc.AddServer(server => {
            server.RegisterHandlersFromAssemblyContaining<GetIncidentsHandler>();
            server.ListenOverHttp(http => http.WithPrefix("/rpc"));
        })));

// OpenTelemetry tracing + metrics (opt-in via OTEL_EXPORTER_OTLP_ENDPOINT).
builder.Services.AddVeilTelemetry(builder.Configuration, "veil-analytics");

// Auth surface only — deliberately not the whole EdgeNodes module, so the
// node management endpoints are not exposed on the ingest port. The
// endpoint depends on the IEdgeNodeTokenVerifier contract; the DbContext
// registration exists solely to back its implementation.
builder.Services.AddDbContextFactory<EdgeNodesDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddSingleton<Veil.EdgeNodes.Contracts.IEdgeNodeTokenVerifier, Veil.EdgeNodes.EdgeNodeTokenVerifier>();

// Health: /healthz liveness, /readyz adds a PostgreSQL reachability probe.
builder.Services.AddHealthChecks()
    .AddCheck<Veil.Analytics.Worker.Health.DbReadinessHealthCheck<EdgeNodesDbContext>>(
        "database", tags: ["ready"]);

var app = builder.Build();

await app.UseModulithAsync();

// Edge → control plane log ingestion (node-token authenticated).
Veil.Analytics.Worker.Internal.IngestEndpoints.Map(app);

// Tyto RPC endpoints — incident feed is served here (GetIncidentsHandler).
app.MapTytoRpcEndpoints();

// Live AI anomaly feed (Phase 11) — legacy HTTP path. The control plane now
// reads this over Tyto RPC (/rpc); kept for direct debugging/back-compat.
app.MapGet("/intelligence/incidents",
    (Veil.Analytics.Intelligence.IncidentStore store, int? limit) =>
        Results.Ok(store.Recent(Math.Clamp(limit ?? 50, 1, 200))));

app.MapHealthChecks("/healthz", new() { Predicate = _ => false });
app.MapHealthChecks("/readyz", new() { Predicate = check => check.Tags.Contains("ready") });

// Prometheus scrape endpoint.
app.MapGet("/metrics", (Veil.Shared.Observability.MetricsCollector metrics) =>
    Results.Text(metrics.Render(), "text/plain; version=0.0.4"));

app.Run();

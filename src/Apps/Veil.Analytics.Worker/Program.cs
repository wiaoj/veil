using Microsoft.EntityFrameworkCore;
using Veil.Analytics;
using Veil.EdgeNodes.Infrastructure.Persistence;
using Veil.Shared;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddModulith(builder.Configuration, builder.Environment, modules => {
    modules.AddModule<SharedModule>();
    modules.AddModule<AnalyticsModule>();
});
builder.Services.AddModulithAspNetCore();

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

app.MapHealthChecks("/healthz", new() { Predicate = _ => false });
app.MapHealthChecks("/readyz", new() { Predicate = check => check.Tags.Contains("ready") });

app.Run();

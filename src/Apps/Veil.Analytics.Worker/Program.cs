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

// Auth store only — deliberately not the whole EdgeNodes module, so the
// node management endpoints are not exposed on the ingest port.
builder.Services.AddDbContextFactory<EdgeNodesDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

var app = builder.Build();

await app.UseModulithAsync();

// Edge → control plane log ingestion (node-token authenticated).
Veil.Analytics.Worker.Internal.IngestEndpoints.Map(app);

app.Run();

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

// Tyto RPC (Phase 12). Server: serves the live AI incident feed to the control
// plane under /rpc (replaces the /intelligence/incidents HTTP proxy). Client:
// applies AI-suggested rules by calling the control plane, replacing the bespoke
// HTTP rule applier. The X-Api-Key default header carries the control plane's
// API-key auth so the privileged rule-creation path stays protected.
var intelligenceSection = builder.Configuration.GetSection(
    Veil.Analytics.Intelligence.IntelligenceOptions.SectionName);
string controlPlaneRpcUrl =
    (intelligenceSection["ControlPlaneUrl"] ?? "http://localhost:5210").TrimEnd('/') + "/rpc";
string? controlPlaneApiKey = intelligenceSection["ControlPlaneApiKey"];

builder.AddTyto(tyto => {
    // Messaging (Phase 12 Slice 3): the analysis loop publishes IncidentRaised;
    // the webhook + SIEM sinks subscribe as independent handlers. In-memory
    // transport — publisher and subscribers are co-located in this worker.
    tyto.MessageDefinitions(define =>
        define.Add<Veil.Analytics.Intelligence.IncidentRaised>("intelligence.incident-raised", 1));
    tyto.Transports(transports => transports.AddInMemory("memory",
        options => options.Bind("veil.intelligence.events", "veil.intelligence.alerts")));
    tyto.Endpoints(endpoints =>
        endpoints.Add("INTELLIGENCE", endpoint => {
            endpoint.ListenOn("memory", "veil.intelligence.alerts");
            endpoint.Routing.Publish<Veil.Analytics.Intelligence.IncidentRaised>()
                .To("memory", "veil.intelligence.events");
            endpoint.AddHandler<Veil.Analytics.Intelligence.WebhookAlertHandler>();
            endpoint.AddHandler<Veil.Analytics.Intelligence.SiemAlertHandler>();
            // Durable archive sink: persists each incident so the feed survives
            // a restart (no-op when no PostgreSQL connection is configured).
            endpoint.AddHandler<Veil.Analytics.Intelligence.IncidentPersistenceHandler>();
        }));

    tyto.AddRpc(rpc => {
        rpc.AddServer(server => {
            server.RegisterHandlersFromAssemblyContaining<GetIncidentsHandler>();
            server.ListenOverHttp(http => http.WithPrefix("/rpc"));
        });
        rpc.AddClient(client =>
            client.UseHttp(http => {
                if(!string.IsNullOrWhiteSpace(controlPlaneApiKey))
                    http.WithHeader("X-Api-Key", controlPlaneApiKey);
                http.ConnectTo("control-plane", new Uri(controlPlaneRpcUrl))
                    .Handles<Veil.Analytics.Intelligence.ApplyAiRuleRequest>();
            }));
    });
});

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

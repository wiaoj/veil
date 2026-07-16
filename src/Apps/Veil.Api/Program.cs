using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text.Json.Serialization;
using Tyto;
using Tyto.DependencyInjection;
using Tyto.Rpc;
using Tyto.Rpc.Client;
using Tyto.Rpc.Http;
using Tyto.Rpc.Hosting.AspNetCore;
using Tyto.Rpc.Server;
using Veil.Analytics;
using Veil.Analytics.Intelligence;
using Veil.Api.ConfigSync;
using Veil.Auth;
using Veil.Certificates;
using Veil.Certificates.IntegrationEvents;
using Veil.EdgeNodes;
using Veil.EdgeNodes.Contracts.IntegrationEvents;
using Veil.Infrastructure.Security;
using Veil.Shared;
using Veil.Shared.Observability;
using Veil.Zones;
using Veil.Zones.Contracts.IntegrationEvents;
using Wiaoj.Serialization;
using Wiaoj.Serialization.SystemTextJson;
using Veil.Api.Internal;

var builder = WebApplication.CreateBuilder(args);

// Enums bind from / serialize to their string names ("RateLimit", "RoundRobin")
// instead of raw numbers, case-insensitive on input. PascalCase matches the
// hand-built status/action strings in the response DTOs.
builder.Services.ConfigureHttpJsonOptions(options => {
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddModulith(builder.Configuration, builder.Environment, modules => {
    modules.AddModule<SharedModule>();
    modules.AddModule<SecurityModule>();
    modules.AddModule<AuthModule>();
    modules.AddModule<ZoneModule>();
    modules.AddModule<CertificatesModule>();
    modules.AddModule<EdgeNodesModule>();
    // Read side only — ingestion (AnalyticsModule) runs in the worker.
    modules.AddModule<AnalyticsQueryModule>();
})
    .AddModulithAspNetCore();

// OpenTelemetry tracing + metrics (opt-in via OTEL_EXPORTER_OTLP_ENDPOINT).
builder.Services.AddVeilTelemetry(builder.Configuration, "veil-api");

// Current-user accessor over the request principal (JWT/API-key claims).
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

// AI rule application — shared by the worker RPC (auto-apply) and the dashboard
// REST endpoint (manual one-click apply). Bind the Intelligence section so the
// default rate-limit values are honoured on this side too.
builder.Services.Configure<IntelligenceOptions>(builder.Configuration.GetSection(IntelligenceOptions.SectionName));
builder.Services.AddSingleton<AiRuleService>();

builder.AddTyto(tyto => {
    tyto.MessageDefinitions(define => {
        define.Add<ZoneConfigChanged>("zones.config-changed", 1);
        define.Add<EdgeNodeRegistered>("edge-nodes.registered", 1);
        define.Add<CertificateIssued>("certificates.issued", 1);
        define.Add<CertificateRevoked>("certificates.revoked", 1);
    });
    // Publishes go to the 'veil.events' exchange; the in-memory broker
    // fans out to bound queues (RabbitMQ topology semantics).
    tyto.Transports(transports => transports.AddInMemory("memory",
        options => options.Bind("veil.events", "veil.config-sync")));
    tyto.Endpoints(endpoints => {
        endpoints.Add("CONFIG-SYNC", endpoint => {
            endpoint.ListenOn("memory", "veil.config-sync");
            endpoint.Routing.Publish<ZoneConfigChanged>().To("memory", "veil.events");
            endpoint.Routing.Publish<EdgeNodeRegistered>().To("memory", "veil.events");
            endpoint.Routing.Publish<CertificateIssued>().To("memory", "veil.events");
            endpoint.Routing.Publish<CertificateRevoked>().To("memory", "veil.events");
            endpoint.AddHandler<ZoneConfigChangedHandler>();
            endpoint.AddHandler<EdgeNodeRegisteredHandler>();
            endpoint.AddHandler<CertificateIssuedHandler>();
            endpoint.AddHandler<CertificateRevokedHandler>();
        });
    });

    // Tyto RPC (Phase 12). Client: the dashboard incident feed calls the analytics
    // worker over RPC-over-HTTP. Server: serves AI rule application back to the
    // worker (ApplyAiRuleHandler) under /rpc, behind the default API-key auth.
    string workerRpcUrl =
        (builder.Configuration.GetSection("Intelligence")["WorkerUrl"] ?? "http://localhost:5001")
            .TrimEnd('/') + "/rpc";
    tyto.AddRpc(rpc => {
        rpc.AddClient(client =>
            client.UseHttp(http =>
                http.ConnectTo("intelligence", new Uri(workerRpcUrl))
                    .Handles<GetIncidentsRequest>()));
        rpc.AddServer(server => {
            server.RegisterHandlersFromAssemblyContaining<Veil.Api.Internal.ApplyAiRuleHandler>();
            server.ListenOverHttp(http => http.WithPrefix("/rpc"));
        });
    });
});

// Config sync: pushes signed zone snapshots to edge nodes on change.
builder.Services.AddSingleton<ConfigPushSignal>();
builder.Services.AddSingleton<ZoneCertificateProvider>();

// Multi-replica coordination: Redis leader election + retry queue when a
// connection string is configured, otherwise single-instance local.
string? configSyncRedis = builder.Configuration
    .GetSection(ConfigSyncOptions.SectionName)["RedisConnection"];
if(!string.IsNullOrWhiteSpace(configSyncRedis)) {
    builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(
        _ => StackExchange.Redis.ConnectionMultiplexer.Connect(configSyncRedis));
    builder.Services.AddSingleton<IPushCoordinator, RedisPushCoordinator>();
}
else {
    builder.Services.AddSingleton<IPushCoordinator, LocalPushCoordinator>();
}
builder.Services.Configure<ConfigSyncOptions>(
    builder.Configuration.GetSection(ConfigSyncOptions.SectionName));

// Node identity lives in PostgreSQL; where that node currently *is* depends on
// the deployment. Static (VMs/compose) uses the registered address; Dns resolves
// a Kubernetes headless Service to every ready pod; Redis reads TTL'd
// self-registrations for dynamic non-Kubernetes fleets.
DiscoveryMode discoveryMode = builder.Configuration
    .GetSection(ConfigSyncOptions.SectionName)
    .GetSection(nameof(ConfigSyncOptions.Discovery))
    .GetValue(nameof(DiscoveryOptions.Mode), DiscoveryMode.Static);

switch(discoveryMode) {
    case DiscoveryMode.Dns:
        builder.Services.AddSingleton<IEdgeNodeLocator, DnsEdgeNodeLocator>();
        break;
    case DiscoveryMode.Redis when !string.IsNullOrWhiteSpace(configSyncRedis):
        builder.Services.AddSingleton<IEdgeNodeLocator, RedisEdgeNodeLocator>();
        break;
    case DiscoveryMode.Redis:
        throw new InvalidOperationException(
            "ConfigSync:Discovery:Mode is Redis but ConfigSync:RedisConnection is not set.");
    default:
        builder.Services.AddSingleton<IEdgeNodeLocator, StaticEdgeNodeLocator>();
        break;
}

builder.Services.AddHttpClient(ConfigSyncService.HttpClientName,
    client => client.Timeout = TimeSpan.FromSeconds(10));

// Schema registry (Vaultify) — the store behind body_schema positive validation.
// Uploads register + validate there; the concrete schema is resolved into the
// snapshot at push time. Unset Vaultify:BaseUrl → the feature is off.
builder.Services.Configure<Veil.Api.Schemas.VaultifyOptions>(
    builder.Configuration.GetSection(Veil.Api.Schemas.VaultifyOptions.SectionName));
string? vaultifyUrl = builder.Configuration
    .GetSection(Veil.Api.Schemas.VaultifyOptions.SectionName)[nameof(Veil.Api.Schemas.VaultifyOptions.BaseUrl)];
if(!string.IsNullOrWhiteSpace(vaultifyUrl)) {
    builder.Services.AddHttpClient(Veil.Api.Schemas.VaultifySchemaRegistry.HttpClientName,
        client => client.Timeout = TimeSpan.FromSeconds(10));
    builder.Services.AddSingleton<Veil.Api.Schemas.ISchemaRegistry, Veil.Api.Schemas.VaultifySchemaRegistry>();
}
else {
    builder.Services.AddSingleton<Veil.Api.Schemas.ISchemaRegistry, Veil.Api.Schemas.DisabledSchemaRegistry>();
}

builder.Services.AddHostedService<ConfigSyncService>();

// ACME: provisions/renews certificates, publishing HTTP-01 challenges to
// edge nodes. Idles unless Certificates:AcmeDirectoryUrl + EncryptionKey set.
builder.Services.AddSingleton<Veil.Api.Acme.EdgeChallengePublisher>();
builder.Services.AddHostedService<Veil.Api.Acme.AcmeProvisioningService>();

// Health: /healthz is liveness (process up), /readyz adds a DB reachability
// probe tagged "ready".
builder.Services.AddHealthChecks()
    .AddCheck<Veil.Api.Health.DbReadinessHealthCheck<Veil.Zones.Infrastructure.Persistence.ZonesDbContext>>(
        "database", tags: ["ready"]);


var app = builder.Build();

await app.UseModulithAsync();

// Internal control-plane → edge endpoints (node-token authenticated).
Veil.Api.Internal.EdgeConfigEndpoints.Map(app);

// Dashboard-facing proxy to the analytics worker's live AI incident feed.
Veil.Api.Internal.IntelligenceEndpoints.Map(app);
Veil.Api.Schemas.SchemaEndpoints.Map(app);

// Tyto RPC endpoints — AI rule application (ApplyAiRuleHandler). Behind the
// default auth policy, so callers must present a valid API key.
app.MapTytoRpcEndpoints();

// Liveness vs readiness — both bypass the fallback auth policy so probes
// work without credentials.
app.MapHealthChecks("/healthz", new() {
    Predicate = _ => false
}).AllowAnonymous();
app.MapHealthChecks("/readyz", new() {
    Predicate = check => check.Tags.Contains("ready")
}).AllowAnonymous();

// Prometheus scrape endpoint.
app.MapGet("/metrics", (MetricsCollector metrics) =>
        Results.Text(metrics.Render(), "text/plain; version=0.0.4"))
   .AllowAnonymous();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

// In Development the edge nodes pull config over plain HTTP (:5150) and the
// Vite dev proxy forwards /v1/* there too; forcing HTTPS would 307 them to the
// self-signed :7248 endpoint, which neither client follows.
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.Run();
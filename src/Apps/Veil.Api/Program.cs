using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text;
using System.Text.Json.Serialization;
using Tyto;
using Tyto.DependencyInjection;
using Veil.Api.ConfigSync;
using Veil.EdgeNodes.IntegrationEvents;
using Veil.Shared;
using Veil.Zones.IntegrationEvents;
using Wiaoj.Primitives.Obfuscation;
using Wiaoj.Serialization;
using Wiaoj.Serialization.SystemTextJson;

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
    modules.AddModule<Veil.Zones.ZoneModule>();
    modules.AddModule<Veil.EdgeNodes.EdgeNodesModule>();
});
builder.Services.AddModulithAspNetCore();

// The Wiaoj.Ddd EF integration registers its dispatcher/interceptors scoped
// (designed for AddDbContext); our contexts come from singleton factories,
// so the whole chain must resolve from the root provider. Lift the scoped
// Ddd registrations to singleton — their dependencies are all singletons.
LiftDddRegistrationsToSingleton(builder.Services);

// Tyto event bus — in-memory transport; integration events published by the
// outbox processor are handled in-process by the ConfigSync handlers.
builder.Services.TryAddSingleton<ISerializer<TytoJsonSerializerKey>>(
    new SystemTextJsonSerializer<TytoJsonSerializerKey>(new System.Text.Json.JsonSerializerOptions()));
builder.AddTyto(tyto => {
    tyto.MessageDefinitions(define => {
        define.Add<ZoneConfigChanged>("zones.config-changed", 1);
        define.Add<EdgeNodeRegistered>("edge-nodes.registered", 1);
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
            endpoint.AddHandler<ZoneConfigChangedHandler>();
            endpoint.AddHandler<EdgeNodeRegisteredHandler>();
        });
    });
});

// Config sync: pushes signed zone snapshots to edge nodes on change.
builder.Services.AddSingleton<ConfigPushSignal>();
builder.Services.Configure<ConfigSyncOptions>(
    builder.Configuration.GetSection(ConfigSyncOptions.SectionName));
builder.Services.AddHttpClient(ConfigSyncService.HttpClientName,
    client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHostedService<ConfigSyncService>();


var app = builder.Build();

await app.UseModulithAsync();

// Internal control-plane → edge endpoints (node-token authenticated).
Veil.Api.Internal.EdgeConfigEndpoints.Map(app);

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.Run();

// Rewrites the scoped Wiaoj.Ddd descriptors (dispatcher, audit/dispatch
// interceptors and their ISaveChangesInterceptor forwards) to singleton so
// they can be resolved by the singleton DbContext factories.
static void LiftDddRegistrationsToSingleton(IServiceCollection services) {
    for(int i = 0; i < services.Count; i++) {
        ServiceDescriptor descriptor = services[i];
        if(descriptor.Lifetime != ServiceLifetime.Scoped)
            continue;

        bool isDdd = (descriptor.ServiceType.FullName?.StartsWith("Wiaoj.Ddd") ?? false)
            || (descriptor.ImplementationType?.FullName?.StartsWith("Wiaoj.Ddd") ?? false)
            || descriptor.ServiceType == typeof(ISaveChangesInterceptor);
        if(!isDdd)
            continue;

        services[i] = descriptor.ImplementationType is not null
            ? ServiceDescriptor.Singleton(descriptor.ServiceType, descriptor.ImplementationType)
            : descriptor.ImplementationFactory is not null
                ? ServiceDescriptor.Singleton(descriptor.ServiceType, descriptor.ImplementationFactory)
                : descriptor;
    }
}

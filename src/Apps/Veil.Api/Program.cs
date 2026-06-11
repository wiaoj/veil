using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text;
using System.Text.Json.Serialization;
using Veil.Shared;
using Wiaoj.Primitives.Obfuscation;

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

// Config sync: pushes signed zone snapshots to edge nodes on change.
builder.Services.Configure<Veil.Api.ConfigSync.ConfigSyncOptions>(
    builder.Configuration.GetSection(Veil.Api.ConfigSync.ConfigSyncOptions.SectionName));
builder.Services.AddHttpClient(Veil.Api.ConfigSync.ConfigSyncService.HttpClientName,
    client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHostedService<Veil.Api.ConfigSync.ConfigSyncService>();


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

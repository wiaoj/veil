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


var app = builder.Build();

await app.UseModulithAsync();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast =  Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}

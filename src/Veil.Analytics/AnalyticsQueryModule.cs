using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Veil.Analytics.ClickHouse;
using Wiaoj.Modulith;

namespace Veil.Analytics;

/// <summary>
/// Read side of the analytics pipeline, hosted in Veil.Api: maps the
/// /v1/analytics/* query endpoints over the ClickHouse request log.
/// Deliberately separate from <see cref="AnalyticsModule"/> (ingestion),
/// which runs in the analytics worker — the two sides scale and deploy
/// independently.
/// </summary>
public sealed class AnalyticsQueryModule : IWebModule {
    public string Name => nameof(AnalyticsQueryModule);

    public void Register(IServiceCollection services, IConfiguration configuration) {
        services.Configure<ClickHouseOptions>(configuration.GetSection(ClickHouseOptions.SectionName));

        services.AddHttpClient(ClickHouseWriter.HttpClientName,
            client => client.Timeout = TimeSpan.FromSeconds(10));

        services.AddSingleton<ClickHouseReader>();
    }

    public Task ConfigureAsync(IApplicationBuilder app) {
        if(app is IEndpointRouteBuilder rb) {
            rb.MapEndpoints<AnalyticsQueryModule>();
        }

        return Task.CompletedTask;
    }
}

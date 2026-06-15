using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Veil.Analytics.Aggregation;
using Veil.Analytics.ClickHouse;
using Veil.Analytics.Ingestion;
using Veil.Analytics.Intelligence;
using Veil.Analytics.Siem;
using Wiaoj.Modulith;

namespace Veil.Analytics;

/// <summary>
/// Request log ingestion: bounded in-process queue + ClickHouse bulk writer.
/// The <c>/ingest</c> endpoint itself lives in the host — it composes this
/// module with EdgeNodes (node token authentication), same split as the
/// internal config endpoint in Veil.Api.
/// </summary>
public sealed class AnalyticsModule : IModule {
    public string Name => nameof(AnalyticsModule);

    public void Register(IServiceCollection services, IConfiguration configuration) {
        services.Configure<ClickHouseOptions>(configuration.GetSection(ClickHouseOptions.SectionName));

        services.AddHttpClient(ClickHouseWriter.HttpClientName,
            client => client.Timeout = TimeSpan.FromSeconds(10));

        services.AddSingleton<ClickHouseWriter>();
        services.AddSingleton<RequestLogQueue>();
        services.AddHostedService<ClickHouseFlushService>();

        // Optional SIEM export (NDJSON over HTTP). Active only when configured.
        services.Configure<SiemOptions>(configuration.GetSection(SiemOptions.SectionName));
        string? siemEndpoint = configuration.GetSection(SiemOptions.SectionName)[nameof(SiemOptions.Endpoint)];
        if(!string.IsNullOrWhiteSpace(siemEndpoint)) {
            services.AddHttpClient(HttpSiemExporter.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));
            services.AddSingleton<ISiemExporter, HttpSiemExporter>();
        }
        else {
            services.AddSingleton<ISiemExporter, NullSiemExporter>();
        }

        // Live AI traffic analysis (Phase 11). Opt-in: when disabled, a no-op
        // analyzer is registered so the ingest hot path can resolve it cheaply
        // and nothing else (timer, Claude client) is wired up.
        services.Configure<IntelligenceOptions>(configuration.GetSection(IntelligenceOptions.SectionName));
        IntelligenceOptions intelligence =
            configuration.GetSection(IntelligenceOptions.SectionName).Get<IntelligenceOptions>() ?? new IntelligenceOptions();
        services.AddSingleton<IncidentStore>(_ => new IncidentStore(intelligence.MaxIncidents));
        if(intelligence.Enabled) {
            // ML.NET spike detector (in-process; no external dependency). This is
            // the detection brain — the LLM layer below is optional enrichment.
            services.AddSingleton(_ => new MlAnomalyDetector(intelligence.MlConfidence, intelligence.MlMinHistory));
            services.AddSingleton<ITrafficAnalyzer, TrafficAnalyzer>();

            if(!string.IsNullOrWhiteSpace(intelligence.AnthropicApiKey)) {
                services.AddHttpClient(AnthropicAnalystClient.HttpClientName,
                    client => client.Timeout = TimeSpan.FromSeconds(30));
                services.AddSingleton<IAnalystClient, AnthropicAnalystClient>();
            }
            else {
                services.AddSingleton<IAnalystClient, NullAnalystClient>();
            }

            // Real rule application calls Veil.Api; without an API key we only
            // log the decision (the prototype default).
            if(!string.IsNullOrWhiteSpace(intelligence.ControlPlaneApiKey)) {
                services.AddHttpClient(HttpRuleApplier.HttpClientName,
                    client => client.Timeout = TimeSpan.FromSeconds(10));
                services.AddSingleton<IRuleApplier, HttpRuleApplier>();
            }
            else {
                services.AddSingleton<IRuleApplier, LoggingRuleApplier>();
            }

            services.AddHostedService<TrafficAnalysisService>();
        }
        else {
            services.AddSingleton<ITrafficAnalyzer, NullTrafficAnalyzer>();
        }

        // Nightly rollup: ClickHouse → PostgreSQL daily summary. Skipped when
        // no PostgreSQL connection string is configured (e.g. CH-only test
        // setups) rather than crashing the worker at startup.
        services.AddSingleton<ClickHouseReader>();
        string? connectionString = configuration.GetConnectionString("Default");
        if(!string.IsNullOrWhiteSpace(connectionString)) {
            services.AddSingleton(new DailySummaryStore(connectionString));
            services.AddHostedService<DailyAggregationService>();
        }
    }
}

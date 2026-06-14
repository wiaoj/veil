using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Veil.Shared.Observability;

/// <summary>
/// OpenTelemetry wiring for the control-plane services. Distributed tracing
/// and runtime/HTTP metrics are exported over OTLP, but only when an exporter
/// endpoint is configured — otherwise this is a no-op so there is zero
/// overhead and no failed-export noise by default.
/// </summary>
public static class TelemetryExtensions {
    /// <summary>
    /// Standard OpenTelemetry env var; when set (e.g. <c>http://otel-collector:4317</c>)
    /// tracing + metrics are enabled and exported via OTLP.
    /// </summary>
    public const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    public static IServiceCollection AddVeilTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName) {
        string? endpoint = configuration[OtlpEndpointKey]
            ?? Environment.GetEnvironmentVariable(OtlpEndpointKey);
        if(string.IsNullOrWhiteSpace(endpoint))
            return services;

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter());

        return services;
    }
}

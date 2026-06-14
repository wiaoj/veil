namespace Veil.Analytics.Siem;

/// <summary>
/// Optional export of request logs to an external SIEM. When
/// <see cref="Endpoint"/> is unset, export is disabled (a no-op exporter is
/// registered).
/// </summary>
public sealed record SiemOptions {
    public const string SectionName = "Siem";

    /// <summary>HTTP endpoint receiving newline-delimited JSON (NDJSON) batches.</summary>
    public string? Endpoint { get; init; }

    /// <summary>Optional auth header name, e.g. <c>Authorization</c>.</summary>
    public string? ApiKeyHeader { get; init; }

    /// <summary>Optional auth header value, e.g. <c>Bearer …</c>.</summary>
    public string? ApiKey { get; init; }
}

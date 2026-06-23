using System.Text.Json.Serialization;

namespace Veil.Analytics.Ingestion;

/// <summary>
/// One row of <c>request_logs</c> in the exact JSONEachRow shape ClickHouse
/// ingests. Property names are column names; <see cref="Ts"/> serialises as
/// ISO 8601 and is parsed with <c>date_time_input_format=best_effort</c>.
/// </summary>
public sealed record RequestLogRow(
    [property: JsonPropertyName("ts")] DateTime Ts,
    [property: JsonPropertyName("node_id")] string NodeId,
    [property: JsonPropertyName("zone")] string Zone,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("verdict")] string Verdict,
    [property: JsonPropertyName("rule_id")] string RuleId,
    [property: JsonPropertyName("client_ip")] string ClientIp,
    [property: JsonPropertyName("user_agent")] string UserAgent,
    [property: JsonPropertyName("duration_ms")] long DurationMs,
    [property: JsonPropertyName("asn")] uint Asn);

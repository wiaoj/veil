using System.Text.Json.Serialization;

namespace Veil.Analytics.Contracts;

// Wire contract for edge → control plane log batches. Property names are
// snake_case and match the Rust serde model exactly
// (see edge/src/analytics/mod.rs).

public sealed record IngestRequest(
    [property: JsonPropertyName("node_id")] string NodeId,
    [property: JsonPropertyName("records")] List<IngestRecord> Records);

public sealed record IngestRecord(
    [property: JsonPropertyName("ts_ms")] long TimestampMs,
    [property: JsonPropertyName("zone")] string? Zone,
    [property: JsonPropertyName("host")] string? Host,
    [property: JsonPropertyName("method")] string? Method,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("verdict")] string? Verdict,
    [property: JsonPropertyName("rule_id")] string? RuleId,
    [property: JsonPropertyName("client_ip")] string? ClientIp,
    [property: JsonPropertyName("user_agent")] string? UserAgent,
    [property: JsonPropertyName("duration_ms")] long DurationMs);

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
    [property: JsonPropertyName("duration_ms")] long DurationMs,
    [property: JsonPropertyName("asn")] uint? Asn = null);

// Human-verification interaction telemetry (challenge / widget verify outcomes
// + behavioural features). Matches the Rust InteractionRecord serde model
// (see edge/src/analytics/mod.rs).

public sealed record InteractionIngestRequest(
    [property: JsonPropertyName("node_id")] string NodeId,
    [property: JsonPropertyName("records")] List<InteractionIngestRecord> Records);

public sealed record InteractionIngestRecord(
    [property: JsonPropertyName("ts_ms")] long TimestampMs,
    [property: JsonPropertyName("zone")] string? Zone,
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("tier")] int Tier,
    [property: JsonPropertyName("outcome")] string? Outcome,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("client_ip")] string? ClientIp,
    [property: JsonPropertyName("asn")] uint? Asn,
    [property: JsonPropertyName("country")] string? Country,
    [property: JsonPropertyName("event_count")] uint? EventCount,
    [property: JsonPropertyName("path_length")] double? PathLength,
    [property: JsonPropertyName("straight_line")] double? StraightLine,
    [property: JsonPropertyName("duration_ms")] long? DurationMs,
    [property: JsonPropertyName("time_to_first_ms")] long? TimeToFirstMs,
    [property: JsonPropertyName("timing_jitter_ms")] double? TimingJitterMs);

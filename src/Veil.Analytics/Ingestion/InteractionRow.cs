using System.Text.Json.Serialization;

namespace Veil.Analytics.Ingestion;

/// <summary>
/// One row of <c>challenge_interactions</c> in the exact JSONEachRow shape
/// ClickHouse ingests. Property names are column names; <see cref="Ts"/>
/// serialises as ISO 8601 (parsed with <c>date_time_input_format=best_effort</c>).
/// This is the labelled-ish dataset the ML layer trains on: behavioural features
/// plus a weak label (<see cref="Outcome"/> pass/fail).
/// </summary>
public sealed record InteractionRow(
    [property: JsonPropertyName("ts")] DateTime Ts,
    [property: JsonPropertyName("node_id")] string NodeId,
    [property: JsonPropertyName("zone")] string Zone,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("tier")] int Tier,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("client_ip")] string ClientIp,
    [property: JsonPropertyName("asn")] uint Asn,
    [property: JsonPropertyName("country")] string Country,
    [property: JsonPropertyName("event_count")] uint EventCount,
    [property: JsonPropertyName("path_length")] double PathLength,
    [property: JsonPropertyName("straight_line")] double StraightLine,
    [property: JsonPropertyName("duration_ms")] long DurationMs,
    [property: JsonPropertyName("time_to_first_ms")] long TimeToFirstMs,
    [property: JsonPropertyName("timing_jitter_ms")] double TimingJitterMs);

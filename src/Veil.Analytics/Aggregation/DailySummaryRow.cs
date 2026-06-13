using System.Text.Json.Serialization;

namespace Veil.Analytics.Aggregation;

/// <summary>
/// One day's verdict rollup for a single zone, as projected out of
/// ClickHouse and stored in PostgreSQL. Property names match the ClickHouse
/// SELECT aliases (JSONEachRow) and the snake_case PG columns.
/// </summary>
public sealed record DailySummaryRow(
    [property: JsonPropertyName("day")] string Day,
    [property: JsonPropertyName("zone")] string Zone,
    [property: JsonPropertyName("total")] long Total,
    [property: JsonPropertyName("allowed")] long Allowed,
    [property: JsonPropertyName("blocked")] long Blocked,
    [property: JsonPropertyName("challenged")] long Challenged,
    [property: JsonPropertyName("challenge_passed")] long ChallengePassed,
    [property: JsonPropertyName("rate_limited")] long RateLimited,
    [property: JsonPropertyName("unique_ips")] long UniqueIps);

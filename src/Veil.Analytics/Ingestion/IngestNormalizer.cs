using Veil.Analytics.Contracts;

namespace Veil.Analytics.Ingestion;

/// <summary>
/// Validation + enrichment between the wire batch and ClickHouse rows.
/// Oversized fields are truncated, unusable records are dropped (analytics
/// is lossy by design — a malformed record must never fail the batch), and
/// the authenticated node id is stamped onto every row.
/// </summary>
public static class IngestNormalizer {
    public const int MaxBatchRecords = 5_000;

    private const int MaxZoneLength = 256;
    private const int MaxHostLength = 256;
    private const int MaxMethodLength = 16;
    private const int MaxPathLength = 2_048;
    private const int MaxVerdictLength = 32;
    private const int MaxRuleIdLength = 64;
    private const int MaxClientIpLength = 64;
    private const int MaxUserAgentLength = 1_024;

    /// <summary>
    /// Clock drift beyond this window replaces the edge timestamp with the
    /// server's receive time rather than polluting time-series queries.
    /// </summary>
    private static readonly TimeSpan MaxClockDrift = TimeSpan.FromHours(1);

    public static List<RequestLogRow> Normalize(
        string nodeId,
        IReadOnlyList<IngestRecord> records,
        DateTimeOffset receivedAtUtc) {
        List<RequestLogRow> rows = new(Math.Min(records.Count, MaxBatchRecords));

        foreach(IngestRecord record in records.Take(MaxBatchRecords)) {
            if(record.Status is < 100 or > 999)
                continue;

            DateTimeOffset ts = DateTimeOffset.FromUnixTimeMilliseconds(
                Math.Clamp(record.TimestampMs, 0, DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()));
            if((receivedAtUtc - ts).Duration() > MaxClockDrift)
                ts = receivedAtUtc;

            rows.Add(new RequestLogRow(
                ts.UtcDateTime,
                nodeId,
                Clip(record.Zone, MaxZoneLength, fallback: "-"),
                Clip(record.Host, MaxHostLength),
                Clip(record.Method, MaxMethodLength),
                Clip(record.Path, MaxPathLength),
                record.Status,
                Clip(record.Verdict, MaxVerdictLength, fallback: "unknown"),
                Clip(record.RuleId, MaxRuleIdLength),
                Clip(record.ClientIp, MaxClientIpLength),
                Clip(record.UserAgent, MaxUserAgentLength),
                Math.Max(record.DurationMs, 0)));
        }

        return rows;
    }

    private static string Clip(string? value, int maxLength, string fallback = "") {
        if(string.IsNullOrEmpty(value))
            return fallback;
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}

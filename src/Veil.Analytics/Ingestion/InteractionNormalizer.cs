using Veil.Analytics.Contracts;

namespace Veil.Analytics.Ingestion;

/// <summary>
/// Wire batch → ClickHouse rows for interaction telemetry. Lossy by design:
/// oversized fields are truncated and the authenticated node id is stamped on
/// every row. Mirrors <see cref="IngestNormalizer"/>.
/// </summary>
public static class InteractionNormalizer {
    public const int MaxBatchRecords = 5_000;

    private const int MaxZoneLength = 256;
    private const int MaxShortLength = 32;
    private const int MaxClientIpLength = 64;

    private static readonly TimeSpan MaxClockDrift = TimeSpan.FromHours(1);

    public static List<InteractionRow> Normalize(
        string nodeId,
        IReadOnlyList<InteractionIngestRecord> records,
        DateTimeOffset receivedAtUtc) {
        List<InteractionRow> rows = new(Math.Min(records.Count, MaxBatchRecords));

        foreach(InteractionIngestRecord record in records.Take(MaxBatchRecords)) {
            DateTimeOffset ts = DateTimeOffset.FromUnixTimeMilliseconds(
                Math.Clamp(record.TimestampMs, 0, DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()));
            if((receivedAtUtc - ts).Duration() > MaxClockDrift)
                ts = receivedAtUtc;

            rows.Add(new InteractionRow(
                ts.UtcDateTime,
                nodeId,
                Clip(record.Zone, MaxZoneLength, fallback: "-"),
                Clip(record.Kind, MaxShortLength, fallback: "unknown"),
                Math.Clamp(record.Tier, 0, 2),
                Clip(record.Outcome, MaxShortLength, fallback: "unknown"),
                Clip(record.Reason, MaxShortLength),
                Clip(record.ClientIp, MaxClientIpLength),
                record.Asn ?? 0,
                Clip(record.Country, MaxShortLength),
                record.EventCount ?? 0,
                NonNegative(record.PathLength),
                NonNegative(record.StraightLine),
                Math.Max(record.DurationMs ?? 0, 0),
                Math.Max(record.TimeToFirstMs ?? 0, 0),
                NonNegative(record.TimingJitterMs)));
        }

        return rows;
    }

    private static double NonNegative(double? value) => value is > 0 ? value.Value : 0;

    private static string Clip(string? value, int maxLength, string fallback = "") {
        if(string.IsNullOrEmpty(value))
            return fallback;
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Text.Json.Serialization;
using Veil.Analytics.ClickHouse;
using Wiaoj.Endpoints;
using IHttpResult = Microsoft.AspNetCore.Http.IResult;

namespace Veil.Analytics.Features.Queries;

public sealed record AnalyticsSummaryResponse(
    int WindowHours,
    long Total,
    long Allowed,
    long Blocked,
    long Challenged,
    long ChallengePassed,
    long RateLimited,
    long UniqueIps,
    double AvgDurationMs,
    List<RequestVolumePoint> Series);

/// <summary>One time-series bucket for the request volume chart.</summary>
public sealed record RequestVolumePoint(
    DateTimeOffset Bucket,
    long Total,
    long Blocked,
    long Challenged,
    long RateLimited);

public sealed class GetAnalyticsSummaryEndpoint : IEndpoint {
    public void Map(IEndpointRouteBuilder app) {
        app.MapGet("/v1/analytics/summary", Handle)
           .WithName("GetAnalyticsSummary")
           .WithTags("Analytics")
           .WithSummary("Request totals and volume time series")
           .WithDescription("Aggregates the request log over the given window (default 24h, max 30 days), optionally filtered to one zone (hostname).")
           .Produces<AnalyticsSummaryResponse>(StatusCodes.Status200OK);
    }

    private sealed record TotalsRow(
        [property: JsonPropertyName("total")] long Total,
        [property: JsonPropertyName("allowed")] long Allowed,
        [property: JsonPropertyName("blocked")] long Blocked,
        [property: JsonPropertyName("challenged")] long Challenged,
        [property: JsonPropertyName("challenge_passed")] long ChallengePassed,
        [property: JsonPropertyName("rate_limited")] long RateLimited,
        [property: JsonPropertyName("unique_ips")] long UniqueIps,
        [property: JsonPropertyName("avg_duration_ms")] double AvgDurationMs);

    private sealed record SeriesRow(
        [property: JsonPropertyName("bucket_unix")] long BucketUnix,
        [property: JsonPropertyName("total")] long Total,
        [property: JsonPropertyName("blocked")] long Blocked,
        [property: JsonPropertyName("challenged")] long Challenged,
        [property: JsonPropertyName("rate_limited")] long RateLimited);

    private static async Task<IHttpResult> Handle(
        ClickHouseReader reader,
        CancellationToken cancellationToken,
        string? zone = null,
        int hours = AnalyticsQueryWindow.DefaultHours) {

        hours = AnalyticsQueryWindow.ClampHours(hours);
        (string where, Dictionary<string, string>? parameters) = AnalyticsQueryWindow.Filter(hours, zone);

        List<TotalsRow> totals = await reader.QueryAsync<TotalsRow>($"""
            SELECT
                count() AS total,
                countIf(verdict = 'allow') AS allowed,
                countIf(verdict = 'block') AS blocked,
                countIf(verdict = 'challenge') AS challenged,
                countIf(verdict = 'challenge_pass') AS challenge_passed,
                countIf(verdict = 'rate_limited') AS rate_limited,
                uniqExact(client_ip) AS unique_ips,
                round(ifNotFinite(avg(duration_ms), 0), 2) AS avg_duration_ms
            FROM request_logs
            WHERE {where}
            """, parameters, cancellationToken);

        int bucketMinutes = AnalyticsQueryWindow.BucketMinutes(hours);
        List<SeriesRow> series = await reader.QueryAsync<SeriesRow>($"""
            SELECT
                toUnixTimestamp(toStartOfInterval(ts, INTERVAL {bucketMinutes} MINUTE)) AS bucket_unix,
                count() AS total,
                countIf(verdict = 'block') AS blocked,
                countIf(verdict = 'challenge') AS challenged,
                countIf(verdict = 'rate_limited') AS rate_limited
            FROM request_logs
            WHERE {where}
            GROUP BY bucket_unix
            ORDER BY bucket_unix
            """, parameters, cancellationToken);

        TotalsRow t = totals.FirstOrDefault()
            ?? new TotalsRow(0, 0, 0, 0, 0, 0, 0, 0);

        return Results.Ok(new AnalyticsSummaryResponse(
            hours,
            t.Total, t.Allowed, t.Blocked, t.Challenged, t.ChallengePassed, t.RateLimited,
            t.UniqueIps, t.AvgDurationMs,
            series.Select(s => new RequestVolumePoint(
                DateTimeOffset.FromUnixTimeSeconds(s.BucketUnix),
                s.Total, s.Blocked, s.Challenged, s.RateLimited)).ToList()));
    }
}

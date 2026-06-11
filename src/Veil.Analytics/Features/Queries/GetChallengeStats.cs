using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Text.Json.Serialization;
using Veil.Analytics.ClickHouse;
using Wiaoj.Endpoints;
using IHttpResult = Microsoft.AspNetCore.Http.IResult;

namespace Veil.Analytics.Features.Queries;

/// <summary>
/// Challenge funnel: <paramref name="Issued"/> counts challenge pages
/// served, <paramref name="Passed"/> counts requests that presented a valid
/// challenge token. The pass rate is passed / (issued + passed) — a solved
/// challenge stops producing 'challenge' verdicts for its token lifetime.
/// </summary>
public sealed record ChallengeStatsResponse(
    int WindowHours,
    long Issued,
    long Passed,
    double PassRate);

public sealed class GetChallengeStatsEndpoint : IEndpoint {
    public void Map(IEndpointRouteBuilder app) {
        app.MapGet("/v1/analytics/challenges", Handle)
           .WithName("GetChallengeStats")
           .WithTags("Analytics")
           .WithSummary("Challenge issue/pass statistics")
           .Produces<ChallengeStatsResponse>(StatusCodes.Status200OK);
    }

    private sealed record Row(
        [property: JsonPropertyName("issued")] long Issued,
        [property: JsonPropertyName("passed")] long Passed);

    private static async Task<IHttpResult> Handle(
        ClickHouseReader reader,
        CancellationToken cancellationToken,
        string? zone = null,
        int hours = AnalyticsQueryWindow.DefaultHours) {

        hours = AnalyticsQueryWindow.ClampHours(hours);
        (string where, Dictionary<string, string>? parameters) = AnalyticsQueryWindow.Filter(hours, zone);

        List<Row> rows = await reader.QueryAsync<Row>($"""
            SELECT
                countIf(verdict = 'challenge') AS issued,
                countIf(verdict = 'challenge_pass') AS passed
            FROM request_logs
            WHERE {where}
            """, parameters, cancellationToken);

        Row stats = rows.FirstOrDefault() ?? new Row(0, 0);
        long attempts = stats.Issued + stats.Passed;
        double passRate = attempts == 0 ? 0 : Math.Round((double)stats.Passed / attempts, 4);

        return Results.Ok(new ChallengeStatsResponse(hours, stats.Issued, stats.Passed, passRate));
    }
}

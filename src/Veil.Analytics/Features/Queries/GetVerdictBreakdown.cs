using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Text.Json.Serialization;
using Veil.Analytics.ClickHouse;
using Wiaoj.Endpoints;
using IHttpResult = Microsoft.AspNetCore.Http.IResult;

namespace Veil.Analytics.Features.Queries;

public sealed record VerdictCount(string Verdict, long Total);

public sealed record VerdictBreakdownResponse(int WindowHours, List<VerdictCount> Items);

public sealed class GetVerdictBreakdownEndpoint : IEndpoint {
    public void Map(IEndpointRouteBuilder app) {
        app.MapGet("/v1/analytics/verdicts", Handle)
           .WithName("GetVerdictBreakdown")
           .WithTags("Analytics")
           .WithSummary("Request counts per verdict")
           .Produces<VerdictBreakdownResponse>(StatusCodes.Status200OK);
    }

    private sealed record Row(
        [property: JsonPropertyName("verdict")] string Verdict,
        [property: JsonPropertyName("total")] long Total);

    private static async Task<IHttpResult> Handle(
        ClickHouseReader reader,
        CancellationToken cancellationToken,
        string? zone = null,
        int hours = AnalyticsQueryWindow.DefaultHours) {

        hours = AnalyticsQueryWindow.ClampHours(hours);
        (string where, Dictionary<string, string>? parameters) = AnalyticsQueryWindow.Filter(hours, zone);

        List<Row> rows = await reader.QueryAsync<Row>($"""
            SELECT verdict, count() AS total
            FROM request_logs
            WHERE {where}
            GROUP BY verdict
            ORDER BY total DESC
            """, parameters, cancellationToken);

        return Results.Ok(new VerdictBreakdownResponse(
            hours,
            rows.Select(r => new VerdictCount(r.Verdict, r.Total)).ToList()));
    }
}

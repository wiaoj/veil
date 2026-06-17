using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Text.Json.Serialization;
using Veil.Analytics.ClickHouse;
using Wiaoj.Endpoints;
using IHttpResult = Microsoft.AspNetCore.Http.IResult;

namespace Veil.Analytics.Features.Queries;

public sealed record TopIpEntry(
    string ClientIp,
    long Total,
    long Blocked,
    long Challenged,
    long RateLimited,
    DateTimeOffset LastSeenUtc);

public sealed record TopIpsResponse(int WindowHours, List<TopIpEntry> Items);

public sealed class GetTopIpsEndpoint : IEndpoint {
    private const int DefaultLimit = 10;
    private const int MaxLimit = 100;

    public void Map(IEndpointRouteBuilder app) {
        app.MapGet("/v1/analytics/top-ips", Handle)
           .WithName("GetTopIps")
           .WithTags("Analytics")
           .WithSummary("Most active client IPs")
           .WithDescription("Client IPs by request count over the window, with per-verdict counts.")
           .Produces<TopIpsResponse>(StatusCodes.Status200OK);
    }

    private sealed record Row(
        [property: JsonPropertyName("client_ip")] string ClientIp,
        [property: JsonPropertyName("total")] long Total,
        [property: JsonPropertyName("blocked")] long Blocked,
        [property: JsonPropertyName("challenged")] long Challenged,
        [property: JsonPropertyName("rate_limited")] long RateLimited,
        [property: JsonPropertyName("last_seen_unix")] long LastSeenUnix);

    private static async Task<IHttpResult> Handle(
        ClickHouseReader reader,
        CancellationToken cancellationToken,
        string? zone = null,
        int hours = AnalyticsQueryWindow.DefaultHours,
        int limit = DefaultLimit) {

        hours = AnalyticsQueryWindow.ClampHours(hours);
        limit = Math.Clamp(limit, 1, MaxLimit);
        (string where, Dictionary<string, string>? parameters) = AnalyticsQueryWindow.Filter(hours, zone);

        List<Row> rows = await reader.QueryAsync<Row>($"""
            SELECT
                client_ip,
                count() AS total,
                countIf(verdict = 'block') AS blocked,
                countIf(verdict = 'challenge') AS challenged,
                countIf(verdict = 'rate_limited') AS rate_limited,
                toUnixTimestamp(max(ts)) AS last_seen_unix
            FROM request_logs
            WHERE {where}
            GROUP BY client_ip
            ORDER BY total DESC
            LIMIT {limit}
            """, parameters, cancellationToken);

        return Results.Ok(new TopIpsResponse(
            hours,
            rows.Select(r => new TopIpEntry(
                r.ClientIp, r.Total, r.Blocked, r.Challenged, r.RateLimited,
                DateTimeOffset.FromUnixTimeSeconds(r.LastSeenUnix))).ToList()));
    }
}
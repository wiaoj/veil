using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Veil.Shared;
using Veil.Zones.Domain.Enums;
using Veil.Zones.Domain.ValueObjects;
using Veil.Zones.Infrastructure.Persistence;
using Wiaoj.Endpoints;

namespace Veil.Zones.Features.Zones.ListZones;

/// <summary>
/// Compact zone summary used in list views.
/// </summary>
public sealed record ZoneSummaryResponse(
    string Id,
    string Hostname,
    string Status,
    int RuleCount);

/// <summary>
/// Paginated zone list.
/// </summary>
public sealed record ListZonesResponse(
    List<ZoneSummaryResponse> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed class ListZonesEndpoint : IEndpoint {
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    public void Map(IEndpointRouteBuilder app) {
        app.MapGet("/v1/zones", Handle)
           .WithName("ListZones")
           .WithTags("Zones")
           .WithSummary("Lists zones")
           .WithDescription("Returns a paginated list of zones ordered by creation (snowflake id).")
           .Produces<ListZonesResponse>(StatusCodes.Status200OK);
    }

    private static async Task<IHttpResult> Handle(
        IDbContextFactory<ZonesDbContext> dbFactory,
        IObfuscator<ZoneId> obfuscator,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = DefaultPageSize) {

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        await using ZonesDbContext dbContext = await dbFactory.CreateDbContextAsync(cancellationToken);

        int totalCount = await dbContext.Zones.CountAsync(cancellationToken);

        var zones = await dbContext.Zones
            .AsNoTracking()
            .OrderBy(z => z.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(z => new {
                z.Id,
                Hostname = z.Hostname.Value,
                z.Status,
                RuleCount = z.Rules.Count
            })
            .ToListAsync(cancellationToken);

        List<ZoneSummaryResponse> items = zones
            .Select(z => new ZoneSummaryResponse(
                obfuscator.Encode(z.Id),
                z.Hostname,
                z.Status.ToString(),
                z.RuleCount))
            .ToList();

        return Results.Ok(new ListZonesResponse(items, page, pageSize, totalCount));
    }
}

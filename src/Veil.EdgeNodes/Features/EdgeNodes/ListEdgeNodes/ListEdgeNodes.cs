using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Veil.EdgeNodes.Domain.ValueObjects;
using Veil.EdgeNodes.Infrastructure.Persistence;
using Veil.Shared;
using Wiaoj.Endpoints;

namespace Veil.EdgeNodes.Features.EdgeNodes.ListEdgeNodes;

/// <summary>
/// Edge node summary. The token hash is never exposed.
/// </summary>
public sealed record EdgeNodeSummaryResponse(
    string Id,
    string Name,
    string Address,
    string Status,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset? LastSeenAtUtc);

/// <summary>
/// Paginated edge node list.
/// </summary>
public sealed record ListEdgeNodesResponse(
    List<EdgeNodeSummaryResponse> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed class ListEdgeNodesEndpoint : IEndpoint {
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    public void Map(IEndpointRouteBuilder app) {
        app.MapGet("/v1/edge-nodes", Handle)
           .WithName("ListEdgeNodes")
           .WithTags("EdgeNodes")
           .WithSummary("Lists registered edge nodes")
           .WithDescription("Returns a paginated list of edge nodes ordered by registration (snowflake id).")
           .Produces<ListEdgeNodesResponse>(StatusCodes.Status200OK);
    }

    private static async Task<IHttpResult> Handle(
        IDbContextFactory<EdgeNodesDbContext> dbFactory,
        IObfuscator<EdgeNodeId> obfuscator,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = DefaultPageSize) {

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        await using EdgeNodesDbContext dbContext = await dbFactory.CreateDbContextAsync(cancellationToken);

        int totalCount = await dbContext.EdgeNodes.CountAsync(cancellationToken);

        var nodes = await dbContext.EdgeNodes
            .AsNoTracking()
            .OrderBy(n => n.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(n => new {
                n.Id,
                n.Name,
                n.Address,
                n.Status,
                n.RegisteredAtUtc,
                n.LastSeenAtUtc
            })
            .ToListAsync(cancellationToken);

        List<EdgeNodeSummaryResponse> items = nodes
            .Select(n => new EdgeNodeSummaryResponse(
                obfuscator.Encode(n.Id),
                n.Name,
                n.Address.ToString(),
                n.Status.ToString(),
                n.RegisteredAtUtc,
                n.LastSeenAtUtc))
            .ToList();

        return Results.Ok(new ListEdgeNodesResponse(items, page, pageSize, totalCount));
    }
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Veil.EdgeNodes.Domain;
using Veil.EdgeNodes.Domain.ValueObjects;
using Veil.EdgeNodes.Infrastructure.Persistence;
using Veil.Shared;
using Wiaoj.Endpoints;

namespace Veil.EdgeNodes.Features.EdgeNodes.GetConfigPushLog;

/// <summary>
/// One config push attempt as seen by the dashboard.
/// </summary>
public sealed record ConfigPushLogEntryResponse(
    bool Succeeded,
    string? Error,
    DateTimeOffset PushedAtUtc);

/// <summary>
/// Paginated push history of a single edge node, newest first.
/// </summary>
public sealed record GetConfigPushLogResponse(
    string NodeId,
    List<ConfigPushLogEntryResponse> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed class GetConfigPushLogEndpoint : IEndpoint {
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    public void Map(IEndpointRouteBuilder app) {
        app.MapGet("/v1/edge-nodes/{id}/push-log", Handle)
           .WithName("GetConfigPushLog")
           .WithTags("EdgeNodes")
           .WithSummary("Gets the config push history of an edge node")
           .WithDescription("Returns the node's config push attempts, newest first. Append-only log written by the ConfigSync worker.")
           .Produces<GetConfigPushLogResponse>(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IHttpResult> Handle(
        string id,
        IDbContextFactory<EdgeNodesDbContext> dbFactory,
        IObfuscator<EdgeNodeId> obfuscator,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = DefaultPageSize) {

        Result<EdgeNodeId> nodeId = obfuscator.Decode(id);
        if(nodeId.IsFailure) return nodeId.ToProblemDetails();

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        await using EdgeNodesDbContext dbContext = await dbFactory.CreateDbContextAsync(cancellationToken);

        bool nodeExists = await dbContext.EdgeNodes
            .AsNoTracking()
            .AnyAsync(n => n.Id.Equals(nodeId.Value), cancellationToken);

        if(!nodeExists) {
            Result<EdgeNode> notFound = EdgeNodeErrors.NotFound;
            return notFound.ToProblemDetails();
        }

        IQueryable<ConfigPushLog> logs = dbContext.ConfigPushLogs
            .AsNoTracking()
            .Where(l => l.EdgeNodeId.Equals(nodeId.Value));

        int totalCount = await logs.CountAsync(cancellationToken);

        List<ConfigPushLogEntryResponse> items = await logs
            .OrderByDescending(l => l.PushedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(l => new ConfigPushLogEntryResponse(l.Succeeded, l.Error, l.PushedAtUtc))
            .ToListAsync(cancellationToken);

        return Results.Ok(new GetConfigPushLogResponse(id, items, page, pageSize, totalCount));
    }
}

using Microsoft.EntityFrameworkCore;
using Veil.Analytics.Contracts;
using Veil.Analytics.Ingestion;
using Veil.EdgeNodes.Domain;
using Veil.EdgeNodes.Domain.Enums;
using Veil.EdgeNodes.Infrastructure.Persistence;
using Veil.Shared;
using Wiaoj.Primitives.Cryptography.Hashing;
using EdgeNodeId = Veil.EdgeNodes.Domain.ValueObjects.EdgeNodeId;
using IHttpResult = Microsoft.AspNetCore.Http.IResult;

namespace Veil.Analytics.Worker.Internal;

/// <summary>
/// Edge → control plane log ingestion. Lives in the host (not a module)
/// because it composes two modules: EdgeNodes authenticates the caller,
/// Analytics owns the queue — same split as EdgeConfigEndpoints in Veil.Api.
/// </summary>
public static class IngestEndpoints {
    public const string NodeTokenHeader = "X-Veil-Node-Token";

    /// <summary>
    /// Batches land every ~500ms per node; refreshing last-seen on each one
    /// would write the row twice a second, so refreshes are throttled.
    /// </summary>
    private static readonly TimeSpan MarkSeenInterval = TimeSpan.FromMinutes(1);

    public static void Map(WebApplication app) {
        app.MapPost("/ingest", Handle)
           .WithName("IngestRequestLogs")
           .ExcludeFromDescription();
    }

    private static async Task<IHttpResult> Handle(
        IngestRequest payload,
        HttpRequest request,
        IDbContextFactory<EdgeNodesDbContext> nodesDbFactory,
        IObfuscator<EdgeNodeId> nodeObfuscator,
        RequestLogQueue queue,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) {

        string? token = request.Headers[NodeTokenHeader].FirstOrDefault();
        if(string.IsNullOrEmpty(token))
            return Results.Unauthorized();

        if(string.IsNullOrEmpty(payload.NodeId) || !nodeObfuscator.TryDecode(payload.NodeId, out EdgeNodeId edgeNodeId))
            return Results.Unauthorized();

        await using EdgeNodesDbContext nodesDb = await nodesDbFactory.CreateDbContextAsync(cancellationToken);

        EdgeNode? node = await nodesDb.EdgeNodes
            .FirstOrDefaultAsync(n => n.Id.Equals(edgeNodeId), cancellationToken);

        string tokenHash = Sha256Hash.Compute(token).ToHexString().ToLower();
        if(node is null || !FixedTimeEquals(node.TokenHash, tokenHash))
            return Results.Unauthorized();

        if(node.Status is EdgeNodeStatus.Disabled)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        DateTimeOffset now = timeProvider.GetUtcNow();
        if(node.LastSeenAtUtc is null || now - node.LastSeenAtUtc >= MarkSeenInterval) {
            node.MarkSeen(now);
            await nodesDb.SaveChangesAsync(cancellationToken);
        }

        if(payload.Records is not { Count: > 0 })
            return Results.Accepted(value: new IngestResponse(0));

        List<RequestLogRow> rows = IngestNormalizer.Normalize(payload.NodeId, payload.Records, now);
        queue.Enqueue(rows);

        return Results.Accepted(value: new IngestResponse(rows.Count));
    }

    private static bool FixedTimeEquals(string left, string right) {
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(left),
            System.Text.Encoding.UTF8.GetBytes(right));
    }
}

public sealed record IngestResponse(int Accepted);

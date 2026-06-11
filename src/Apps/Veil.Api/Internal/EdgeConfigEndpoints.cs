using Microsoft.EntityFrameworkCore;
using Veil.EdgeNodes.Domain;
using Veil.EdgeNodes.Domain.Enums;
using Veil.EdgeNodes.Infrastructure.Persistence;
using Veil.Shared;
using Veil.Zones.Domain;
using Veil.Zones.EdgeConfig;
using Veil.Zones.Infrastructure.Persistence;
using Wiaoj.Primitives.Cryptography.Hashing;
using EdgeNodeId = Veil.EdgeNodes.Domain.ValueObjects.EdgeNodeId;
using IHttpResult = Microsoft.AspNetCore.Http.IResult;
using RuleId = Veil.Zones.Domain.ValueObjects.RuleId;

namespace Veil.Api.Internal;

/// <summary>
/// Internal control-plane → edge endpoints. These live in the host (not a
/// module) because they compose two modules: EdgeNodes authenticates the
/// caller, Zones provides the config snapshot.
/// </summary>
public static class EdgeConfigEndpoints {
    public const string NodeTokenHeader = "X-Veil-Node-Token";

    public static void Map(WebApplication app) {
        app.MapGet("/internal/config/{nodeId}", Handle)
           .WithName("GetEdgeConfigSnapshot")
           .ExcludeFromDescription();
    }

    private static async Task<IHttpResult> Handle(
        string nodeId,
        HttpRequest request,
        IDbContextFactory<EdgeNodesDbContext> nodesDbFactory,
        IDbContextFactory<ZonesDbContext> zonesDbFactory,
        IObfuscator<EdgeNodeId> nodeObfuscator,
        IObfuscator<RuleId> ruleObfuscator,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) {

        string? token = request.Headers[NodeTokenHeader].FirstOrDefault();
        if(string.IsNullOrEmpty(token))
            return Results.Unauthorized();

        if(!nodeObfuscator.TryDecode(nodeId, out EdgeNodeId edgeNodeId))
            return Results.Unauthorized();

        await using EdgeNodesDbContext nodesDb = await nodesDbFactory.CreateDbContextAsync(cancellationToken);

        EdgeNode? node = await nodesDb.EdgeNodes
            .FirstOrDefaultAsync(n => n.Id.Equals(edgeNodeId), cancellationToken);

        string tokenHash = Sha256Hash.Compute(token).ToHexString().ToLower();
        if(node is null || !FixedTimeEquals(node.TokenHash, tokenHash))
            return Results.Unauthorized();

        if(node.Status is EdgeNodeStatus.Disabled)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        node.MarkSeen(timeProvider.GetUtcNow());
        await nodesDb.SaveChangesAsync(cancellationToken);

        await using ZonesDbContext zonesDb = await zonesDbFactory.CreateDbContextAsync(cancellationToken);
        List<Zone> zones = await zonesDb.Zones
            .AsNoTracking()
            .Include(z => z.Rules)
            .ToListAsync(cancellationToken);

        EdgeConfigSnapshot snapshot = EdgeConfigSnapshotBuilder.Build(zones, ruleObfuscator);
        return Results.Ok(snapshot);
    }

    private static bool FixedTimeEquals(string left, string right) {
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(left),
            System.Text.Encoding.UTF8.GetBytes(right));
    }
}

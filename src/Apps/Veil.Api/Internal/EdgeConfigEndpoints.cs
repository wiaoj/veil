using Microsoft.EntityFrameworkCore;
using Veil.EdgeNodes.Contracts;
using Veil.Zones.Domain;
using Veil.Zones.EdgeConfig;
using Veil.Zones.Infrastructure.Persistence;
using Veil.Shared;
using IHttpResult = Microsoft.AspNetCore.Http.IResult;
using RuleId = Veil.Zones.Domain.ValueObjects.RuleId;

namespace Veil.Api.Internal;

/// <summary>
/// Internal control-plane → edge endpoints. These live in the host (not a
/// module) because they compose two modules: EdgeNodes authenticates the
/// caller via <see cref="IEdgeNodeTokenVerifier"/>, Zones provides the
/// config snapshot.
/// </summary>
public static class EdgeConfigEndpoints {
    public const string NodeTokenHeader = "X-Veil-Node-Token";

    public static void Map(WebApplication app) {
        app.MapGet("/internal/config/{nodeId}", Handle)
           .WithName("GetEdgeConfigSnapshot")
           // Authenticates with the node token, not a user session — must
           // bypass the fallback authorization policy.
           .AllowAnonymous()
           .ExcludeFromDescription();
    }

    private static async Task<IHttpResult> Handle(
        string nodeId,
        HttpRequest request,
        IEdgeNodeTokenVerifier tokenVerifier,
        IDbContextFactory<ZonesDbContext> zonesDbFactory,
        IObfuscator<RuleId> ruleObfuscator,
        ConfigSync.ZoneCertificateProvider certificateProvider,
        CancellationToken cancellationToken) {

        string? token = request.Headers[NodeTokenHeader].FirstOrDefault();
        if(string.IsNullOrEmpty(token))
            return Results.Unauthorized();

        switch(await tokenVerifier.VerifyAsync(nodeId, token, cancellationToken)) {
            case EdgeNodeTokenVerdict.Invalid:
                return Results.Unauthorized();
            case EdgeNodeTokenVerdict.Disabled:
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        await using ZonesDbContext zonesDb = await zonesDbFactory.CreateDbContextAsync(cancellationToken);
        List<Zone> zones = await zonesDb.Zones
            .AsNoTracking()
            .Include(z => z.Rules)
            .ToListAsync(cancellationToken);

        EdgeConfigSnapshot snapshot = EdgeConfigSnapshotBuilder.Build(zones, ruleObfuscator,
            await certificateProvider.GetActiveCertificatesAsync(cancellationToken));
        return Results.Ok(snapshot);
    }
}

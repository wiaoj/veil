using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Veil.Shared;
using Veil.Zones.Domain;
using Veil.Zones.Domain.ValueObjects;
using Veil.Zones.Infrastructure.Persistence;
using Wiaoj.Endpoints;

namespace Veil.Zones.Features.Zones.UpdateCache;

/// <summary>Toggle for the zone's opt-in edge response cache.</summary>
public sealed record CacheConfigRequest(bool Enabled);

public sealed record CacheConfigResponse(bool Enabled);

public sealed class UpdateZoneCacheEndpoint : IEndpoint {
    public void Map(IEndpointRouteBuilder app) {
        app.MapPut("/v1/zones/{id}/cache", Handle)
           .WithName("UpdateZoneCache")
           .WithTags("Zones")
           .WithSummary("Enables or disables the zone's response cache")
           .WithDescription("Opt-in, conservative edge response caching (RFC 7234 subset): only explicitly-cacheable GET responses are stored.")
           .Produces<CacheConfigResponse>(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IHttpResult> Handle(
        string id,
        CacheConfigRequest req,
        IDbContextFactory<ZonesDbContext> dbFactory,
        IObfuscator<ZoneId> obfuscator,
        CancellationToken cancellationToken) {

        Result<ZoneId> zoneId = obfuscator.Decode(id);
        if(zoneId.IsFailure) return zoneId.ToProblemDetails();

        await using ZonesDbContext dbContext = await dbFactory.CreateDbContextAsync(cancellationToken);

        Zone? zone = await dbContext.Zones
            .FirstOrDefaultAsync(z => z.Id.Equals(zoneId.Value), cancellationToken);

        if(zone is null) {
            Result<Zone> notFound = ZoneErrors.NotFound;
            return notFound.ToProblemDetails();
        }

        Result<Success> update = zone.UpdateCache(req.Enabled);
        if(update.IsFailure) return update.ToProblemDetails();

        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Ok(new CacheConfigResponse(zone.CacheEnabled));
    }
}

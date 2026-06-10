using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Veil.Shared;
using Veil.Zones.Domain;
using Veil.Zones.Domain.ValueObjects;
using Veil.Zones.Infrastructure.Persistence;
using Wiaoj.Endpoints;

namespace Veil.Zones.Features.Zones.PauseZone;

public sealed class PauseZoneEndpoint : IEndpoint {
    public void Map(IEndpointRouteBuilder app) {
        app.MapPost("/v1/zones/{id}/pause", Handle)
           .WithName("PauseZone")
           .WithTags("Zones")
           .WithSummary("Pauses a zone")
           .WithDescription("Paused zones stop enforcing rules at the edge; traffic passes through to the upstream unfiltered.")
           .Produces<ZoneStatusResponse>(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IHttpResult> Handle(
        string id,
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

        Result<Success> result = zone.Pause();
        if(result.IsFailure) return result.ToProblemDetails();

        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Ok(new ZoneStatusResponse(obfuscator.EncodeId(zone), zone.Status.ToString()));
    }
}

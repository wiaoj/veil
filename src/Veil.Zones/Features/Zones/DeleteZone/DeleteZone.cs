using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Veil.Shared;
using Veil.Zones.Domain;
using Veil.Zones.Domain.ValueObjects;
using Veil.Zones.Infrastructure.Persistence;
using Wiaoj.Endpoints;

namespace Veil.Zones.Features.Zones.DeleteZone;

public sealed class DeleteZoneEndpoint : IEndpoint {
    public void Map(IEndpointRouteBuilder app) {
        app.MapDelete("/v1/zones/{id}", Handle)
           .WithName("DeleteZone")
           .WithTags("Zones")
           .WithSummary("Deletes a zone")
           .WithDescription("Permanently removes the zone and its rules. Edge nodes drop it on their next config sync.")
           .Produces(StatusCodes.Status204NoContent)
           .ProducesProblem(StatusCodes.Status400BadRequest)
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
            .Include(z => z.Rules)
            .FirstOrDefaultAsync(z => z.Id.Equals(zoneId.Value), cancellationToken);

        if(zone is null) {
            Result<Zone> notFound = ZoneErrors.NotFound;
            return notFound.ToProblemDetails();
        }

        // Raise the deletion event before removing the aggregate so the outbox
        // captures it in the same transaction; the config-sync loop then drops
        // the zone from every node's snapshot.
        zone.MarkDeleted();

        dbContext.Zones.Remove(zone);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.NoContent();
    }
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Veil.Shared;
using Veil.Zones.Domain;
using Veil.Zones.Domain.ValueObjects;
using Veil.Zones.Infrastructure.Persistence;
using Wiaoj.Endpoints;

namespace Veil.Zones.Features.Zones.ActivateZone;

public sealed class ActivateZoneEndpoint : IEndpoint {
    public void Map(IEndpointRouteBuilder app) {
        app.MapPost("/v1/zones/{id}/activate", Handle)
           .WithName("ActivateZone")
           .WithTags("Zones")
           .WithSummary("Activates a provisioning zone")
           .WithDescription("Forces a zone Active without waiting for a certificate. Use for HTTP-only zones that never trigger ACME issuance.")
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

        Result<Success> result = zone.Activate();
        if(result.IsFailure) return result.ToProblemDetails();

        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Ok(new ZoneStatusResponse(obfuscator.EncodeId(zone), zone.Status.ToString()));
    }
}

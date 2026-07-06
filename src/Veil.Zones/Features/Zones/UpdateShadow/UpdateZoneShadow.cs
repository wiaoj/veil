using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Veil.Shared;
using Veil.Zones.Domain;
using Veil.Zones.Domain.ValueObjects;
using Veil.Zones.Infrastructure.Persistence;
using Wiaoj.Endpoints;

namespace Veil.Zones.Features.Zones.UpdateShadow;

/// <summary>Toggle for a zone's shadow (dry-run) mode.</summary>
public sealed record ShadowRequest(bool Enabled);

public sealed record ShadowResponse(bool Enabled);

public sealed class UpdateZoneShadowEndpoint : IEndpoint {
    public void Map(IEndpointRouteBuilder app) {
        app.MapPut("/v1/zones/{id}/shadow", Handle)
           .WithName("UpdateZoneShadow")
           .WithTags("Zones")
           .WithSummary("Enables or disables the zone's shadow (dry-run) mode")
           .WithDescription("In shadow mode rules and managed signatures are evaluated and logged, but nothing is enforced — every request is forwarded. Useful for validating a rule set against live traffic.")
           .Produces<ShadowResponse>(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IHttpResult> Handle(
        string id,
        ShadowRequest req,
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

        Result<Success> update = zone.UpdateShadow(req.Enabled);
        if(update.IsFailure) return update.ToProblemDetails();

        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Ok(new ShadowResponse(zone.Shadow));
    }
}

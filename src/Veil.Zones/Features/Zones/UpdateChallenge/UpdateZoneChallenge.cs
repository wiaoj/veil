using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Veil.Shared;
using Veil.Zones.Domain;
using Veil.Zones.Domain.ValueObjects;
using Veil.Zones.Infrastructure.Persistence;
using Wiaoj.Endpoints;

namespace Veil.Zones.Features.Zones.UpdateChallenge;

public sealed class UpdateZoneChallengeEndpoint : IEndpoint {
    public void Map(IEndpointRouteBuilder app) {
        app.MapPut("/v1/zones/{id}/challenge", Handle)
           .WithName("UpdateZoneChallenge")
           .WithTags("Zones")
           .WithSummary("Replaces a zone's challenge configuration")
           .WithDescription("Updates PoW difficulty, token TTL and CAPTCHA fallback. Set Enabled=false to disable challenges for the zone.")
           .Produces<ChallengeConfigDto>(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status400BadRequest)
           .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IHttpResult> Handle(
        string id,
        ChallengeConfigDto req,
        IDbContextFactory<ZonesDbContext> dbFactory,
        IObfuscator<ZoneId> obfuscator,
        CancellationToken cancellationToken) {

        Result<ZoneId> zoneId = obfuscator.Decode(id);
        if(zoneId.IsFailure) return zoneId.ToProblemDetails();

        Result<ChallengeConfig> challenge = req.ToDomain();
        if(challenge.IsFailure) return challenge.ToProblemDetails();

        await using ZonesDbContext dbContext = await dbFactory.CreateDbContextAsync(cancellationToken);

        Zone? zone = await dbContext.Zones
            .FirstOrDefaultAsync(z => z.Id.Equals(zoneId.Value), cancellationToken);

        if(zone is null) {
            Result<Zone> notFound = ZoneErrors.NotFound;
            return notFound.ToProblemDetails();
        }

        Result<Success> update = zone.UpdateChallenge(challenge.Value);
        if(update.IsFailure) return update.ToProblemDetails();

        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Ok(zone.Challenge.ToDto());
    }
}

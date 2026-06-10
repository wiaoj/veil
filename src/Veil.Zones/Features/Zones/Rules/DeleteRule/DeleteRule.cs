using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Veil.Shared;
using Veil.Zones.Domain;
using Veil.Zones.Domain.ValueObjects;
using Veil.Zones.Infrastructure.Persistence;
using Wiaoj.Endpoints;

namespace Veil.Zones.Features.Zones.Rules.DeleteRule;

public sealed class DeleteRuleEndpoint : IEndpoint {
    public void Map(IEndpointRouteBuilder app) {
        app.MapDelete("/v1/zones/{id}/rules/{ruleId}", Handle)
           .WithName("DeleteRule")
           .WithTags("Rules")
           .WithSummary("Deletes a rule from a zone")
           .WithDescription("Removes the rule from the zone's rule set. Deleting an already-removed rule is a no-op (idempotent).")
           .Produces(StatusCodes.Status204NoContent)
           .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IHttpResult> Handle(
        string id,
        string ruleId,
        IDbContextFactory<ZonesDbContext> dbFactory,
        IObfuscator<ZoneId> zoneObfuscator,
        IObfuscator<RuleId> ruleObfuscator,
        CancellationToken cancellationToken) {

        Result<ZoneId> zoneIdResult = zoneObfuscator.Decode(id);
        if(zoneIdResult.IsFailure) return zoneIdResult.ToProblemDetails();

        Result<RuleId> ruleIdResult = ruleObfuscator.Decode(ruleId);
        if(ruleIdResult.IsFailure) return ruleIdResult.ToProblemDetails();

        await using ZonesDbContext dbContext = await dbFactory.CreateDbContextAsync(cancellationToken);

        Zone? zone = await dbContext.Zones
            .Include(z => z.Rules)
            .FirstOrDefaultAsync(z => z.Id.Equals(zoneIdResult.Value), cancellationToken);

        if(zone is null) {
            Result<Zone> notFound = ZoneErrors.NotFound;
            return notFound.ToProblemDetails();
        }

        Result<Success> result = zone.RemoveRule(ruleIdResult.Value);
        if(result.IsFailure) return result.ToProblemDetails();

        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.NoContent();
    }
}

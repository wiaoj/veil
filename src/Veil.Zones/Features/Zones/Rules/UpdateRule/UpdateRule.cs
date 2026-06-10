using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Veil.Shared;
using Veil.Zones.Domain;
using Veil.Zones.Domain.ValueObjects;
using Veil.Zones.Infrastructure.Persistence;
using Wiaoj.Endpoints;

namespace Veil.Zones.Features.Zones.Rules.UpdateRule;

/// <summary>
/// Partial rule update. Omitted (null) fields are left unchanged.
/// </summary>
/// <param name="Priority">New evaluation priority, if changing.</param>
/// <param name="IsEnabled">Enable or disable the rule, if changing.</param>
public sealed record UpdateRuleRequest(int? Priority = null, bool? IsEnabled = null);

public sealed class UpdateRuleEndpoint : IEndpoint {
    public void Map(IEndpointRouteBuilder app) {
        app.MapPatch("/v1/zones/{id}/rules/{ruleId}", Handle)
           .WithName("UpdateRule")
           .WithTags("Rules")
           .WithSummary("Updates a rule's priority or enabled state")
           .WithDescription("Partially updates a rule. Only the provided fields are changed; the rule set is re-sorted by priority.")
           .Produces<RuleResponse>(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status400BadRequest)
           .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IHttpResult> Handle(
        string id,
        string ruleId,
        UpdateRuleRequest req,
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

        Result<Rule> rule = zone.UpdateRule(ruleIdResult.Value, req.Priority, req.IsEnabled);
        if(rule.IsFailure) return rule.ToProblemDetails();

        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Ok(rule.Value.ToResponse(ruleObfuscator));
    }
}

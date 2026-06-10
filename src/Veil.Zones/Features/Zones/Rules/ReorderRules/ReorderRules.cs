using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Veil.Shared;
using Veil.Zones.Domain;
using Veil.Zones.Domain.ValueObjects;
using Veil.Zones.Infrastructure.Persistence;
using Wiaoj.Endpoints;

namespace Veil.Zones.Features.Zones.Rules.ReorderRules;

/// <summary>
/// The desired rule order. Must contain every rule id of the zone exactly once.
/// </summary>
/// <param name="RuleIds">Rule ids in the desired evaluation order (first = highest priority).</param>
public sealed record ReorderRulesRequest(List<string> RuleIds);

public sealed class ReorderRulesEndpoint : IEndpoint {
    public void Map(IEndpointRouteBuilder app) {
        app.MapPut("/v1/zones/{id}/rules/order", Handle)
           .WithName("ReorderRules")
           .WithTags("Rules")
           .WithSummary("Reorders a zone's rules")
           .WithDescription("Replaces rule ordering wholesale. Priorities are reassigned in steps of 10 following the given order.")
           .Produces<List<RuleResponse>>(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status400BadRequest)
           .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IHttpResult> Handle(
        string id,
        ReorderRulesRequest req,
        IDbContextFactory<ZonesDbContext> dbFactory,
        IObfuscator<ZoneId> zoneObfuscator,
        IObfuscator<RuleId> ruleObfuscator,
        CancellationToken cancellationToken) {

        Result<ZoneId> zoneId = zoneObfuscator.Decode(id);
        if(zoneId.IsFailure) return zoneId.ToProblemDetails();

        List<RuleId> orderedIds = new(req.RuleIds.Count);
        foreach(string rawRuleId in req.RuleIds) {
            Result<RuleId> ruleId = ruleObfuscator.Decode(rawRuleId);
            if(ruleId.IsFailure) return ruleId.ToProblemDetails();
            orderedIds.Add(ruleId.Value);
        }

        await using ZonesDbContext dbContext = await dbFactory.CreateDbContextAsync(cancellationToken);

        Zone? zone = await dbContext.Zones
            .Include(z => z.Rules)
            .FirstOrDefaultAsync(z => z.Id.Equals(zoneId.Value), cancellationToken);

        if(zone is null) {
            Result<Zone> notFound = ZoneErrors.NotFound;
            return notFound.ToProblemDetails();
        }

        Result<Success> result = zone.ReorderRules(orderedIds);
        if(result.IsFailure) return result.ToProblemDetails();

        await dbContext.SaveChangesAsync(cancellationToken);

        List<RuleResponse> response = zone.Rules
            .Select(r => r.ToResponse(ruleObfuscator))
            .ToList();

        return Results.Ok(response);
    }
}

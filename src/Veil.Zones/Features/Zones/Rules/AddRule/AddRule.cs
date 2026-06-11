using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Veil.Shared;
using Veil.Zones.Domain;
using Veil.Zones.Domain.Enums;
using Veil.Zones.Domain.ValueObjects;
using Veil.Zones.Infrastructure.Persistence;
using Wiaoj.Endpoints;

namespace Veil.Zones.Features.Zones.Rules.AddRule;

/// <summary>
/// The request payload for adding a rule to a zone.
/// </summary>
/// <param name="Name">Human-readable rule name.</param>
/// <param name="Priority">Evaluation priority — lower numbers are evaluated first.</param>
/// <param name="Action">Verdict produced when all conditions match.</param>
/// <param name="Conditions">Conditions that must all match (AND).</param>
/// <param name="RateLimit">Rate limit parameters — required when Action is RateLimit, forbidden otherwise.</param>
public sealed record AddRuleRequest(
    string Name,
    int Priority,
    RuleAction Action,
    List<RuleConditionRequest> Conditions,
    RateLimitRequest? RateLimit = null);

public sealed class AddRuleEndpoint : IEndpoint {
    public void Map(IEndpointRouteBuilder app) {
        app.MapPost("/v1/zones/{id}/rules", Handle)
           .WithName("AddRule")
           .WithTags("Rules")
           .WithSummary("Adds a rule to a zone")
           .WithDescription("Appends a new rule to the zone's rule set. Rules are kept sorted by priority and pushed to edge nodes via config sync.")
           .Produces<RuleResponse>(StatusCodes.Status201Created)
           .ProducesProblem(StatusCodes.Status400BadRequest)
           .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IHttpResult> Handle(
        string id,
        AddRuleRequest req,
        IDbContextFactory<ZonesDbContext> dbFactory,
        IObfuscator<ZoneId> zoneObfuscator,
        IObfuscator<RuleId> ruleObfuscator,
        CancellationToken cancellationToken) {

        Result<ZoneId> zoneId = zoneObfuscator.Decode(id);
        if(zoneId.IsFailure) return zoneId.ToProblemDetails();

        Result<List<RuleCondition>> conditions = req.Conditions.ToDomain();
        if(conditions.IsFailure) return conditions.ToProblemDetails();

        RateLimitConfig? rateLimit = null;
        if(req.RateLimit is not null) {
            Result<RateLimitConfig> rateLimitResult =
                RateLimitConfig.Create(req.RateLimit.Requests, req.RateLimit.WindowSecs);
            if(rateLimitResult.IsFailure) return rateLimitResult.ToProblemDetails();
            rateLimit = rateLimitResult.Value;
        }

        await using ZonesDbContext dbContext = await dbFactory.CreateDbContextAsync(cancellationToken);

        Zone? zone = await dbContext.Zones
            .Include(z => z.Rules)
            .FirstOrDefaultAsync(z => z.Id.Equals(zoneId.Value), cancellationToken);

        if(zone is null) {
            Result<Zone> notFound = ZoneErrors.NotFound;
            return notFound.ToProblemDetails();
        }

        Result<Rule> rule = zone.AddRule(req.Name, req.Priority, req.Action, conditions.Value, rateLimit);
        if(rule.IsFailure) return rule.ToProblemDetails();

        await dbContext.SaveChangesAsync(cancellationToken);

        RuleResponse response = rule.Value.ToResponse(ruleObfuscator);
        return Results.Created($"/v1/zones/{id}/rules/{response.Id}", response);
    }
}

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

namespace Veil.Zones.Features.Zones.UpdateManagedRules;

/// <summary>Managed WAF signature toggles for a zone.</summary>
public sealed record ManagedRulesRequest(
    bool SqlInjection,
    bool Xss,
    bool PathTraversal,
    bool InspectBody,
    string Action = "block");

public sealed record ManagedRulesResponse(
    bool SqlInjection,
    bool Xss,
    bool PathTraversal,
    bool InspectBody,
    string Action);

public sealed class UpdateZoneManagedRulesEndpoint : IEndpoint {
    public void Map(IEndpointRouteBuilder app) {
        app.MapPut("/v1/zones/{id}/managed-rules", Handle)
           .WithName("UpdateZoneManagedRules")
           .WithTags("Zones")
           .WithSummary("Replaces a zone's managed WAF signature configuration")
           .WithDescription("Toggles the SQLi / XSS / path-traversal signature families, body inspection, and the match action (block or challenge).")
           .Produces<ManagedRulesResponse>(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status400BadRequest)
           .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IHttpResult> Handle(
        string id,
        ManagedRulesRequest req,
        IDbContextFactory<ZonesDbContext> dbFactory,
        IObfuscator<ZoneId> obfuscator,
        CancellationToken cancellationToken) {

        Result<ZoneId> zoneId = obfuscator.Decode(id);
        if(zoneId.IsFailure) return zoneId.ToProblemDetails();

        ManagedRuleAction action = req.Action?.ToLowerInvariant() switch {
            "challenge" => ManagedRuleAction.Challenge,
            _ => ManagedRuleAction.Block
        };
        ManagedRulesConfig managed = ManagedRulesConfig.Create(
            req.SqlInjection, req.Xss, req.PathTraversal, req.InspectBody, action);

        await using ZonesDbContext dbContext = await dbFactory.CreateDbContextAsync(cancellationToken);

        Zone? zone = await dbContext.Zones
            .FirstOrDefaultAsync(z => z.Id.Equals(zoneId.Value), cancellationToken);

        if(zone is null) {
            Result<Zone> notFound = ZoneErrors.NotFound;
            return notFound.ToProblemDetails();
        }

        Result<Success> update = zone.UpdateManagedRules(managed);
        if(update.IsFailure) return update.ToProblemDetails();

        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Ok(new ManagedRulesResponse(
            managed.SqlInjection, managed.Xss, managed.PathTraversal, managed.InspectBody,
            managed.Action == ManagedRuleAction.Challenge ? "challenge" : "block"));
    }
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Veil.Shared;
using Veil.Zones.Domain;
using Veil.Zones.Domain.ValueObjects;
using Veil.Zones.Infrastructure.Persistence;
using Wiaoj.Endpoints;

namespace Veil.Zones.Features.Zones.Rules.GetZoneRules;

public sealed class GetZoneRulesEndpoint : IEndpoint {
    public void Map(IEndpointRouteBuilder app) {
        app.MapGet("/v1/zones/{id}/rules", Handle)
           .WithName("GetZoneRules")
           .WithTags("Rules")
           .WithSummary("Lists a zone's rules")
           .WithDescription("Returns the zone's rules in evaluation order (ascending priority).")
           .Produces<List<RuleResponse>>(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IHttpResult> Handle(
        string id,
        IDbContextFactory<ZonesDbContext> dbFactory,
        IObfuscator<ZoneId> zoneObfuscator,
        IObfuscator<RuleId> ruleObfuscator,
        CancellationToken cancellationToken) {

        Result<ZoneId> zoneId = zoneObfuscator.Decode(id);
        if(zoneId.IsFailure) return zoneId.ToProblemDetails();

        await using ZonesDbContext dbContext = await dbFactory.CreateDbContextAsync(cancellationToken);

        Zone? zone = await dbContext.Zones
            .AsNoTracking()
            .Include(z => z.Rules)
            .FirstOrDefaultAsync(z => z.Id.Equals(zoneId.Value), cancellationToken);

        if(zone is null) {
            Result<Zone> notFound = ZoneErrors.NotFound;
            return notFound.ToProblemDetails();
        }

        List<RuleResponse> response = zone.Rules
            .OrderBy(r => r.Priority)
            .Select(r => r.ToResponse(ruleObfuscator))
            .ToList();

        return Results.Ok(response);
    }
}

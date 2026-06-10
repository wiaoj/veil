using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Veil.Shared;
using Veil.Zones.Domain;
using Veil.Zones.Domain.ValueObjects;
using Veil.Zones.Infrastructure.Persistence;
using Wiaoj.Endpoints;

namespace Veil.Zones.Features.Zones.GetZone;

/// <summary>
/// Full detail view of a zone, including its rules in evaluation order.
/// </summary>
public sealed record GetZoneResponse(
    string Id,
    string Hostname,
    string Status,
    UpstreamConfigDto Upstream,
    ChallengeConfigDto Challenge,
    List<RuleResponse> Rules);

public sealed class GetZoneEndpoint : IEndpoint {
    public void Map(IEndpointRouteBuilder app) {
        app.MapGet("/v1/zones/{id}", Handle)
           .WithName("GetZone")
           .WithTags("Zones")
           .WithSummary("Gets a zone by id")
           .WithDescription("Returns the full zone configuration including upstream, challenge settings and rules in evaluation order.")
           .Produces<GetZoneResponse>(StatusCodes.Status200OK)
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

        var response = new GetZoneResponse(
            zoneObfuscator.EncodeId(zone),
            zone.Hostname.Value,
            zone.Status.ToString(),
            zone.Upstream.ToDto(),
            zone.Challenge.ToDto(),
            zone.Rules.Select(r => r.ToResponse(ruleObfuscator)).ToList());

        return Results.Ok(response);
    }
}

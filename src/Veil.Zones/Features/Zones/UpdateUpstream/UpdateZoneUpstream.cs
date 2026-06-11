using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Veil.Shared;
using Veil.Zones.Domain;
using Veil.Zones.Domain.ValueObjects;
using Veil.Zones.Infrastructure.Persistence;
using Wiaoj.Endpoints;

namespace Veil.Zones.Features.Zones.UpdateUpstream;

public sealed class UpdateZoneUpstreamEndpoint : IEndpoint {
    public void Map(IEndpointRouteBuilder app) {
        app.MapPut("/v1/zones/{id}/upstream", Handle)
           .WithName("UpdateZoneUpstream")
           .WithTags("Zones")
           .WithSummary("Replaces a zone's upstream configuration")
           .WithDescription("Replaces the upstream targets, load balancing strategy and timeouts. The change is pushed to edge nodes via config sync.")
           .Produces<UpstreamConfigResponse>(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status400BadRequest)
           .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IHttpResult> Handle(
        string id,
        UpstreamConfigRequest req,
        IDbContextFactory<ZonesDbContext> dbFactory,
        IObfuscator<ZoneId> obfuscator,
        CancellationToken cancellationToken) {

        Result<ZoneId> zoneId = obfuscator.Decode(id);
        if(zoneId.IsFailure) return zoneId.ToProblemDetails();

        Result<UpstreamConfig> upstream = req.ToDomain();
        if(upstream.IsFailure) return upstream.ToProblemDetails();

        await using ZonesDbContext dbContext = await dbFactory.CreateDbContextAsync(cancellationToken);

        Zone? zone = await dbContext.Zones
            .FirstOrDefaultAsync(z => z.Id.Equals(zoneId.Value), cancellationToken);

        if(zone is null) {
            Result<Zone> notFound = ZoneErrors.NotFound;
            return notFound.ToProblemDetails();
        }

        Result<Success> update = zone.UpdateUpstream(upstream.Value);
        if(update.IsFailure) return update.ToProblemDetails();

        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Ok(zone.Upstream.ToResponse());
    }
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Veil.Shared;
using Veil.Zones.Domain;
using Veil.Zones.Domain.ValueObjects;
using Veil.Zones.Infrastructure.Persistence;
using Wiaoj.Endpoints;

namespace Veil.Zones.Features.Zones.CreateZone;

/// <summary>
/// The request payload for creating a new zone.
/// </summary>
/// <param name="Hostname">The fully qualified domain name to route.</param>
/// <param name="Upstream">The upstream configuration.</param>
/// <param name="Challenge">Optional challenge configuration.</param>
public sealed record CreateZoneRequest(
    string Hostname,
    UpstreamConfigRequest Upstream,
    ChallengeConfigRequest? Challenge);

/// <summary>
/// The response payload returned upon successful zone creation.
/// </summary>
/// <param name="Id">The unique identifier of the created zone.</param>
/// <param name="Hostname">The configured hostname for the zone.</param>
/// <param name="Status">The current status of the zone.</param>
public sealed record CreateZoneResponse(
    string Id,
    string Hostname,
    string Status);

public sealed class CreateZoneEndpoint : IEndpoint {
    public void Map(IEndpointRouteBuilder app) {
        app.MapPost("/v1/zones", Handle)
           .WithName("CreateZone")
           .WithTags("Zones")
           .WithSummary("Creates a new proxy zone")
           .WithDescription("Provisions a new Edge proxy zone with the specified upstream routing and challenge configuration.")
           .Produces<CreateZoneResponse>(StatusCodes.Status201Created)
           .ProducesProblem(StatusCodes.Status400BadRequest)
           // .RequireAuthorization() // TODO: Enable when auth module is ready
           ;
    }

    private static async Task<IHttpResult> Handle(
        CreateZoneRequest req,
        IDbContextFactory<ZonesDbContext> dbFactory,
        IObfuscator<ZoneId> obfuscator,
        CancellationToken cancellationToken) {

        await using ZonesDbContext dbContext = await dbFactory.CreateDbContextAsync(cancellationToken);

        // 1. Hostname validation
        Result<Hostname> hostnameResult = Hostname.Create(req.Hostname);
        if(hostnameResult.IsFailure) return hostnameResult.ToProblemDetails();

        // 2. Upstream validation (incl. URL parsing)
        Result<UpstreamConfig> upstreamResult = req.Upstream.ToDomain();
        if(upstreamResult.IsFailure) return upstreamResult.ToProblemDetails();

        // 3. Challenge validation (optional)
        ChallengeConfig? challenge = null;
        if(req.Challenge is not null) {
            Result<ChallengeConfig> challengeResult = req.Challenge.ToDomain();
            if(challengeResult.IsFailure) return challengeResult.ToProblemDetails();
            challenge = challengeResult.Value;
        }

        // 4. Create Zone Aggregate
        Result<Zone> zoneResult = Zone.Create(hostnameResult.Value, upstreamResult.Value, challenge);

        if(zoneResult.IsFailure)
            return zoneResult.ToProblemDetails();

        Result<Zone> zone = await zoneResult.ThenAsync(async (zone, ct) => {
            await dbContext.Zones.AddAsync(zone, ct);
            await dbContext.SaveChangesAsync(ct);
            return zone;
        }, cancellationToken);

        if(zone.IsFailure)
            return zone.ToProblemDetails();

        ObfuscatedId encodedId = obfuscator.EncodeId(zone.Value);
        var response = new CreateZoneResponse(
            encodedId,
            zone.Value.Hostname.Value,
            zone.Value.Status.ToString());

        return Results.Created($"/v1/zones/{encodedId}", response);
    }
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Veil.Shared;
using Veil.Zones.Domain;
using Veil.Zones.Domain.ValueObjects;
using Veil.Zones.Infrastructure.Persistence;
using Wiaoj.Endpoints;

namespace Veil.Zones.Features.Zones.UpdateWidget;

/// <summary>The site key + secret returned once when keys are (re)generated.
/// The secret is shown here and never again — the API only stores it.</summary>
public sealed record WidgetRotateResponse(string SiteKey, string Secret);

/// <summary>
/// Manages a zone's embeddable, self-hosted bot-verification widget: an
/// enable/theme update, and a key-rotation endpoint that mints a fresh
/// sitekey + secret (the secret is revealed only in that response).
/// </summary>
public sealed class WidgetEndpoints : IEndpoint {
    public void Map(IEndpointRouteBuilder app) {
        app.MapPut("/v1/zones/{id}/widget", UpdateWidget)
           .WithName("UpdateZoneWidget")
           .WithTags("Zones")
           .WithSummary("Enables/disables the zone's bot-verification widget and sets its theme")
           .Produces<WidgetConfigResponse>(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status400BadRequest)
           .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapPost("/v1/zones/{id}/widget/rotate", RotateWidgetKeys)
           .WithName("RotateZoneWidgetKeys")
           .WithTags("Zones")
           .WithSummary("Generates a fresh site key + secret for the widget")
           .WithDescription("The secret is returned only in this response and is stored server-side thereafter.")
           .Produces<WidgetRotateResponse>(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IHttpResult> UpdateWidget(
        string id,
        WidgetConfigRequest req,
        IDbContextFactory<ZonesDbContext> dbFactory,
        IObfuscator<ZoneId> obfuscator,
        CancellationToken cancellationToken) {

        Result<ZoneId> zoneId = obfuscator.Decode(id);
        if(zoneId.IsFailure) return zoneId.ToProblemDetails();

        await using ZonesDbContext dbContext = await dbFactory.CreateDbContextAsync(cancellationToken);
        Zone? zone = await dbContext.Zones
            .FirstOrDefaultAsync(z => z.Id.Equals(zoneId.Value), cancellationToken);
        if(zone is null) {
            Result<Zone> notFound = ZoneErrors.NotFound;
            return notFound.ToProblemDetails();
        }

        zone.UpdateWidget(zone.Widget.With(req.Enabled, req.Theme));
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Ok(zone.Widget.ToResponse());
    }

    private static async Task<IHttpResult> RotateWidgetKeys(
        string id,
        IDbContextFactory<ZonesDbContext> dbFactory,
        IObfuscator<ZoneId> obfuscator,
        CancellationToken cancellationToken) {

        Result<ZoneId> zoneId = obfuscator.Decode(id);
        if(zoneId.IsFailure) return zoneId.ToProblemDetails();

        await using ZonesDbContext dbContext = await dbFactory.CreateDbContextAsync(cancellationToken);
        Zone? zone = await dbContext.Zones
            .FirstOrDefaultAsync(z => z.Id.Equals(zoneId.Value), cancellationToken);
        if(zone is null) {
            Result<Zone> notFound = ZoneErrors.NotFound;
            return notFound.ToProblemDetails();
        }

        WidgetConfig rotated = zone.Widget.WithNewKeys();
        zone.UpdateWidget(rotated);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Ok(new WidgetRotateResponse(rotated.SiteKey, rotated.Secret));
    }
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Veil.Auth.Domain.ValueObjects;
using Veil.Auth.Infrastructure.Persistence;
using Veil.Shared;
using Wiaoj.Endpoints;

namespace Veil.Auth.Features.ApiKeys.ListApiKeys;

/// <summary>
/// API key summary. The key hash is never exposed.
/// </summary>
public sealed record ApiKeySummaryResponse(
    string Id,
    string Name,
    IEnumerable<string> Scopes,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? LastUsedAt);

public sealed record ListApiKeysResponse(List<ApiKeySummaryResponse> Items);

public sealed class ListApiKeysEndpoint : IEndpoint {
    public void Map(IEndpointRouteBuilder app) {
        app.MapGet("/v1/api-keys", Handle)
           .WithName("ListApiKeys")
           .WithTags("ApiKeys")
           .WithSummary("Lists API keys")
           .WithDescription("Returns every API key (active and revoked) ordered by creation.")
           .Produces<ListApiKeysResponse>(StatusCodes.Status200OK);
    }

    private static async Task<IHttpResult> Handle(
        IDbContextFactory<AuthDbContext> dbFactory,
        IObfuscator<ApiKeyId> obfuscator,
        CancellationToken cancellationToken) {

        await using AuthDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var keys = await db.ApiKeys
            .AsNoTracking()
            .OrderBy(k => k.Id)
            .Select(k => new {
                k.Id,
                k.Name,
                k.Scopes,
                k.RevokedAt,
                k.CreatedAt,
                k.LastUsedAt
            })
            .ToListAsync(cancellationToken);

        List<ApiKeySummaryResponse> items = keys
            .Select(k => new ApiKeySummaryResponse(
                obfuscator.Encode(k.Id),
                k.Name,
                k.Scopes,
                k.RevokedAt is null,
                k.CreatedAt,
                k.RevokedAt,
                k.LastUsedAt))
            .ToList();

        return Results.Ok(new ListApiKeysResponse(items));
    }
}

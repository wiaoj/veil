using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Veil.Auth.Audit;
using Veil.Auth.Domain;
using Veil.Auth.Domain.ValueObjects;
using Veil.Auth.Infrastructure.Persistence;
using Veil.Shared;
using Wiaoj.Endpoints;

namespace Veil.Auth.Features.ApiKeys.RevokeApiKey;

public sealed class RevokeApiKeyEndpoint : IEndpoint {
    public void Map(IEndpointRouteBuilder app) {
        app.MapDelete("/v1/api-keys/{id}", Handle)
           .WithName("RevokeApiKey")
           .WithTags("ApiKeys")
           .WithSummary("Revokes an API key")
           .WithDescription("Permanently revokes the key; it stays listed for audit but can no longer authenticate.")
           .Produces(StatusCodes.Status204NoContent)
           .ProducesProblem(StatusCodes.Status400BadRequest)
           .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IHttpResult> Handle(
        string id,
        IDbContextFactory<AuthDbContext> dbFactory,
        IObfuscator<ApiKeyId> obfuscator,
        TimeProvider timeProvider,
        IAuditLogger audit,
        HttpContext httpContext,
        CancellationToken cancellationToken) {

        Result<ApiKeyId> keyId = obfuscator.Decode(id);
        if(keyId.IsFailure) return keyId.ToProblemDetails();

        await using AuthDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);

        ApiKey? apiKey = await db.ApiKeys
            .FirstOrDefaultAsync(k => k.Id.Equals(keyId.Value), cancellationToken);

        if(apiKey is null) {
            Result<ApiKey> notFound = AuthErrors.ApiKeyNotFound;
            return notFound.ToProblemDetails();
        }

        Result<Success> revoke = apiKey.Revoke(timeProvider.GetUtcNow());
        if(revoke.IsFailure) return revoke.ToProblemDetails();

        await db.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            AuditActions.ApiKeyRevoked,
            AuditOutcome.Success,
            actor: httpContext.User.Identity?.Name,
            actorIp: httpContext.Connection.RemoteIpAddress?.ToString(),
            target: id,
            cancellationToken: cancellationToken);

        return Results.NoContent();
    }
}

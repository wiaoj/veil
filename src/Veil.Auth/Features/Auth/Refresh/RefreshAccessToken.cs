using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using Veil.Auth.Domain;
using Veil.Auth.Features.Auth.Login;
using Veil.Auth.Infrastructure.Persistence;
using Veil.Auth.Infrastructure.Security;
using Wiaoj.Endpoints;
using Wiaoj.Primitives.Cryptography.Hashing;

namespace Veil.Auth.Features.Auth.Refresh;

public sealed record RefreshRequest(string RefreshToken);

public sealed class RefreshAccessTokenEndpoint : IEndpoint {
    public void Map(IEndpointRouteBuilder app) {
        app.MapPost("/v1/auth/refresh", Handle)
           .WithName("RefreshAccessToken")
           .WithTags("Auth")
           .WithSummary("Exchanges a refresh token for a new token pair")
           .WithDescription("Rotates the refresh token: the presented token is revoked and a new pair is issued. A replayed token fails.")
           .Produces<TokenPairResponse>(StatusCodes.Status200OK)
           .Produces(StatusCodes.Status401Unauthorized)
           .AllowAnonymous();
    }

    private static async Task<IHttpResult> Handle(
        RefreshRequest req,
        IDbContextFactory<AuthDbContext> dbFactory,
        JwtTokenService tokenService,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) {

        if(string.IsNullOrEmpty(req.RefreshToken))
            return Results.Unauthorized();

        string tokenHash = Sha256Hash.Compute(req.RefreshToken).ToHexString().ToLower();
        DateTimeOffset now = timeProvider.GetUtcNow();

        await using AuthDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);

        RefreshToken? stored = await db.RefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

        if(stored is null || !stored.IsActive(now))
            return Results.Unauthorized();

        User? user = await db.Users
            .FirstOrDefaultAsync(u => u.Id.Equals(stored.UserId), cancellationToken);

        if(user is null || user.IsDisabled)
            return Results.Unauthorized();

        // Rotate: revoke the presented token and link it to its successor.
        string newRefreshToken = $"vrt_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))}";
        string newRefreshTokenHash = Sha256Hash.Compute(newRefreshToken).ToHexString().ToLower();

        stored.Rotate(newRefreshTokenHash, now);
        await db.RefreshTokens.AddAsync(
            RefreshToken.Issue(user.Id, newRefreshTokenHash, now, tokenService.RefreshTokenLifetime),
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new TokenPairResponse(
            tokenService.IssueAccessToken(user),
            (int)tokenService.AccessTokenLifetime.TotalSeconds,
            newRefreshToken));
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using Veil.Auth.Domain;
using Veil.Auth.Infrastructure.Persistence;
using Veil.Auth.Infrastructure.Security;
using Wiaoj.Endpoints;
using Wiaoj.Primitives.Cryptography.Hashing;

namespace Veil.Auth.Features.Auth.Login;

public sealed record LoginRequest(string Email, string Password);

/// <summary>
/// Issued token pair. The refresh token is opaque and single-use (rotated
/// on refresh); only its hash is stored.
/// </summary>
public sealed record TokenPairResponse(
    string AccessToken,
    int ExpiresInSeconds,
    string RefreshToken,
    string TokenType = "Bearer");

public sealed class LoginEndpoint : IEndpoint {
    public void Map(IEndpointRouteBuilder app) {
        app.MapPost("/v1/auth/login", Handle)
           .WithName("Login")
           .WithTags("Auth")
           .WithSummary("Authenticates a user with email + password")
           .WithDescription("Returns a short-lived JWT access token and a single-use refresh token.")
           .Produces<TokenPairResponse>(StatusCodes.Status200OK)
           .Produces(StatusCodes.Status401Unauthorized)
           .AllowAnonymous();
    }

    private static async Task<IHttpResult> Handle(
        LoginRequest req,
        IDbContextFactory<AuthDbContext> dbFactory,
        JwtTokenService tokenService,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) {

        if(string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrEmpty(req.Password))
            return Results.Unauthorized();

        string email = req.Email.Trim().ToLowerInvariant();

        await using AuthDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        User? user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);

        // Same response for unknown user and wrong password — no user
        // enumeration through error shape.
        if(user is null || user.IsDisabled || !Pbkdf2PasswordHasher.Verify(req.Password, user.PasswordHash))
            return Results.Unauthorized();

        TokenPairResponse response = await IssueTokenPairAsync(db, user, tokenService, timeProvider, cancellationToken);
        return Results.Ok(response);
    }

    /// <summary>
    /// Issues an access/refresh pair and persists the refresh token hash.
    /// Shared with the refresh endpoint.
    /// </summary>
    internal static async Task<TokenPairResponse> IssueTokenPairAsync(
        AuthDbContext db,
        User user,
        JwtTokenService tokenService,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) {
        string refreshToken = $"vrt_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))}";
        string refreshTokenHash = Sha256Hash.Compute(refreshToken).ToHexString().ToLower();

        await db.RefreshTokens.AddAsync(
            RefreshToken.Issue(user.Id, refreshTokenHash, timeProvider.GetUtcNow(), tokenService.RefreshTokenLifetime),
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return new TokenPairResponse(
            tokenService.IssueAccessToken(user),
            (int)tokenService.AccessTokenLifetime.TotalSeconds,
            refreshToken);
    }
}

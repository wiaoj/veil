using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using Veil.Auth.Audit;
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
        IOptions<AuthOptions> authOptions,
        IAuditLogger audit,
        HttpContext httpContext,
        CancellationToken cancellationToken) {

        if(string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrEmpty(req.Password))
            return Results.Unauthorized();

        using Secret<char> password = Secret<char>.Parse(req.Password);
        string email = req.Email.Trim().ToLowerInvariant();
        string? ip = httpContext.Connection.RemoteIpAddress?.ToString();
        DateTimeOffset now = timeProvider.GetUtcNow();
        AuthOptions opts = authOptions.Value;

        await using AuthDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
      
        Result<HexString> hashResult = User.GenerateEmailHash(email);
        if(hashResult.IsFailure) {
            return Results.Unauthorized();
        }
        HexString emailHash = hashResult.Value;
         
        User? user = await db.Users.FirstOrDefaultAsync(u => u.EmailHash == emailHash, cancellationToken);

        // Locked-out accounts are rejected before the password is even checked.
        // The response shape is identical to a normal failure (no signal that
        // the account is locked, no user enumeration).
        if(user is not null && user.IsLockedOut(now)) {
            await audit.WriteAsync(AuditActions.LoginLockedOut, AuditOutcome.Failure, email, ip, email, cancellationToken: cancellationToken);
            return Results.Unauthorized();
        }

        // Same response for unknown user and wrong password — no user
        // enumeration through error shape.
        if(user is null || user.IsDisabled || !Pbkdf2PasswordHasher.Verify(password, user.PasswordHash)) {
            // Count failures (and arm lockout) only for real, enabled accounts.
            if(user is not null && !user.IsDisabled) {
                user.RegisterFailedLogin(now, opts.MaxFailedLoginAttempts, TimeSpan.FromMinutes(opts.LockoutMinutes));
                await db.SaveChangesAsync(cancellationToken);
            }
            await audit.WriteAsync(AuditActions.LoginFailure, AuditOutcome.Failure, email, ip, email, cancellationToken: cancellationToken);
            return Results.Unauthorized();
        }

        // Success: clear the failed-attempt counter (persisted together with
        // the refresh token inside IssueTokenPairAsync's SaveChanges).
        user.RegisterSuccessfulLogin();
        TokenPairResponse response = await IssueTokenPairAsync(db, user, tokenService, timeProvider, cancellationToken);
        await audit.WriteAsync(AuditActions.LoginSuccess, AuditOutcome.Success, email, ip, email, cancellationToken: cancellationToken);
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
        var refreshTokenHash = Sha256Hash.Compute(refreshToken).ToHexStringLower();

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

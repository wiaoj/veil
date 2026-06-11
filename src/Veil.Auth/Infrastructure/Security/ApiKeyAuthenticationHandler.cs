using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Veil.Auth.Domain;
using Veil.Auth.Infrastructure.Persistence;
using Wiaoj.Primitives.Cryptography.Hashing;

namespace Veil.Auth.Infrastructure.Security;

/// <summary>
/// Authenticates machine callers via the <c>X-Api-Key</c> header (SHA-256
/// hash compare against the stored key). Key scopes surface as "scope"
/// claims for future fine-grained policies; last-used is refreshed at most
/// once a minute.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IDbContextFactory<AuthDbContext> dbFactory,
    TimeProvider timeProvider)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder) {

    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    private static readonly TimeSpan MarkUsedInterval = TimeSpan.FromMinutes(1);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync() {
        string? key = this.Request.Headers[HeaderName].FirstOrDefault();
        if(string.IsNullOrEmpty(key))
            return AuthenticateResult.NoResult();

        string keyHash = Sha256Hash.Compute(key).ToHexString().ToLower();

        await using AuthDbContext db = await dbFactory.CreateDbContextAsync(this.Context.RequestAborted);
        ApiKey? apiKey = await db.ApiKeys
            .FirstOrDefaultAsync(k => k.KeyHash == keyHash, this.Context.RequestAborted);

        if(apiKey is null || !apiKey.IsActive)
            return AuthenticateResult.Fail("Invalid or revoked API key.");

        DateTimeOffset now = timeProvider.GetUtcNow();
        if(apiKey.LastUsedAtUtc is null || now - apiKey.LastUsedAtUtc >= MarkUsedInterval) {
            apiKey.MarkUsed(now);
            await db.SaveChangesAsync(this.Context.RequestAborted);
        }

        List<Claim> claims = [new Claim(ClaimTypes.Name, apiKey.Name)];
        claims.AddRange(apiKey.Scopes.Select(scope => new Claim("scope", scope)));

        ClaimsPrincipal principal = new(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }
}

using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using Veil.Auth.Domain;
using Veil.Auth.Domain.ValueObjects;
using Veil.Shared;

namespace Veil.Auth.Infrastructure.Security;

/// <summary>
/// Issues HMAC-SHA256 signed access tokens. The subject claim carries the
/// public (obfuscated) user id — raw ids never leave the module. Tokens are
/// signed with the active key from the <see cref="SigningKeyRing"/> and carry
/// its id in the <c>kid</c> header so rotation stays zero-downtime.
/// </summary>
public sealed class JwtTokenService(
    IOptions<AuthOptions> options,
    SigningKeyRing keyRing,
    IObfuscator<UserId> userObfuscator,
    TimeProvider timeProvider) {

    private readonly AuthOptions _options = options.Value;
    private readonly JsonWebTokenHandler _handler = new();

    public TimeSpan AccessTokenLifetime => TimeSpan.FromMinutes(this._options.AccessTokenMinutes);
    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(this._options.RefreshTokenDays);

    public string IssueAccessToken(User user) {
        if(keyRing.ActiveCredentials is null)
            throw new InvalidOperationException("No Auth signing key is configured; cannot issue tokens.");

        DateTimeOffset now = timeProvider.GetUtcNow();

        SecurityTokenDescriptor descriptor = new() {
            Issuer = this._options.Issuer,
            Audience = this._options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.Add(this.AccessTokenLifetime).UtcDateTime,
            Subject = new ClaimsIdentity([
                new Claim(JwtRegisteredClaimNames.Sub, userObfuscator.Encode(user.Id)),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim(ClaimTypes.Role, user.Role.ToString()),
            ]),
            SigningCredentials = keyRing.ActiveCredentials
        };

        return this._handler.CreateToken(descriptor);
    }
}

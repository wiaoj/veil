using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Text;
using Veil.Auth.Domain;
using Veil.Auth.Domain.ValueObjects;
using Veil.Shared;

namespace Veil.Auth.Infrastructure.Security;

/// <summary>
/// Issues HMAC-SHA256 signed access tokens. The subject claim carries the
/// public (obfuscated) user id — raw ids never leave the module.
/// </summary>
public sealed class JwtTokenService(
    IOptions<AuthOptions> options,
    IObfuscator<UserId> userObfuscator,
    TimeProvider timeProvider) {

    private readonly AuthOptions _options = options.Value;
    private readonly JsonWebTokenHandler _handler = new();

    public TimeSpan AccessTokenLifetime => TimeSpan.FromMinutes(this._options.AccessTokenMinutes);
    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(this._options.RefreshTokenDays);

    public string IssueAccessToken(User user) {
        if(string.IsNullOrEmpty(this._options.SigningKey))
            throw new InvalidOperationException("Auth:SigningKey is not configured; cannot issue tokens.");

        DateTimeOffset now = timeProvider.GetUtcNow();
        SymmetricSecurityKey key = new(Encoding.UTF8.GetBytes(this._options.SigningKey));

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
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        };

        return this._handler.CreateToken(descriptor);
    }
}

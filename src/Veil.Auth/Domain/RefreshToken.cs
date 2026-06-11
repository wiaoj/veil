using Veil.Auth.Domain.ValueObjects;
using Wiaoj.Ddd;

namespace Veil.Auth.Domain;

/// <summary>
/// One opaque refresh token, stored by hash. Tokens rotate on every use:
/// the consumed token is revoked and linked to its successor's hash, so a
/// replayed (stolen) token is detectable as already-rotated.
/// </summary>
public sealed class RefreshToken : Entity<RefreshTokenId> {
    public UserId UserId { get; private set; }
    public string TokenHash { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? RevokedAtUtc { get; private set; }
    public string? ReplacedByTokenHash { get; private set; }

    private RefreshToken() { }

    public static RefreshToken Issue(
        UserId userId,
        string tokenHash,
        DateTimeOffset createdAtUtc,
        TimeSpan lifetime) {
        return new RefreshToken {
            Id = RefreshTokenId.New(),
            UserId = userId,
            TokenHash = tokenHash,
            CreatedAtUtc = createdAtUtc,
            ExpiresAtUtc = createdAtUtc.Add(lifetime)
        };
    }

    public bool IsActive(DateTimeOffset now) {
        return this.RevokedAtUtc is null && now < this.ExpiresAtUtc;
    }

    public void Rotate(string replacedByTokenHash, DateTimeOffset revokedAtUtc) {
        this.RevokedAtUtc = revokedAtUtc;
        this.ReplacedByTokenHash = replacedByTokenHash;
    }

    public void Revoke(DateTimeOffset revokedAtUtc) {
        this.RevokedAtUtc = revokedAtUtc;
    }
}

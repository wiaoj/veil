using Veil.Auth.Domain.ValueObjects;
using Wiaoj.Primitives.Collections;

namespace Veil.Auth.Domain;

/// <summary>
/// A long-lived machine credential for the management API. The plaintext key
/// is shown exactly once at creation; only its SHA-256 hash is stored.
/// Revocation is permanent — a revoked key is never reactivated.
/// </summary>
public sealed class ApiKey : Aggregate<ApiKeyId> {
    public string Name { get; private set; }
    public HexString KeyHash { get; private set; }
    public EquatableArray<string> Scopes { get; private set; }
    public UserId CreatedBy { get; private set; } 
    public DateTimeOffset? RevokedAt { get; private set; }
    public DateTimeOffset? LastUsedAt { get; private set; }

    public bool IsActive => this.RevokedAt is null;

    private ApiKey() { }

    public static Result<ApiKey> Create(
        string name,
        HexString keyHash,
        IEnumerable<string> scopes,
        UserId createdBy) {
        if(string.IsNullOrWhiteSpace(name))
            return AuthErrors.ApiKeyNameEmpty;

        if(keyHash == default)
            return AuthErrors.KeyHashEmpty;

        var normalizedScopes = scopes
           .Select(s => s?.Trim().ToLowerInvariant() ?? "")
           .Where(s => s.Length > 0)
           .Distinct()
           .ToEquatableArray();

        return new ApiKey {
            Id = ApiKeyId.New(),
            Name = name.Trim(),
            KeyHash = keyHash,
            Scopes = normalizedScopes,
            CreatedBy = createdBy
        };
    }

    public Result<Success> Revoke(DateTimeOffset revokedAtUtc) {
        if(this.RevokedAt is not null)
            return AuthErrors.ApiKeyAlreadyRevoked;

        this.RevokedAt = revokedAtUtc;
        return Result.Success();
    }

    /// <summary>Records key usage (throttled by the authentication handler).</summary>
    public void MarkUsed(DateTimeOffset usedAtUtc) {
        this.LastUsedAt = usedAtUtc;
    }
}
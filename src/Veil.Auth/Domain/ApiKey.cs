using Veil.Auth.Domain.ValueObjects;

namespace Veil.Auth.Domain;

/// <summary>
/// A long-lived machine credential for the management API. The plaintext key
/// is shown exactly once at creation; only its SHA-256 hash is stored.
/// Revocation is permanent — a revoked key is never reactivated.
/// </summary>
public sealed class ApiKey : Aggregate<ApiKeyId> {
    public string Name { get; private set; }
    public string KeyHash { get; private set; }
    public List<string> Scopes { get; private set; }
    public UserId CreatedBy { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? RevokedAtUtc { get; private set; }
    public DateTimeOffset? LastUsedAtUtc { get; private set; }

    public bool IsActive => this.RevokedAtUtc is null;

    private ApiKey() { }

    public static Result<ApiKey> Create(
        string name,
        string keyHash,
        IEnumerable<string> scopes,
        UserId createdBy,
        DateTimeOffset createdAtUtc) {
        if(string.IsNullOrWhiteSpace(name))
            return AuthErrors.ApiKeyNameEmpty;

        if(string.IsNullOrWhiteSpace(keyHash))
            return AuthErrors.KeyHashEmpty;

        List<string> normalizedScopes = scopes
            .Select(s => s?.Trim().ToLowerInvariant() ?? "")
            .Where(s => s.Length > 0)
            .Distinct()
            .ToList();

        return Result<ApiKey>.Success(new ApiKey {
            Id = ApiKeyId.New(),
            Name = name.Trim(),
            KeyHash = keyHash,
            Scopes = normalizedScopes,
            CreatedBy = createdBy,
            CreatedAtUtc = createdAtUtc
        });
    }

    public Result<Success> Revoke(DateTimeOffset revokedAtUtc) {
        if(this.RevokedAtUtc is not null)
            return AuthErrors.ApiKeyAlreadyRevoked;

        this.RevokedAtUtc = revokedAtUtc;
        return Result.Success();
    }

    /// <summary>Records key usage (throttled by the authentication handler).</summary>
    public void MarkUsed(DateTimeOffset usedAtUtc) {
        this.LastUsedAtUtc = usedAtUtc;
    }
}

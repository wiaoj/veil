namespace Veil.Auth;

/// <summary>
/// Typed view of the <c>Auth</c> configuration section.
/// </summary>
public sealed record AuthOptions {
    public const string SectionName = "Auth";

    public string Issuer { get; init; } = "veil-control-plane";
    public string Audience { get; init; } = "veil";

    /// <summary>
    /// HMAC-SHA256 signing key for access tokens — at least 32 bytes of
    /// entropy (any string ≥ 32 chars). Unset disables the auth module's
    /// token issuance and JWT validation entirely (endpoints stay open),
    /// so it must be configured everywhere except throwaway dev setups.
    ///
    /// Legacy single-key form. When <see cref="SigningKeys"/> is non-empty it
    /// takes precedence and this is ignored.
    /// </summary>
    public string? SigningKey { get; init; }

    /// <summary>
    /// Versioned signing keys for zero-downtime rotation. Tokens are signed
    /// with <see cref="ActiveSigningKeyId"/> (each token carries its key id in
    /// the JWT <c>kid</c> header); every key here remains valid for
    /// verification. To rotate: add a new key, point <see cref="ActiveSigningKeyId"/>
    /// at it, then drop the old key once outstanding tokens have expired.
    /// </summary>
    public List<SigningKeyEntry> SigningKeys { get; init; } = [];

    /// <summary>Key id used to sign new tokens. Defaults to the first entry.</summary>
    public string? ActiveSigningKeyId { get; init; }

    public int AccessTokenMinutes { get; init; } = 15;
    public int RefreshTokenDays { get; init; } = 14;

    /// <summary>Consecutive failed logins before an account is locked out.</summary>
    public int MaxFailedLoginAttempts { get; init; } = 5;

    /// <summary>How long an account stays locked after hitting the limit.</summary>
    public int LockoutMinutes { get; init; } = 15;

    /// <summary>Seeded admin account (created only when no users exist).</summary>
    public string AdminEmail { get; init; } = "admin@veil.local";

    /// <summary>
    /// Seed admin password. When unset a random one is generated and logged
    /// once at startup.
    /// </summary>
    public string? AdminPassword { get; init; }
}

/// <summary>One entry in the signing key ring: a stable id and its secret.</summary>
public sealed record SigningKeyEntry {
    /// <summary>Stable identifier written to the JWT <c>kid</c> header.</summary>
    public string Kid { get; init; } = "default";

    /// <summary>HMAC-SHA256 secret — at least 32 chars.</summary>
    public string Key { get; init; } = "";
}

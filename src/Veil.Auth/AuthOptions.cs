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
    /// </summary>
    public string? SigningKey { get; init; }

    public int AccessTokenMinutes { get; init; } = 15;
    public int RefreshTokenDays { get; init; } = 14;

    /// <summary>Seeded admin account (created only when no users exist).</summary>
    public string AdminEmail { get; init; } = "admin@veil.local";

    /// <summary>
    /// Seed admin password. When unset a random one is generated and logged
    /// once at startup.
    /// </summary>
    public string? AdminPassword { get; init; }
}

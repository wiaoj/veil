namespace Veil.Api.ConfigSync;

/// <summary>
/// Typed view of the <c>ConfigSync</c> configuration section.
/// </summary>
public sealed record ConfigSyncOptions {
    public const string SectionName = "ConfigSync";

    /// <summary>
    /// Header carrying the HMAC-SHA256 signature of the push body.
    /// Must match the edge's <c>VEIL_PUSH_SIGNATURE_HEADER</c>.
    /// </summary>
    public string SignatureHeader { get; init; } = "X-Veil-Signature";

    /// <summary>
    /// Reserved path on the edge node receiving config pushes. Must match
    /// the edge's push receiver path.
    /// </summary>
    public string PushPath { get; init; } = "/_veil/internal/config";

    /// <summary>
    /// Shared HMAC key signing push bodies — 64 hex chars (32 bytes).
    /// Unset disables config sync entirely.
    /// </summary>
    public string? PushHmacKey { get; init; }

    /// <summary>
    /// StackExchange.Redis connection string. When set, ConfigSync runs with
    /// Redis leader election (single active replica) and a backoff retry
    /// queue; unset → single-instance local coordination.
    /// </summary>
    public string? RedisConnection { get; init; }
}

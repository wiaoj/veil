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

    /// <summary>How a registered node's push addresses are resolved.</summary>
    public DiscoveryOptions Discovery { get; init; } = new();
}

/// <summary>Where a node's *current* addresses come from.</summary>
public enum DiscoveryMode {
    /// <summary>Use the address recorded at registration. Right for static
    /// fleets (VMs, docker-compose) where the address never moves.</summary>
    Static,

    /// <summary>Resolve a DNS name to every A record — a Kubernetes headless
    /// Service returns one per ready pod. Kubernetes is already the registry,
    /// so nothing is stored: the fleet is discovered at push time.</summary>
    Dns,

    /// <summary>Read self-registered, TTL'd entries from Redis. For dynamic
    /// fleets outside Kubernetes: a node that stops renewing simply expires,
    /// so there is no stale row to reap.</summary>
    Redis,
}

public sealed record DiscoveryOptions {
    public DiscoveryMode Mode { get; init; } = DiscoveryMode.Static;

    /// <summary>Dns mode: the name to resolve (e.g. a headless Service).</summary>
    public string? DnsName { get; init; }

    /// <summary>Dns mode: port the edge's push receiver listens on.</summary>
    public int Port { get; init; } = 8080;

    /// <summary>Dns/Redis mode: scheme for the resolved addresses.</summary>
    public string Scheme { get; init; } = "http";

    /// <summary>Redis mode: key prefix the nodes self-register under.</summary>
    public string RedisKeyPrefix { get; init; } = "veil:nodes:";
}

using Veil.Zones.Domain.Enums;

namespace Veil.Zones.Features.Zones;

// Request payloads shared by the Zones feature slices. Declared in the
// parent feature namespace so every slice sees them without extra usings.
// This is the management API contract — the edge config snapshot has its
// own serialization model.

/// <summary>
/// A target server for upstream routing.
/// </summary>
/// <param name="Url">The absolute URL of the upstream target.</param>
/// <param name="Weight">The routing weight of this target.</param>
public sealed record UpstreamTargetRequest(string Url, int Weight = 1);

/// <summary>
/// Configuration for the upstream routing and load balancing.
/// </summary>
/// <param name="Targets">List of upstream targets.</param>
/// <param name="Strategy">The load balancing strategy to use.</param>
/// <param name="ConnectTimeoutMs">Timeout for establishing a connection to the upstream (in milliseconds).</param>
/// <param name="ResponseTimeoutMs">Timeout for receiving a response from the upstream (in milliseconds).</param>
/// <param name="PassHostHeader">Whether to pass the original host header to the upstream.</param>
public sealed record UpstreamConfigRequest(
    List<UpstreamTargetRequest> Targets,
    LoadBalanceStrategy Strategy = LoadBalanceStrategy.RoundRobin,
    int ConnectTimeoutMs = 5000,
    int ResponseTimeoutMs = 30000,
    bool PassHostHeader = true);

/// <summary>
/// Configuration for the Proof-of-Work challenge mechanism.
/// </summary>
/// <param name="Enabled">Whether the challenge is active for this zone.</param>
/// <param name="Difficulty">The difficulty level for the PoW challenge (8-32).</param>
/// <param name="ExpirationSeconds">How long the issued token remains valid.</param>
/// <param name="RequireCaptcha">Whether to require a CAPTCHA fallback for high-risk clients.</param>
public sealed record ChallengeConfigRequest(
    bool Enabled = true,
    int Difficulty = 20,
    int ExpirationSeconds = 600,
    bool RequireCaptcha = false,
    int RiskThreshold = 70);

/// <summary>
/// Flat wire representation of a rule condition. <paramref name="Type"/>
/// selects the condition kind; the remaining fields are populated per kind.
/// </summary>
/// <param name="Type">Condition discriminator: ip_match, ip_range, country, asn, path_match, path_regex, header, user_agent.</param>
/// <param name="Value">Primary value (IP, CIDR, country code, path pattern, regex, header value or UA pattern).</param>
/// <param name="Name">Header name — required for the header condition.</param>
/// <param name="Asn">Autonomous system number — required for the asn condition.</param>
/// <param name="Mode">Path match mode for path_match: "prefix" (default) or "exact".</param>
public sealed record RuleConditionRequest(
    string Type,
    string? Value = null,
    string? Name = null,
    int? Asn = null,
    string? Mode = null,
    string? Path = null);

/// <summary>
/// Rate limit parameters for rules with the RateLimit action.
/// </summary>
/// <param name="Requests">Allowed request count per window.</param>
/// <param name="WindowSecs">Window length in seconds.</param>
public sealed record RateLimitRequest(int Requests, int WindowSecs);

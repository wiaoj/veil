using Veil.Zones.Domain.Enums;

namespace Veil.Zones.Features.Zones;

// Response payloads shared by the Zones feature slices. Mirrors of the
// request shapes are deliberate duplicates: request and response contracts
// evolve independently.

public sealed record UpstreamTargetResponse(string Url, int Weight);

public sealed record UpstreamConfigResponse(
    List<UpstreamTargetResponse> Targets,
    LoadBalanceStrategy Strategy,
    int ConnectTimeoutMs,
    int ResponseTimeoutMs,
    bool PassHostHeader);

public sealed record ChallengeConfigResponse(
    bool Enabled,
    int Difficulty,
    int ExpirationSeconds,
    bool RequireCaptcha,
    int RiskThreshold,
    string CookieDomain);

/// <summary>Embeddable widget config as returned by the management API. The
/// secret is never returned (write-only); <see cref="HasSecret"/> only reports
/// whether keys have been provisioned.</summary>
public sealed record WidgetConfigResponse(
    bool Enabled,
    string SiteKey,
    string Theme,
    bool HasSecret);

/// <summary>
/// Flat wire representation of a rule condition; same shape as the request
/// counterpart (see <see cref="RuleConditionRequest"/> for field semantics).
/// </summary>
public sealed record RuleConditionResponse(
    string Type,
    string? Value = null,
    string? Name = null,
    int? Asn = null,
    string? Mode = null,
    string? Path = null,
    string? Subject = null,
    string? Version = null);

public sealed record RateLimitResponse(int Requests, int WindowSecs);

/// <summary>
/// A rule as returned by the management API.
/// </summary>
public sealed record RuleResponse(
    string Id,
    string Name,
    int Priority,
    string Action,
    bool IsEnabled,
    List<RuleConditionResponse> Conditions,
    RateLimitResponse? RateLimit);

/// <summary>
/// Zone status snapshot returned by status-changing endpoints.
/// </summary>
public sealed record ZoneStatusResponse(string Id, string Status);

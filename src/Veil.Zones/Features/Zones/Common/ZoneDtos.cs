using Veil.Shared;
using Veil.Zones.Domain;
using Veil.Zones.Domain.Enums;
using Veil.Zones.Domain.ValueObjects;

namespace Veil.Zones.Features.Zones;

// DTOs shared by the Zones feature slices. Declared in the parent feature
// namespace so every slice sees them without extra usings. This is the
// management API contract — the edge config snapshot (Phase 3) has its own
// serialization model.

/// <summary>
/// Represents a target server for upstream routing.
/// </summary>
/// <param name="Url">The absolute URL of the upstream target.</param>
/// <param name="Weight">The routing weight of this target.</param>
public sealed record UpstreamTargetDto(string Url, int Weight = 1);

/// <summary>
/// Configuration for the upstream routing and load balancing.
/// </summary>
/// <param name="Targets">List of upstream targets.</param>
/// <param name="Strategy">The load balancing strategy to use.</param>
/// <param name="ConnectTimeoutMs">Timeout for establishing a connection to the upstream (in milliseconds).</param>
/// <param name="ResponseTimeoutMs">Timeout for receiving a response from the upstream (in milliseconds).</param>
/// <param name="PassHostHeader">Whether to pass the original host header to the upstream.</param>
public sealed record UpstreamConfigDto(
    List<UpstreamTargetDto> Targets,
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
public sealed record ChallengeConfigDto(
    bool Enabled = true,
    int Difficulty = 20,
    int ExpirationSeconds = 600,
    bool RequireCaptcha = false);

/// <summary>
/// Flat wire representation of a rule condition. <paramref name="Type"/>
/// selects the condition kind; the remaining fields are populated per kind.
/// </summary>
/// <param name="Type">Condition discriminator: ip_match, ip_range, country, asn, path_match, path_regex, header, user_agent.</param>
/// <param name="Value">Primary value (IP, CIDR, country code, path pattern, regex, header value or UA pattern).</param>
/// <param name="Name">Header name — required for the header condition.</param>
/// <param name="Asn">Autonomous system number — required for the asn condition.</param>
/// <param name="Mode">Path match mode for path_match: "prefix" (default) or "exact".</param>
public sealed record RuleConditionDto(
    string Type,
    string? Value = null,
    string? Name = null,
    int? Asn = null,
    string? Mode = null);

/// <summary>
/// Rate limit parameters for rules with the RateLimit action.
/// </summary>
/// <param name="Requests">Allowed request count per window.</param>
/// <param name="WindowSecs">Window length in seconds.</param>
public sealed record RateLimitDto(int Requests, int WindowSecs);

/// <summary>
/// A rule as returned by the management API.
/// </summary>
public sealed record RuleResponse(
    string Id,
    string Name,
    int Priority,
    string Action,
    bool IsEnabled,
    List<RuleConditionDto> Conditions,
    RateLimitDto? RateLimit);

/// <summary>
/// Zone status snapshot returned by status-changing endpoints.
/// </summary>
public sealed record ZoneStatusResponse(string Id, string Status);

// ─────────────────────────────────────────────────────────────────────
// DTO ⇄ domain mapping
// ─────────────────────────────────────────────────────────────────────

public static class ZoneMappings {
    public static Result<UpstreamConfig> ToDomain(this UpstreamConfigDto dto) {
        List<UpstreamTarget> targets = new(dto.Targets.Count);
        foreach(UpstreamTargetDto target in dto.Targets) {
            if(!Uri.TryCreate(target.Url, UriKind.Absolute, out Uri? url))
                return ZoneErrors.UpstreamInvalidUrl(target.Url);

            targets.Add(new UpstreamTarget(url, target.Weight));
        }

        return UpstreamConfig.Create(
            targets,
            dto.Strategy,
            TimeSpan.FromMilliseconds(dto.ConnectTimeoutMs),
            TimeSpan.FromMilliseconds(dto.ResponseTimeoutMs),
            dto.PassHostHeader);
    }

    public static Result<ChallengeConfig> ToDomain(this ChallengeConfigDto dto) {
        if(!dto.Enabled)
            return ChallengeConfig.Disabled;

        Result<PowDifficulty> difficulty = PowDifficulty.Create(dto.Difficulty);
        if(difficulty.IsFailure) return Result.Failure<ChallengeConfig>(difficulty.FirstError);

        Result<TokenTtl> ttl = TokenTtl.Create(TimeSpan.FromSeconds(dto.ExpirationSeconds));
        if(ttl.IsFailure) return Result.Failure<ChallengeConfig>(ttl.FirstError);

        return ChallengeConfig.Create(difficulty.Value, ttl.Value, dto.RequireCaptcha);
    }

    public static Result<List<RuleCondition>> ToDomain(this IReadOnlyList<RuleConditionDto> dtos) {
        List<RuleCondition> conditions = new(dtos.Count);
        foreach(RuleConditionDto dto in dtos) {
            Result<RuleCondition> condition = dto.ToDomain();
            if(condition.IsFailure) return Result.Failure<List<RuleCondition>>(condition.FirstError);
            conditions.Add(condition.Value);
        }
        return conditions;
    }

    public static Result<RuleCondition> ToDomain(this RuleConditionDto dto) {
        switch(dto.Type) {
            case "ip_match":
                if(string.IsNullOrWhiteSpace(dto.Value))
                    return RuleErrors.ConditionValueMissing(dto.Type, nameof(dto.Value));
                return new IpMatchCondition(dto.Value);

            case "ip_range":
                if(string.IsNullOrWhiteSpace(dto.Value))
                    return RuleErrors.ConditionValueMissing(dto.Type, nameof(dto.Value));
                return new IpRangeMatchCondition(dto.Value);

            case "country":
                if(string.IsNullOrWhiteSpace(dto.Value))
                    return RuleErrors.ConditionValueMissing(dto.Type, nameof(dto.Value));
                return new CountryMatchCondition(dto.Value.ToUpperInvariant());

            case "asn":
                if(dto.Asn is not int asn)
                    return RuleErrors.ConditionValueMissing(dto.Type, nameof(dto.Asn));
                return new AsnMatchCondition(asn);

            case "path_match":
                if(string.IsNullOrWhiteSpace(dto.Value))
                    return RuleErrors.ConditionValueMissing(dto.Type, nameof(dto.Value));
                PathMatchMode mode = string.Equals(dto.Mode, "exact", StringComparison.OrdinalIgnoreCase)
                    ? PathMatchMode.Exact
                    : PathMatchMode.Prefix;
                return new PathMatchCondition(dto.Value, mode);

            case "path_regex":
                if(string.IsNullOrWhiteSpace(dto.Value))
                    return RuleErrors.ConditionValueMissing(dto.Type, nameof(dto.Value));
                return new PathRegexMatchCondition(dto.Value);

            case "header":
                if(string.IsNullOrWhiteSpace(dto.Name))
                    return RuleErrors.ConditionValueMissing(dto.Type, nameof(dto.Name));
                if(dto.Value is null)
                    return RuleErrors.ConditionValueMissing(dto.Type, nameof(dto.Value));
                return new HeaderMatchCondition(dto.Name, dto.Value);

            case "user_agent":
                if(string.IsNullOrWhiteSpace(dto.Value))
                    return RuleErrors.ConditionValueMissing(dto.Type, nameof(dto.Value));
                return new UserAgentMatchCondition(dto.Value);

            default:
                return RuleErrors.ConditionTypeUnknown(dto.Type);
        }
    }

    public static RuleConditionDto ToDto(this RuleCondition condition) {
        return condition switch {
            IpMatchCondition c => new RuleConditionDto("ip_match", c.Ip),
            IpRangeMatchCondition c => new RuleConditionDto("ip_range", c.Cidr),
            CountryMatchCondition c => new RuleConditionDto("country", c.CountryCode),
            AsnMatchCondition c => new RuleConditionDto("asn", Asn: c.Asn),
            PathMatchCondition c => new RuleConditionDto(
                "path_match", c.Pattern,
                Mode: c.Mode == PathMatchMode.Exact ? "exact" : "prefix"),
            PathRegexMatchCondition c => new RuleConditionDto("path_regex", c.Regex),
            HeaderMatchCondition c => new RuleConditionDto("header", c.Value, Name: c.Name),
            UserAgentMatchCondition c => new RuleConditionDto("user_agent", c.Pattern),
            _ => new RuleConditionDto(condition.Type)
        };
    }

    public static RuleResponse ToResponse(this Rule rule, IObfuscator<RuleId> obfuscator) {
        return new RuleResponse(
            obfuscator.Encode(rule.Id),
            rule.Name,
            rule.Priority,
            rule.Action.ToString(),
            rule.IsEnabled,
            rule.Conditions.Select(c => c.ToDto()).ToList(),
            rule.RateLimit is null
                ? null
                : new RateLimitDto(rule.RateLimit.Requests, rule.RateLimit.WindowSecs));
    }

    public static UpstreamConfigDto ToDto(this UpstreamConfig upstream) {
        return new UpstreamConfigDto(
            upstream.Targets.Select(t => new UpstreamTargetDto(t.Url.ToString(), t.Weight)).ToList(),
            upstream.Strategy,
            (int)upstream.ConnectTimeout.TotalMilliseconds,
            (int)upstream.ResponseTimeout.TotalMilliseconds,
            upstream.PassHostHeader);
    }

    public static ChallengeConfigDto ToDto(this ChallengeConfig challenge) {
        return new ChallengeConfigDto(
            challenge.Enabled,
            challenge.PowDifficulty.Value,
            (int)challenge.TokenTtl.Value.TotalSeconds,
            challenge.RequireCaptchaOnHighRisk);
    }
}

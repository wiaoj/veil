using Veil.Shared;
using Veil.Zones.Domain;
using Veil.Zones.Domain.Enums;
using Veil.Zones.Domain.ValueObjects;

namespace Veil.Zones.Features.Zones;

/// <summary>
/// Domain → response payload mapping.
/// </summary>
public static class ZoneResponseMapping {
    public static RuleConditionResponse ToResponse(this RuleCondition condition) {
        return condition switch {
            IpMatchCondition c => new RuleConditionResponse("ip_match", c.Ip),
            IpRangeMatchCondition c => new RuleConditionResponse("ip_range", c.Cidr),
            CountryMatchCondition c => new RuleConditionResponse("country", c.CountryCode),
            AsnMatchCondition c => new RuleConditionResponse("asn", Asn: c.Asn),
            PathMatchCondition c => new RuleConditionResponse(
                "path_match", c.Pattern,
                Mode: c.Mode == PathMatchMode.Exact ? "exact" : "prefix"),
            PathRegexMatchCondition c => new RuleConditionResponse("path_regex", c.Regex),
            HeaderMatchCondition c => new RuleConditionResponse("header", c.Value, Name: c.Name),
            UserAgentMatchCondition c => new RuleConditionResponse("user_agent", c.Pattern),
            Ja3MatchCondition c => new RuleConditionResponse("ja3", c.Fingerprint),
            Ja4MatchCondition c => new RuleConditionResponse("ja4", c.Fingerprint),
            _ => new RuleConditionResponse(condition.Type)
        };
    }

    public static RuleResponse ToResponse(this Rule rule, IObfuscator<RuleId> obfuscator) {
        return new RuleResponse(
            obfuscator.Encode(rule.Id),
            rule.Name,
            rule.Priority,
            rule.Action.ToString(),
            rule.IsEnabled,
            rule.Conditions.Select(c => c.ToResponse()).ToList(),
            rule.RateLimit is null
                ? null
                : new RateLimitResponse(rule.RateLimit.Requests, rule.RateLimit.WindowSecs));
    }

    public static UpstreamConfigResponse ToResponse(this UpstreamConfig upstream) {
        return new UpstreamConfigResponse(
            upstream.Targets.Select(t => new UpstreamTargetResponse(t.Url.ToString(), t.Weight)).ToList(),
            upstream.Strategy,
            (int)upstream.ConnectTimeout.TotalMilliseconds,
            (int)upstream.ResponseTimeout.TotalMilliseconds,
            upstream.PassHostHeader);
    }

    public static ChallengeConfigResponse ToResponse(this ChallengeConfig challenge) {
        return new ChallengeConfigResponse(
            challenge.Enabled,
            challenge.PowDifficulty.Value,
            (int)challenge.TokenTtl.Value.TotalSeconds,
            challenge.RequireCaptchaOnHighRisk);
    }
}

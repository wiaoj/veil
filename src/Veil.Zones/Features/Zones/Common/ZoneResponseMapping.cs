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
            MethodMatchCondition c => new RuleConditionResponse("method", c.Method),
            QueryRegexMatchCondition c => new RuleConditionResponse("query_regex", c.Regex),
            HeaderRegexMatchCondition c => new RuleConditionResponse("header_regex", c.Regex, Name: c.Name),
            BodyRegexMatchCondition c => new RuleConditionResponse("body_regex", c.Regex),
            BodyJsonMatchCondition c => new RuleConditionResponse("body_json", c.Regex, Path: c.Path),
            BodySchemaMatchCondition c => new RuleConditionResponse("body_schema", Subject: c.Subject, Version: c.Version),
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
            challenge.RequireCaptchaOnHighRisk,
            challenge.RiskThreshold);
    }

    public static WidgetConfigResponse ToResponse(this WidgetConfig widget) {
        // Secret is never returned — only whether one has been provisioned.
        return new WidgetConfigResponse(
            widget.Enabled,
            widget.SiteKey,
            widget.Theme,
            !string.IsNullOrEmpty(widget.Secret));
    }
}

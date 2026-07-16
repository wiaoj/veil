using Veil.Zones.Domain;
using Veil.Zones.Domain.Enums;
using Veil.Zones.Domain.ValueObjects;

namespace Veil.Zones.Features.Zones;

/// <summary>
/// Request payload → domain mapping. Validation failures surface as domain
/// errors, never exceptions.
/// </summary>
public static class ZoneRequestMapping {
    public static Result<UpstreamConfig> ToDomain(this UpstreamConfigRequest request) {
        List<UpstreamTarget> targets = new(request.Targets.Count);
        foreach(UpstreamTargetRequest target in request.Targets) {
            if(!Uri.TryCreate(target.Url, UriKind.Absolute, out Uri? url))
                return ZoneErrors.UpstreamInvalidUrl(target.Url);

            targets.Add(new UpstreamTarget(url, target.Weight));
        }

        return UpstreamConfig.Create(
            targets,
            request.Strategy,
            TimeSpan.FromMilliseconds(request.ConnectTimeoutMs),
            TimeSpan.FromMilliseconds(request.ResponseTimeoutMs),
            request.PassHostHeader);
    }

    public static Result<ChallengeConfig> ToDomain(this ChallengeConfigRequest request) {
        if(!request.Enabled)
            return ChallengeConfig.Disabled;

        Result<PowDifficulty> difficulty = PowDifficulty.Create(request.Difficulty);
        if(difficulty.IsFailure) return Result.Failure<ChallengeConfig>(difficulty.FirstError);

        Result<TokenTtl> ttl = TokenTtl.Create(TimeSpan.FromSeconds(request.ExpirationSeconds));
        if(ttl.IsFailure) return Result.Failure<ChallengeConfig>(ttl.FirstError);

        return ChallengeConfig.Create(difficulty.Value, ttl.Value, request.RequireCaptcha, request.RiskThreshold);
    }

    public static Result<List<RuleCondition>> ToDomain(this IReadOnlyList<RuleConditionRequest> requests) {
        List<RuleCondition> conditions = new(requests.Count);
        foreach(RuleConditionRequest request in requests) {
            Result<RuleCondition> condition = request.ToDomain();
            if(condition.IsFailure) return Result.Failure<List<RuleCondition>>(condition.FirstError);
            conditions.Add(condition.Value);
        }
        return conditions;
    }

    public static Result<RuleCondition> ToDomain(this RuleConditionRequest request) {
        switch(request.Type) {
            case "ip_match":
                if(string.IsNullOrWhiteSpace(request.Value))
                    return RuleErrors.ConditionValueMissing(request.Type, nameof(request.Value));
                return new IpMatchCondition(request.Value);

            case "ip_range":
                if(string.IsNullOrWhiteSpace(request.Value))
                    return RuleErrors.ConditionValueMissing(request.Type, nameof(request.Value));
                return new IpRangeMatchCondition(request.Value);

            case "country":
                if(string.IsNullOrWhiteSpace(request.Value))
                    return RuleErrors.ConditionValueMissing(request.Type, nameof(request.Value));
                return new CountryMatchCondition(request.Value.ToUpperInvariant());

            case "asn":
                if(request.Asn is not int asn)
                    return RuleErrors.ConditionValueMissing(request.Type, nameof(request.Asn));
                return new AsnMatchCondition(asn);

            case "path_match":
                if(string.IsNullOrWhiteSpace(request.Value))
                    return RuleErrors.ConditionValueMissing(request.Type, nameof(request.Value));
                PathMatchMode mode = string.Equals(request.Mode, "exact", StringComparison.OrdinalIgnoreCase)
                    ? PathMatchMode.Exact
                    : PathMatchMode.Prefix;
                return new PathMatchCondition(request.Value, mode);

            case "path_regex":
                if(string.IsNullOrWhiteSpace(request.Value))
                    return RuleErrors.ConditionValueMissing(request.Type, nameof(request.Value));
                return new PathRegexMatchCondition(request.Value);

            case "header":
                if(string.IsNullOrWhiteSpace(request.Name))
                    return RuleErrors.ConditionValueMissing(request.Type, nameof(request.Name));
                if(request.Value is null)
                    return RuleErrors.ConditionValueMissing(request.Type, nameof(request.Value));
                return new HeaderMatchCondition(request.Name, request.Value);

            case "user_agent":
                if(string.IsNullOrWhiteSpace(request.Value))
                    return RuleErrors.ConditionValueMissing(request.Type, nameof(request.Value));
                return new UserAgentMatchCondition(request.Value);

            case "method":
                if(string.IsNullOrWhiteSpace(request.Value))
                    return RuleErrors.ConditionValueMissing(request.Type, nameof(request.Value));
                return new MethodMatchCondition(request.Value);

            case "query_regex":
                if(string.IsNullOrWhiteSpace(request.Value))
                    return RuleErrors.ConditionValueMissing(request.Type, nameof(request.Value));
                return new QueryRegexMatchCondition(request.Value);

            case "header_regex":
                if(string.IsNullOrWhiteSpace(request.Name))
                    return RuleErrors.ConditionValueMissing(request.Type, nameof(request.Name));
                if(string.IsNullOrWhiteSpace(request.Value))
                    return RuleErrors.ConditionValueMissing(request.Type, nameof(request.Value));
                return new HeaderRegexMatchCondition(request.Name, request.Value);

            case "body_regex":
                if(string.IsNullOrWhiteSpace(request.Value))
                    return RuleErrors.ConditionValueMissing(request.Type, nameof(request.Value));
                return new BodyRegexMatchCondition(request.Value);

            case "body_json":
                if(string.IsNullOrWhiteSpace(request.Path))
                    return RuleErrors.ConditionValueMissing(request.Type, nameof(request.Path));
                if(string.IsNullOrWhiteSpace(request.Value))
                    return RuleErrors.ConditionValueMissing(request.Type, nameof(request.Value));
                return new BodyJsonMatchCondition(request.Path, request.Value);

            case "body_schema":
                if(string.IsNullOrWhiteSpace(request.Subject))
                    return RuleErrors.ConditionValueMissing(request.Type, nameof(request.Subject));
                if(string.IsNullOrWhiteSpace(request.Version))
                    return RuleErrors.ConditionValueMissing(request.Type, nameof(request.Version));
                return new BodySchemaMatchCondition(request.Subject, request.Version);

            case "ja3":
                if(string.IsNullOrWhiteSpace(request.Value))
                    return RuleErrors.ConditionValueMissing(request.Type, nameof(request.Value));
                return new Ja3MatchCondition(request.Value);

            case "ja4":
                if(string.IsNullOrWhiteSpace(request.Value))
                    return RuleErrors.ConditionValueMissing(request.Type, nameof(request.Value));
                return new Ja4MatchCondition(request.Value);

            default:
                return RuleErrors.ConditionTypeUnknown(request.Type);
        }
    }
}

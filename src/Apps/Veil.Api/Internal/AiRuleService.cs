using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Veil.Analytics.Intelligence;
using Veil.Zones.Domain;
using Veil.Zones.Domain.Enums;
using Veil.Zones.Domain.ValueObjects;
using Veil.Zones.Features.Zones;
using Veil.Zones.Infrastructure.Persistence;

namespace Veil.Api.Internal;

/// <summary>
/// Applies an AI-suggested rule to a zone, in-process against the Zones domain.
/// Shared by the worker-facing Tyto RPC handler (<see cref="ApplyAiRuleHandler"/>,
/// auto-apply) and the dashboard REST endpoint (manual one-click apply), so the
/// zone resolution + condition/action mapping live in exactly one place.
/// </summary>
public sealed class AiRuleService(
    IDbContextFactory<ZonesDbContext> dbFactory,
    IOptions<IntelligenceOptions> options) {

    private readonly IntelligenceOptions _options = options.Value;

    public async Task<ApplyAiRuleResult> ApplyAsync(
        string zone, SuggestedRule rule, bool shadow, CancellationToken cancellationToken) {

        Result<RuleCondition> condition = MapCondition(rule);
        if(condition.IsFailure)
            return new ApplyAiRuleResult(false, "none", $"unmappable condition '{rule.ConditionType}'");

        (RuleAction action, RateLimitConfig? rateLimit) = MapAction(rule.Action, shadow);

        await using ZonesDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Hostnames are stored lower-cased; the edge log zone may differ in case.
        string host = zone.Trim().ToLowerInvariant();
        Zone? target = await db.Zones
            .Include(z => z.Rules)
            .FirstOrDefaultAsync(z => z.Hostname.Value == host, cancellationToken);

        if(target is null)
            return new ApplyAiRuleResult(false, "none", "zone not found in control plane");

        string name = $"AI {(shadow ? "shadow" : "auto")}: {rule.ConditionType}={rule.Value}";
        Result<Rule> created = target.AddRule(name, priority: 50, action, [condition.Value], rateLimit);
        if(created.IsFailure)
            return new ApplyAiRuleResult(false, action.ToString(), created.FirstError.Description);

        await db.SaveChangesAsync(cancellationToken);
        return new ApplyAiRuleResult(true, action.ToString(), null);
    }

    /// <summary>Maps the suggestion's condition vocabulary onto a domain condition.</summary>
    private static Result<RuleCondition> MapCondition(SuggestedRule rule) {
        RuleConditionRequest? mapped = rule.ConditionType switch {
            "ip" => new RuleConditionRequest("ip_match", Value: rule.Value),
            "country" => new RuleConditionRequest("country", Value: rule.Value),
            "asn" when int.TryParse(rule.Value, out int asn) => new RuleConditionRequest("asn", Asn: asn),
            "path_regex" => new RuleConditionRequest("path_regex", Value: rule.Value),
            "user_agent" => new RuleConditionRequest("user_agent", Value: rule.Value),
            _ => null
        };
        return mapped is null
            ? RuleErrors.ConditionTypeUnknown(rule.ConditionType)
            : mapped.ToDomain();
    }

    /// <summary>Shadow → Log (observe-only). Enforce → the real action.</summary>
    private (RuleAction Action, RateLimitConfig? RateLimit) MapAction(string suggested, bool shadow) {
        if(shadow)
            return (RuleAction.Log, null);

        switch(suggested) {
            case "block":
                return (RuleAction.Block, null);
            case "rate_limit":
                Result<RateLimitConfig> limit = RateLimitConfig.Create(
                    this._options.DefaultRateLimitRequests, this._options.DefaultRateLimitWindowSeconds);
                return limit.IsSuccess ? (RuleAction.RateLimit, limit.Value) : (RuleAction.Challenge, null);
            default:
                return (RuleAction.Challenge, null);   // default to the softer action
        }
    }
}

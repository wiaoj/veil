using Microsoft.EntityFrameworkCore;
using Tyto.Rpc;
using Veil.Analytics.Intelligence;
using Veil.Zones.Domain;
using Veil.Zones.Domain.Enums;
using Veil.Zones.Domain.ValueObjects;
using Veil.Zones.Features.Zones;
using Veil.Zones.Infrastructure.Persistence;

namespace Veil.Api.Internal;

/// <summary>
/// Control-plane side of the AI rule-application RPC (Phase 12). Resolves the
/// zone by hostname and creates the rule in-process using the Zones domain,
/// replacing the worker's bespoke HTTP calls. Hosted in Veil.Api so it has
/// direct DB + domain access; reached over Tyto RPC-over-HTTP under /rpc.
///
/// The RPC endpoint sits behind the control plane's default auth policy, so the
/// caller (worker) must present a valid API key — the same protection the old
/// REST path had.
/// </summary>
public sealed class ApplyAiRuleHandler(
    IDbContextFactory<ZonesDbContext> dbFactory,
    Microsoft.Extensions.Options.IOptions<Veil.Analytics.Intelligence.IntelligenceOptions> options)
    : IRpcRequestHandler<ApplyAiRuleRequest, ApplyAiRuleResult> {

    private readonly Veil.Analytics.Intelligence.IntelligenceOptions _options = options.Value;

    public async Task<RpcResult<ApplyAiRuleResult>> HandleAsync(
        ApplyAiRuleRequest request,
        CancellationToken cancellationToken) {

        Result<RuleCondition> condition = MapCondition(request.Rule);
        if(condition.IsFailure)
            return Ok(false, "none", $"unmappable condition '{request.Rule.ConditionType}'");

        (RuleAction action, RateLimitConfig? rateLimit) = MapAction(request.Rule.Action, request.Shadow);

        await using ZonesDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Hostnames are stored lower-cased; the edge log zone may differ in case.
        string host = request.Zone.Trim().ToLowerInvariant();
        Zone? zone = await db.Zones
            .Include(z => z.Rules)
            .FirstOrDefaultAsync(z => z.Hostname.Value == host, cancellationToken);

        if(zone is null)
            return Ok(false, "none", "zone not found in control plane");

        string name = $"AI {(request.Shadow ? "shadow" : "auto")}: {request.Rule.ConditionType}={request.Rule.Value}";
        Result<Rule> rule = zone.AddRule(name, priority: 50, action, [condition.Value], rateLimit);
        if(rule.IsFailure)
            return Ok(false, action.ToString(), rule.FirstError.Description);

        await db.SaveChangesAsync(cancellationToken);
        return Ok(true, action.ToString(), null);
    }

    private static RpcResult<ApplyAiRuleResult> Ok(bool applied, string action, string? reason) =>
        RpcResult<ApplyAiRuleResult>.Success(new ApplyAiRuleResult(applied, action, reason));

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

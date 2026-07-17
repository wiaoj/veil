using Veil.Zones.Domain.Enums;
using Veil.Zones.Domain.Events;
using Veil.Zones.Domain.ValueObjects;

namespace Veil.Zones.Domain;

public sealed class Zone : Aggregate<ZoneId> {
    private readonly List<Rule> _rules = [];

    public Hostname Hostname { get; private set; }
    public UpstreamConfig Upstream { get; private set; }
    public ChallengeConfig Challenge { get; private set; }
    /// <summary>Embeddable self-hosted bot-verification widget credentials. Off by default.</summary>
    public WidgetConfig Widget { get; private set; } = WidgetConfig.Disabled;
    /// <summary>Managed WAF signature toggles (OWASP-CRS-style). Off by default.</summary>
    public ManagedRulesConfig ManagedRules { get; private set; } = ManagedRulesConfig.Disabled;
    /// <summary>Opt-in edge response caching (conservative RFC 7234). Off by default.</summary>
    public bool CacheEnabled { get; private set; }
    /// <summary>Shadow (dry-run) mode: rules are evaluated and logged but not
    /// enforced — every request is forwarded. Off by default.</summary>
    public bool Shadow { get; private set; }
    public IReadOnlyList<Rule> Rules => this._rules.AsReadOnly();
    public ZoneStatus Status { get; private set; }

    private Zone() { }

    public static Result<Zone> Create(
        Hostname hostname,
        UpstreamConfig upstream,
        ChallengeConfig? challenge = null) {
        Zone zone = new() {
            Id = ZoneId.New(),
            Hostname = hostname,
            Upstream = upstream,
            Challenge = challenge ?? ChallengeConfig.Disabled,
            Status = ZoneStatus.Provisioning
        };

        zone.RaiseDomainEvent(new ZoneCreatedDomainEvent(zone.Id));

        return Result<Zone>.Success(zone);
    }

    // ── Rule management ──────────────────────────────────────────────

    public Result<Rule> AddRule(
        string name,
        int priority,
        RuleAction action,
        IReadOnlyList<RuleCondition> conditions,
        RateLimitConfig? rateLimit = null) {
        Result<Rule> result = Rule.Create(name, priority, action, conditions, rateLimit);
        if(!result.IsSuccess)
            return result;

        this._rules.Add(result.Value);
        this._rules.Sort((a, b) => a.Priority.CompareTo(b.Priority));

        RaiseDomainEvent(new ZoneConfigChangedDomainEvent(this.Id));

        return result;
    }

    public Result<Success> RemoveRule(RuleId ruleId) {
        Rule? rule = this._rules.Find(r => r.Id.Equals(ruleId));
        if(rule is null) return Result.Success();

        this._rules.Remove(rule);
        RaiseDomainEvent(new ZoneConfigChangedDomainEvent(this.Id));
        return Result.Success();
    }

    public Result<Rule> UpdateRule(RuleId ruleId, int? priority, bool? isEnabled) {
        Rule? rule = this._rules.Find(r => r.Id.Equals(ruleId));
        if(rule is null) return RuleErrors.NotFound;

        if(priority is < 0) return RuleErrors.PriorityNegative;

        if(priority is int newPriority) rule.UpdatePriority(newPriority);
        if(isEnabled is bool enabled) {
            if(enabled) rule.Enable();
            else rule.Disable();
        }

        this._rules.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        RaiseDomainEvent(new ZoneConfigChangedDomainEvent(this.Id));

        return Result<Rule>.Success(rule);
    }

    /// <summary>
    /// Replaces rule ordering wholesale: <paramref name="orderedRuleIds"/> must
    /// be a permutation of the zone's current rules. Priorities are reassigned
    /// in steps of 10 so individual rules can later be squeezed in between.
    /// </summary>
    public Result<Success> ReorderRules(IReadOnlyList<RuleId> orderedRuleIds) {
        if(orderedRuleIds.Count != this._rules.Count)
            return ZoneErrors.RuleReorderMismatch;

        List<Rule> reordered = new(this._rules.Count);
        foreach(RuleId ruleId in orderedRuleIds) {
            Rule? rule = this._rules.Find(r => r.Id.Equals(ruleId));
            if(rule is null || reordered.Contains(rule))
                return ZoneErrors.RuleReorderMismatch;
            reordered.Add(rule);
        }

        for(int i = 0; i < reordered.Count; i++) {
            reordered[i].UpdatePriority((i + 1) * 10);
        }

        this._rules.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        RaiseDomainEvent(new ZoneConfigChangedDomainEvent(this.Id));

        return Result.Success();
    }

    // ── Status transitions ───────────────────────────────────────────

    public Result<Success> Activate() {
        this.Status = ZoneStatus.Active;
        RaiseDomainEvent(new ZoneConfigChangedDomainEvent(this.Id));
        return Result.Success();
    }
    public Result<Success> Pause() {
        this.Status = ZoneStatus.Paused;
        RaiseDomainEvent(new ZoneConfigChangedDomainEvent(this.Id));
        return Result.Success();
    }
    public Result<Success> Resume() {
        this.Status = ZoneStatus.Active;
        RaiseDomainEvent(new ZoneConfigChangedDomainEvent(this.Id));
        return Result.Success();
    }
    public Result<Success> MarkError() {
        this.Status = ZoneStatus.Error;
        RaiseDomainEvent(new ZoneConfigChangedDomainEvent(this.Id));
        return Result.Success();
    }

    // ── Config updates ───────────────────────────────────────────────

    public Result<Success> UpdateUpstream(UpstreamConfig upstream) {
        this.Upstream = upstream;
        RaiseDomainEvent(new ZoneConfigChangedDomainEvent(this.Id));
        return Result.Success();
    }
    public Result<Success> UpdateCache(bool enabled) {
        this.CacheEnabled = enabled;
        RaiseDomainEvent(new ZoneConfigChangedDomainEvent(this.Id));
        return Result.Success();
    }

    public Result<Success> UpdateManagedRules(ManagedRulesConfig managedRules) {
        this.ManagedRules = managedRules;
        RaiseDomainEvent(new ZoneConfigChangedDomainEvent(this.Id));
        return Result.Success();
    }

    public Result<Success> UpdateShadow(bool shadow) {
        this.Shadow = shadow;
        RaiseDomainEvent(new ZoneConfigChangedDomainEvent(this.Id));
        return Result.Success();
    }

    public Result<Success> UpdateChallenge(ChallengeConfig challenge) {
        this.Challenge = challenge;
        RaiseDomainEvent(new ZoneConfigChangedDomainEvent(this.Id));
        return Result.Success();
    }

    public Result<Success> UpdateWidget(WidgetConfig widget) {
        this.Widget = widget;
        RaiseDomainEvent(new ZoneConfigChangedDomainEvent(this.Id));
        return Result.Success();
    }

    /// <summary>
    /// Signals that the zone is being removed. Raised before the aggregate is
    /// deleted so the edge fleet drops it on the next config push. A removed
    /// zone is a config change for every node, so it maps onto the shared
    /// ZoneConfigChanged integration event.
    /// </summary>
    public Result<Success> MarkDeleted() {
        RaiseDomainEvent(new ZoneDeletedDomainEvent(this.Id));
        return Result.Success();
    }
}
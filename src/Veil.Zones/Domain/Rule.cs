using Veil.Zones.Domain.Enums;
using Veil.Zones.Domain.ValueObjects;
using Wiaoj.Ddd;

namespace Veil.Zones.Domain;

public sealed class Rule : Entity<RuleId> {
    public string Name { get; private set; }
    public int Priority { get; private set; }
    public RuleAction Action { get; private set; }
    public IReadOnlyList<RuleCondition> Conditions { get; private set; }
    public RateLimitConfig? RateLimit { get; private set; }
    public bool IsEnabled { get; private set; }

    private Rule() { }

    internal static Result<Rule> Create(
        string name,
        int priority,
        RuleAction action,
        IReadOnlyList<RuleCondition> conditions,
        RateLimitConfig? rateLimit = null,
        bool isEnabled = true) {
        if(string.IsNullOrWhiteSpace(name))
            return RuleErrors.NameEmpty;

        if(priority < 0)
            return RuleErrors.PriorityNegative;

        if(conditions is null || conditions.Count == 0)
            return RuleErrors.ConditionsEmpty;

        if(action is RuleAction.RateLimit && rateLimit is null)
            return RuleErrors.RateLimitConfigMissing;

        if(action is not RuleAction.RateLimit && rateLimit is not null)
            return RuleErrors.RateLimitConfigNotAllowed;

        Rule rule = new() {
            Id = RuleId.New(),
            Name = name,
            Priority = priority,
            Action = action,
            Conditions = conditions,
            RateLimit = rateLimit,
            IsEnabled = isEnabled
        };

        return rule;
    }

    public void Enable() {
        this.IsEnabled = true;
    }

    public void Disable() {
        this.IsEnabled = false;
    }

    public void UpdatePriority(int priority) {
        if(priority < 0) return;
        this.Priority = priority;
    }
}
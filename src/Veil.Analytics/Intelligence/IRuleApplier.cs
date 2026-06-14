using Microsoft.Extensions.Logging;

namespace Veil.Analytics.Intelligence;

/// <summary>
/// Applies an AI-suggested rule. The worker process owns the live traffic
/// stream, but the Zones aggregate lives in Veil.Api — so a production
/// implementation calls the control plane over an internal API. Whether a rule
/// is enforced or staged in shadow mode is decided by the caller.
/// </summary>
public interface IRuleApplier {
    Task ApplyAsync(string zone, SuggestedRule rule, bool shadow, CancellationToken cancellationToken);
}

/// <summary>
/// Prototype applier: records the decision instead of mutating zones. Swap for
/// an HTTP client to Veil.Api's internal rule endpoint to close the loop.
/// </summary>
public sealed class LoggingRuleApplier(ILogger<LoggingRuleApplier> logger) : IRuleApplier {
    public Task ApplyAsync(string zone, SuggestedRule rule, bool shadow, CancellationToken cancellationToken) {
        logger.LogWarning(
            "AI rule {Mode} for zone {Zone}: {Condition}={Value} → {Action} (not yet wired to Veil.Api)",
            shadow ? "SHADOW" : "ENFORCE", zone, rule.ConditionType, rule.Value, rule.Action);
        return Task.CompletedTask;
    }
}

using Veil.Zones.Domain.Enums;
using Veil.Zones.Domain.ValueObjects;

namespace Veil.Zones.Infrastructure.Persistence;

// Persistence shapes for jsonb columns. The domain value objects are
// deliberately not (de)serializable — they have private constructors and
// validated factories — so these records define the stored JSON layout and
// rebuild the domain types via their internal Restore factories.

internal sealed record ManagedRulesData(
    bool SqlInjection,
    bool Xss,
    bool PathTraversal,
    bool InspectBody,
    ManagedRuleAction Action) {

    public static ManagedRulesData FromDomain(ManagedRulesConfig config) {
        return new ManagedRulesData(
            config.SqlInjection, config.Xss, config.PathTraversal, config.InspectBody, config.Action);
    }

    public ManagedRulesConfig ToDomain() {
        return ManagedRulesConfig.Restore(
            this.SqlInjection, this.Xss, this.PathTraversal, this.InspectBody, this.Action);
    }
}

internal sealed record UpstreamTargetData(string Url, int Weight);

internal sealed record UpstreamConfigData(
    List<UpstreamTargetData> Targets,
    LoadBalanceStrategy Strategy,
    double ConnectTimeoutMs,
    double ResponseTimeoutMs,
    bool PassHostHeader) {

    public static UpstreamConfigData FromDomain(UpstreamConfig config) {
        return new UpstreamConfigData(
            config.Targets.Select(t => new UpstreamTargetData(t.Url.ToString(), t.Weight)).ToList(),
            config.Strategy,
            config.ConnectTimeout.TotalMilliseconds,
            config.ResponseTimeout.TotalMilliseconds,
            config.PassHostHeader);
    }

    public UpstreamConfig ToDomain() {
        return UpstreamConfig.Restore(
            this.Targets.Select(t => new UpstreamTarget(new Uri(t.Url), t.Weight)).ToList(),
            this.Strategy,
            TimeSpan.FromMilliseconds(this.ConnectTimeoutMs),
            TimeSpan.FromMilliseconds(this.ResponseTimeoutMs),
            this.PassHostHeader);
    }
}

internal sealed record RateLimitData(int Requests, int WindowSecs) {
    public static RateLimitData FromDomain(RateLimitConfig config) {
        return new RateLimitData(config.Requests, config.WindowSecs);
    }

    public RateLimitConfig ToDomain() {
        return RateLimitConfig.Restore(this.Requests, this.WindowSecs);
    }
}

internal sealed record ChallengeConfigData(
    bool Enabled,
    int PowDifficulty,
    double TokenTtlSeconds,
    bool RequireCaptchaOnHighRisk,
    int? RiskThreshold = null) {

    public static ChallengeConfigData FromDomain(ChallengeConfig config) {
        return new ChallengeConfigData(
            config.Enabled,
            config.PowDifficulty.Value,
            config.TokenTtl.Value.TotalSeconds,
            config.RequireCaptchaOnHighRisk,
            config.RiskThreshold);
    }

    public ChallengeConfig ToDomain() {
        return ChallengeConfig.Restore(
            this.Enabled,
            this.PowDifficulty,
            TimeSpan.FromSeconds(this.TokenTtlSeconds),
            this.RequireCaptchaOnHighRisk,
            // Rows written before RiskThreshold existed default to 70.
            this.RiskThreshold ?? 70);
    }
}

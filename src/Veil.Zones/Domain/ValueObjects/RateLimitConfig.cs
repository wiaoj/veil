namespace Veil.Zones.Domain.ValueObjects;

/// <summary>
/// Rate limit kuralı parametreleri. Yalnızca <see cref="RuleAction.RateLimit"/>
/// action'ına sahip kurallarda kullanılır.
/// </summary>
public sealed record RateLimitConfig {
    public int Requests { get; }
    public int WindowSecs { get; }

    private RateLimitConfig(int requests, int windowSecs) {
        this.Requests = requests;
        this.WindowSecs = windowSecs;
    }

    /// <summary>
    /// Persistence-only factory: trusts previously validated data coming back
    /// from the database.
    /// </summary>
    internal static RateLimitConfig Restore(int requests, int windowSecs) {
        return new RateLimitConfig(requests, windowSecs);
    }

    public static Result<RateLimitConfig> Create(int requests, int windowSecs) {
        if(requests < 1)
            return RuleErrors.RateLimitRequestsInvalid;

        if(windowSecs < 1)
            return RuleErrors.RateLimitWindowInvalid;

        if(windowSecs > 86400)
            return RuleErrors.RateLimitWindowTooLarge;

        return new RateLimitConfig(requests, windowSecs);
    }
}
using Veil.Zones.Domain.Enums;

namespace Veil.Zones.Domain.ValueObjects;

/// <summary>
/// Origin sunucu yapılandırması. Birden fazla target destekler;
/// edge node, strategy'ye göre load-balance yapar.
/// </summary>
public sealed class UpstreamConfig {
    public IReadOnlyList<UpstreamTarget> Targets { get; }
    public LoadBalanceStrategy Strategy { get; }
    public TimeSpan ConnectTimeout { get; }
    public TimeSpan ResponseTimeout { get; }
    public bool PassHostHeader { get; }

    private UpstreamConfig(
        IReadOnlyList<UpstreamTarget> targets,
        LoadBalanceStrategy strategy,
        TimeSpan connectTimeout,
        TimeSpan responseTimeout,
        bool passHostHeader) {
        this.Targets = targets;
        this.Strategy = strategy;
        this.ConnectTimeout = connectTimeout;
        this.ResponseTimeout = responseTimeout;
        this.PassHostHeader = passHostHeader;
    }

    /// <summary>
    /// Persistence-only factory: trusts previously validated data coming back
    /// from the database and bypasses <see cref="Create"/> validation.
    /// </summary>
    internal static UpstreamConfig Restore(
        IReadOnlyList<UpstreamTarget> targets,
        LoadBalanceStrategy strategy,
        TimeSpan connectTimeout,
        TimeSpan responseTimeout,
        bool passHostHeader) {
        return new UpstreamConfig(targets, strategy, connectTimeout, responseTimeout, passHostHeader);
    }

    public static Result<UpstreamConfig> Create(
        IReadOnlyList<UpstreamTarget> targets,
        LoadBalanceStrategy strategy = LoadBalanceStrategy.RoundRobin,
        TimeSpan? connectTimeout = null,
        TimeSpan? responseTimeout = null,
        bool passHostHeader = true) {
        if(targets is null || targets.Count == 0)
            return ZoneErrors.UpstreamEmpty;

        foreach(UpstreamTarget target in targets) {
            if(target.Url.Scheme is not ("http" or "https"))
                return ZoneErrors.UpstreamInvalidScheme(target.Url.ToString());

            if(target.Weight < 1)
                return ZoneErrors.UpstreamInvalidWeight;
        }

        return Result<UpstreamConfig>.Success(new UpstreamConfig(
            targets,
            strategy,
            connectTimeout ?? TimeSpan.FromSeconds(5),
            responseTimeout ?? TimeSpan.FromSeconds(30),
            passHostHeader));
    }
}

public sealed record UpstreamTarget(Uri Url, int Weight = 1);
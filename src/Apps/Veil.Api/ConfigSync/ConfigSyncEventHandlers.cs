using Tyto;
using Veil.EdgeNodes.Contracts.IntegrationEvents;
using Veil.Zones.Contracts.IntegrationEvents;

namespace Veil.Api.ConfigSync;

/// <summary>
/// Bus subscriptions driving the config push loop. Both events resolve to
/// the same coalescing signal: the loop's per-node snapshot-hash dedupe
/// makes the wake-up idempotent — a zone change re-pushes everyone, a fresh
/// node registration only actually pushes to the node without a hash entry.
/// </summary>
public sealed class ZoneConfigChangedHandler(
    ConfigPushSignal signal,
    ILogger<ZoneConfigChangedHandler> logger) : IEventHandler<ZoneConfigChanged> {

    public ValueTask HandleAsync(IMessageContext<ZoneConfigChanged> context, CancellationToken cancellationToken = default) {
        logger.LogDebug("Zone {ZoneId} config changed; waking push loop", context.Message.ZoneId);
        signal.Notify();
        return ValueTask.CompletedTask;
    }
}

public sealed class EdgeNodeRegisteredHandler(
    ConfigPushSignal signal,
    ILogger<EdgeNodeRegisteredHandler> logger) : IEventHandler<EdgeNodeRegistered> {

    public ValueTask HandleAsync(IMessageContext<EdgeNodeRegistered> context, CancellationToken cancellationToken = default) {
        logger.LogInformation("Edge node {NodeId} registered; pushing initial config", context.Message.NodeId);
        signal.Notify();
        return ValueTask.CompletedTask;
    }
}

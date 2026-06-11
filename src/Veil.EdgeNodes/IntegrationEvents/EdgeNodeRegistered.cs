using Tyto;
using Veil.EdgeNodes.Domain.Events;
using Veil.EdgeNodes.Domain.ValueObjects;
using Veil.Shared;

namespace Veil.EdgeNodes.IntegrationEvents;

/// <summary>
/// Published on the bus when a new edge node registers. ConfigSync handles
/// it to push the initial zone snapshot immediately instead of waiting for
/// the node's startup pull or the next reconcile pass.
/// </summary>
[Message("edge-nodes.registered", 1)]
public sealed record EdgeNodeRegistered(string NodeId, DateTimeOffset OccurredAtUtc) : IEvent;

public sealed class EdgeNodeRegisteredMapper(IObfuscator<EdgeNodeId> obfuscator)
    : IIntegrationEventMapper<EdgeNodeRegisteredDomainEvent, EdgeNodeRegistered> {
    public EdgeNodeRegistered Map(EdgeNodeRegisteredDomainEvent @event) {
        return new EdgeNodeRegistered(obfuscator.Encode(@event.EdgeNodeId), @event.OccurredAt);
    }
}

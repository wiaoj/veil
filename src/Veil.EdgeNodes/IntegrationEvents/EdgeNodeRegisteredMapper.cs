using Veil.EdgeNodes.Contracts.IntegrationEvents;
using Veil.EdgeNodes.Domain.Events;
using Veil.EdgeNodes.Domain.ValueObjects;
using Veil.Shared;

namespace Veil.EdgeNodes.IntegrationEvents;

/// <summary>
/// Domain event → integration event mapper, picked up by the module's
/// AddTytoIntegration scan and auto-published post-commit. The event itself
/// lives in Veil.EdgeNodes.Contracts.
/// </summary>
public sealed class EdgeNodeRegisteredMapper(IObfuscator<EdgeNodeId> obfuscator)
    : IIntegrationEventMapper<EdgeNodeRegisteredDomainEvent, EdgeNodeRegistered> {
    public EdgeNodeRegistered Map(EdgeNodeRegisteredDomainEvent @event) {
        return new EdgeNodeRegistered(obfuscator.Encode(@event.EdgeNodeId), @event.OccurredAt);
    }
}

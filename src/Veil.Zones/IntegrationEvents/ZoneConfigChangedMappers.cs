using Veil.Shared;
using Veil.Zones.Contracts.IntegrationEvents;
using Veil.Zones.Domain.Events;
using Veil.Zones.Domain.ValueObjects;

namespace Veil.Zones.IntegrationEvents;

// Domain event → integration event mappers, picked up by the module's
// AddTytoIntegration scan and auto-published post-commit. The events
// themselves live in Veil.Zones.Contracts — consumers depend on that, not
// on this module.

public sealed class ZoneConfigChangedMapper(IObfuscator<ZoneId> obfuscator)
    : IIntegrationEventMapper<ZoneConfigChangedDomainEvent, ZoneConfigChanged> {
    public ZoneConfigChanged Map(ZoneConfigChangedDomainEvent @event) {
        return new ZoneConfigChanged(obfuscator.Encode(@event.ZoneId), @event.OccurredAt);
    }
}

/// <summary>
/// A new zone is by definition a config change for the edge fleet, so
/// creation maps onto the same integration event.
/// </summary>
public sealed class ZoneCreatedMapper(IObfuscator<ZoneId> obfuscator)
    : IIntegrationEventMapper<ZoneCreatedDomainEvent, ZoneConfigChanged> {
    public ZoneConfigChanged Map(ZoneCreatedDomainEvent @event) {
        return new ZoneConfigChanged(obfuscator.Encode(@event.ZoneId), @event.OccurredAt);
    }
}

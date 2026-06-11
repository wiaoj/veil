using Tyto;
using Veil.Shared;
using Veil.Zones.Domain.Events;
using Veil.Zones.Domain.ValueObjects;

namespace Veil.Zones.IntegrationEvents;

/// <summary>
/// Published on the bus whenever a zone's effective edge configuration
/// changes (creation included). Carries the public (obfuscated) zone id —
/// integration events are cross-module contracts and never leak raw ids.
/// </summary>
[Message("zones.config-changed", 1)]
public sealed record ZoneConfigChanged(string ZoneId, DateTimeOffset OccurredAtUtc) : IEvent;

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

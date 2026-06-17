using Veil.Zones.Domain.ValueObjects;
using Wiaoj.Ddd.DomainEvents;

namespace Veil.Zones.Domain.Events;

/// <summary>
/// Raised when a zone is deleted. The config-sync pipeline reacts by
/// re-pushing the (now smaller) snapshot so edge nodes drop the zone.
/// </summary>
public sealed record ZoneDeletedDomainEvent(ZoneId ZoneId) : DomainEvent;

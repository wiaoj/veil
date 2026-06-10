using Veil.Zones.Domain.ValueObjects;
using Wiaoj.Ddd.DomainEvents;

namespace Veil.Zones.Domain.Events;

public sealed record ZoneCreatedDomainEvent(ZoneId ZoneId) : DomainEvent;
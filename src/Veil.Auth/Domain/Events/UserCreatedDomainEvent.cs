using Veil.Auth.Domain.ValueObjects;

namespace Veil.Auth.Domain.Events;

public sealed record UserCreatedDomainEvent(UserId UserId) : DomainEvent;

using Veil.EdgeNodes.Domain.ValueObjects;

namespace Veil.EdgeNodes.Domain.Events;

/// <summary>
/// Raised when a new edge node registers. ConfigSync subscribes to push the
/// initial zone snapshot to the node.
/// </summary>
public sealed record EdgeNodeRegisteredDomainEvent(EdgeNodeId EdgeNodeId) : DomainEvent;

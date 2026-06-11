using Tyto;

namespace Veil.EdgeNodes.Contracts.IntegrationEvents;

/// <summary>
/// Published on the bus when a new edge node registers. ConfigSync handles
/// it to push the initial zone snapshot immediately instead of waiting for
/// the node's startup pull or the next reconcile pass.
/// </summary>
[Message("edge-nodes.registered", 1)]
public sealed record EdgeNodeRegistered(string NodeId, DateTimeOffset OccurredAtUtc) : IEvent;

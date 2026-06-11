using Veil.EdgeNodes.Domain.Enums;
using Veil.EdgeNodes.Domain.Events;
using Veil.EdgeNodes.Domain.ValueObjects;

namespace Veil.EdgeNodes.Domain;

/// <summary>
/// A registered edge node. The control plane pushes zone config snapshots to
/// <see cref="Address"/>; the node authenticates itself with the token whose
/// SHA-256 hash is stored in <see cref="TokenHash"/> — the plaintext token is
/// only shown once at registration.
/// </summary>
public sealed class EdgeNode : Aggregate<EdgeNodeId> {
    public string Name { get; private set; }
    public Uri Address { get; private set; }
    public string TokenHash { get; private set; }
    public EdgeNodeStatus Status { get; private set; }
    public DateTimeOffset RegisteredAtUtc { get; private set; }
    public DateTimeOffset? LastSeenAtUtc { get; private set; }

    private EdgeNode() { }

    public static Result<EdgeNode> Register(
        string name,
        Uri address,
        string tokenHash,
        DateTimeOffset registeredAtUtc) {
        if(string.IsNullOrWhiteSpace(name))
            return EdgeNodeErrors.NameEmpty;

        if(!address.IsAbsoluteUri || address.Scheme is not ("http" or "https"))
            return EdgeNodeErrors.AddressInvalid(address.ToString());

        if(string.IsNullOrWhiteSpace(tokenHash))
            return EdgeNodeErrors.TokenHashEmpty;

        EdgeNode node = new() {
            Id = EdgeNodeId.New(),
            Name = name.Trim(),
            Address = address,
            TokenHash = tokenHash,
            Status = EdgeNodeStatus.Registered,
            RegisteredAtUtc = registeredAtUtc
        };

        node.RaiseDomainEvent(new EdgeNodeRegisteredDomainEvent(node.Id));

        return Result<EdgeNode>.Success(node);
    }

    /// <summary>Records a successful contact from the node (config pull, heartbeat).</summary>
    public Result<Success> MarkSeen(DateTimeOffset seenAtUtc) {
        this.LastSeenAtUtc = seenAtUtc;
        if(this.Status is EdgeNodeStatus.Registered)
            this.Status = EdgeNodeStatus.Active;
        return Result.Success();
    }

    public Result<Success> Disable() {
        this.Status = EdgeNodeStatus.Disabled;
        return Result.Success();
    }

    public Result<Success> Enable() {
        this.Status = this.LastSeenAtUtc is null
            ? EdgeNodeStatus.Registered
            : EdgeNodeStatus.Active;
        return Result.Success();
    }
}
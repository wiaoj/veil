using Veil.Shared;

namespace Veil.EdgeNodes.Domain.ValueObjects;

public readonly record struct EdgeNodeId : IPrefixedId<EdgeNodeId> {
    public static string Prefix => "edg";

    public SnowflakeId Value { get; }

    private EdgeNodeId(SnowflakeId value) {
        this.Value = value;
    }

    public static EdgeNodeId From(SnowflakeId value) {
        return new(value);
    }

    public static EdgeNodeId New() {
        return new(SnowflakeId.NewId());
    }
}
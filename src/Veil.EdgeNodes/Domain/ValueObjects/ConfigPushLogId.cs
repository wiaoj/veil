using Veil.Shared;

namespace Veil.EdgeNodes.Domain.ValueObjects;

public readonly struct ConfigPushLogId : IPrefixedId<ConfigPushLogId> {
    public static string Prefix => "cpl";

    public SnowflakeId Value { get; }

    private ConfigPushLogId(SnowflakeId value) {
        this.Value = value;
    }

    public static ConfigPushLogId From(SnowflakeId value) {
        return new(value);
    }

    public static ConfigPushLogId New() {
        return new(SnowflakeId.NewId());
    }
}

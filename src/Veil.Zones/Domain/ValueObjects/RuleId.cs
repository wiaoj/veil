using Veil.Shared;

namespace Veil.Zones.Domain.ValueObjects;

public readonly struct RuleId : IPrefixedId<RuleId> {
    public static string Prefix => "rul";

    public SnowflakeId Value { get; }

    private RuleId(SnowflakeId value) {
        this.Value = value;
    }

    public static RuleId From(SnowflakeId value) {
        return new(value);
    }

    public static RuleId New() {
        return new(SnowflakeId.NewId());
    }
}
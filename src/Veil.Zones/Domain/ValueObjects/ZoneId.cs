using Veil.Shared;

namespace Veil.Zones.Domain.ValueObjects;

public readonly struct ZoneId : IPrefixedId<ZoneId> {
    public static string Prefix => "zon";

    public SnowflakeId Value { get; }

    private ZoneId(SnowflakeId value) {
        this.Value = value;
    }

    public static ZoneId From(SnowflakeId value) {
        return new(value);
    }

    public static ZoneId New() {
        return new(SnowflakeId.NewId());
    }
}
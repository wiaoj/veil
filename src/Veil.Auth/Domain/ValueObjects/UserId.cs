using Veil.Shared;

namespace Veil.Auth.Domain.ValueObjects;

public readonly struct UserId : IPrefixedId<UserId> {
    public static string Prefix => "usr";

    public SnowflakeId Value { get; }

    private UserId(SnowflakeId value) {
        this.Value = value;
    }

    public static UserId From(SnowflakeId value) {
        return new(value);
    }

    public static UserId New() {
        return new(SnowflakeId.NewId());
    }
}

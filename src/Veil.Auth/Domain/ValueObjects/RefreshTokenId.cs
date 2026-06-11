using Veil.Shared;

namespace Veil.Auth.Domain.ValueObjects;

public readonly struct RefreshTokenId : IPrefixedId<RefreshTokenId> {
    public static string Prefix => "rft";

    public SnowflakeId Value { get; }

    private RefreshTokenId(SnowflakeId value) {
        this.Value = value;
    }

    public static RefreshTokenId From(SnowflakeId value) {
        return new(value);
    }

    public static RefreshTokenId New() {
        return new(SnowflakeId.NewId());
    }
}

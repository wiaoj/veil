using Veil.Shared;

namespace Veil.Auth.Domain.ValueObjects;

public readonly struct ApiKeyId : IPrefixedId<ApiKeyId> {
    public static string Prefix => "key";

    public SnowflakeId Value { get; }

    private ApiKeyId(SnowflakeId value) {
        this.Value = value;
    }

    public static ApiKeyId From(SnowflakeId value) {
        return new(value);
    }

    public static ApiKeyId New() {
        return new(SnowflakeId.NewId());
    }
}

using System.Text.Json.Serialization;

#pragma warning disable IDE0130
namespace Veil.Shared;
#pragma warning restore IDE0130

/// <summary>
/// Wire-form representation of an internal ID after obfuscation. Carries
/// the full <c>{prefix}_{opaque}</c> string. Domain code never sees this —
/// only HTTP DTOs / JWT claims / URL parameters do.
/// </summary>
[JsonConverter(typeof(ObfuscatedIdJsonConverter))]
public readonly record struct ObfuscatedId(string Value) {
    public override string ToString() {
        return this.Value;
    }

    public static bool TryParse(string? s, out ObfuscatedId result) {
        if(string.IsNullOrWhiteSpace(s)) {
            result = default;
            return false;
        }
        result = new ObfuscatedId(s);
        return true;
    }

    public static implicit operator string(ObfuscatedId id) {
        return id.Value;
    }

    public static implicit operator ObfuscatedId(string value) {
        return new(value);
    }
}
using System.Diagnostics.CodeAnalysis;
using Wiaoj.Ddd.ValueObjects;
using Wiaoj.Primitives.Snowflake;
using Wiaoj.Results;

namespace Veil.Shared.Obfuscation;

/// <summary>
/// Debug-mode obfuscator: emits <c>{prefix}_{snowflakeLong}</c> with no
/// masking. The snowflake value is human-readable, so it's easy to grep
/// logs and DB rows for the same id during development. Wire shape stays
/// identical to the real obfuscator (prefix + underscore + payload) so
/// client code doesn't see a difference between dev and prod.
/// </summary>
internal sealed class TransparentObfuscator<TId> : IObfuscator<TId> where TId : IId<TId, SnowflakeId>, IPrefixedId<TId> {

    public ObfuscatedId Encode(TId id) {
        if(id.Value.Value == 0) return new ObfuscatedId($"{TId.Prefix}_0");
        return new ObfuscatedId($"{TId.Prefix}_{id.Value.Value}");
    }

    public Result<TId> Decode(ObfuscatedId obfuscatedId) {
        string opaqueId = obfuscatedId.Value;
        if(string.IsNullOrWhiteSpace(opaqueId))
            return Error.Validation("Obfuscation.EmptyId", "ID cannot be empty.");

        int sepIdx = opaqueId.IndexOf('_');
        if(sepIdx < 0)
            return Error.Validation("Obfuscation.MissingPrefix", "Missing prefix separator.");

        ReadOnlySpan<char> prefix = opaqueId.AsSpan(0, sepIdx);
        if(!prefix.SequenceEqual(TId.Prefix))
            return Error.Validation("Obfuscation.PrefixMismatch",
                $"Expected prefix '{TId.Prefix}', got '{prefix}'.");

        ReadOnlySpan<char> payload = opaqueId.AsSpan(sepIdx + 1);
        if(payload is "0") return TId.From(new SnowflakeId(0));

        if(long.TryParse(payload, out long rawId)) {
            return TId.From(new SnowflakeId(rawId));
        }

        return Error.Validation("Obfuscation.InvalidId", "Numeric value expected after prefix.");
    }

    public bool TryDecode(ObfuscatedId obfuscatedId, [NotNullWhen(true)] out TId? id) {
        id = default;
        string value = obfuscatedId.Value;
        if(string.IsNullOrWhiteSpace(value)) return false;

        int sepIdx = value.IndexOf('_');
        if(sepIdx < 0) return false;

        ReadOnlySpan<char> prefix = value.AsSpan(0, sepIdx);
        if(!prefix.SequenceEqual(TId.Prefix)) return false;

        ReadOnlySpan<char> payload = value.AsSpan(sepIdx + 1);
        if(payload is "0") {
            id = TId.From(new SnowflakeId(0));
            return true;
        }

        if(long.TryParse(payload, out long rawId)) {
            id = TId.From(new SnowflakeId(rawId));
            return true;
        }

        return false;
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Wiaoj.Ddd.ValueObjects;
using Wiaoj.Primitives.Cryptography.Hashing;
using Wiaoj.Primitives.Obfuscation;
using Wiaoj.Primitives.Snowflake;
using Wiaoj.Results;

namespace Veil.Shared.Obfuscation;

// =============================================================================
// Obfuscator<TId> — production obfuscator
// =============================================================================
//
// Per-type tweak: MD5 of the ID's fully-qualified type name → Int128. XOR'd
// with the snowflake before passing to the master obfuscator
// (Wiaoj.Primitives' FeistelBase62Obfuscator) so two different ID types with
// the same underlying number produce different opaque strings.
//
// Wire format: <c>{TId.Prefix}_{base62-opaque}</c>. The prefix is validated
// on Decode/TryDecode — a wire string carrying the wrong prefix is rejected
// before the master cipher runs.
// =============================================================================

internal sealed class Obfuscator<TId>(IObfuscator masterObfuscator) : IObfuscator<TId>
    where TId : IId<TId, SnowflakeId>, IPrefixedId<TId> {

    private static readonly Int128 _tweak;

    static Obfuscator() {
        ReadOnlySpan<byte> typeNameBytes = MemoryMarshal.AsBytes(
            (typeof(TId).FullName ?? typeof(TId).Name).AsSpan());

        Md5Hash hashBytes = Md5Hash.Compute(typeNameBytes);
        _tweak = Unsafe.As<Md5Hash, Int128>(ref hashBytes);
    }

    public ObfuscatedId Encode(TId id) {
        if(id.Value.Value == 0)
            return new ObfuscatedId($"{TId.Prefix}_0");

        Int128 valueToObfuscate = (Int128)(ulong)id.Value.Value ^ _tweak;

        Span<char> buffer = stackalloc char[32];
        if(masterObfuscator.TryEncode(valueToObfuscate, buffer, out int written)) {
            return new ObfuscatedId($"{TId.Prefix}_{buffer[..written]}");
        }
        return new ObfuscatedId(string.Empty);
    }

    public Result<TId> Decode(ObfuscatedId obfuscatedId) {
        string opaqueId = obfuscatedId.Value;
        if(string.IsNullOrWhiteSpace(opaqueId))
            return Error.UnprocessableEntity("UNKNOWN_ID", "ID cannot be empty.");

        int sepIdx = opaqueId.IndexOf('_');
        if(sepIdx < 0)
            return Error.Validation("ID_FORMAT", "Missing prefix separator.");

        ReadOnlySpan<char> prefix = opaqueId.AsSpan(0, sepIdx);
        if(!prefix.SequenceEqual(TId.Prefix))
            return Error.Validation("ID_TYPE_MISMATCH",
                $"Expected prefix '{TId.Prefix}', got '{prefix}'.");

        ReadOnlySpan<char> payload = opaqueId.AsSpan(sepIdx + 1);
        if(payload is "0") return TId.From(new SnowflakeId(0));

        if(masterObfuscator.TryDecode(payload, out Int128 rawValue)) {
            Int128 deobfuscated = rawValue ^ _tweak;
            return TId.From(new SnowflakeId((long)(ulong)deobfuscated));
        }

        return Error.Validation("UNKNOWN_ID", "Invalid or corrupted ID format.");
    }

    public bool TryDecode(ObfuscatedId obfuscatedId, [NotNullWhen(true)] out TId? id) {
        id = default;
        string opaqueId = obfuscatedId.Value;
        if(string.IsNullOrWhiteSpace(opaqueId)) return false;

        int sepIdx = opaqueId.IndexOf('_');
        if(sepIdx < 0) return false;

        ReadOnlySpan<char> prefix = opaqueId.AsSpan(0, sepIdx);
        if(!prefix.SequenceEqual(TId.Prefix)) return false;

        ReadOnlySpan<char> payload = opaqueId.AsSpan(sepIdx + 1);
        if(payload is "0") {
            id = TId.From(new SnowflakeId(0));
            return true;
        }

        if(masterObfuscator.TryDecode(payload, out Int128 rawValue)) {
            Int128 deobfuscated = rawValue ^ _tweak;
            id = TId.From(new SnowflakeId((long)(ulong)deobfuscated));
            return true;
        }

        return false;
    }
}

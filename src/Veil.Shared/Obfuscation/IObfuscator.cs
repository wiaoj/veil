using System.Diagnostics.CodeAnalysis;
using Wiaoj.Ddd;
using Wiaoj.Ddd.ValueObjects;

#pragma warning disable IDE0130
namespace Veil.Shared;
#pragma warning restore IDE0130

/// <summary>
/// Per-type obfuscator — encodes a Snowflake-backed ID into a stable opaque
/// wire string (<c>{prefix}_{opaque}</c>) and back. Each <typeparamref name="TId"/>
/// gets its own derived tweak so two different ID types with the same
/// underlying number encode to different opaque strings; prefix validation
/// on decode prevents cross-type substitution attacks.
/// </summary>
public interface IObfuscator<TId> where TId : IId<TId, SnowflakeId>, IPrefixedId<TId> {

    ObfuscatedId Encode(TId id);

    ObfuscatedId EncodeId<TAggregate>(TAggregate aggregate) where TAggregate : Aggregate<TId> {
        return Encode(aggregate.Id);
    }

    Result<TId> Decode(ObfuscatedId obfuscatedId);

    bool TryDecode(ObfuscatedId obfuscatedId, [NotNullWhen(true)] out TId? id);
}
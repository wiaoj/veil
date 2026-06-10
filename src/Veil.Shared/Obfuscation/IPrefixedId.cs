using Wiaoj.Ddd.ValueObjects;
using Wiaoj.Primitives.Snowflake;

#pragma warning disable IDE0130
namespace Veil.Shared;
#pragma warning restore IDE0130

/// <summary>
/// Marker for strongly-typed IDs that carry a stable wire-format prefix
/// (e.g. "flw" for SignInFlowId, "ses" for SessionId). The obfuscator
/// emits <c>{Prefix}_{opaque}</c> on encode and validates the prefix on
/// decode so a cross-type ID substitution is a compile-time impossibility
/// AND a runtime rejection.
/// </summary>
public interface IPrefixedId<TSelf> : IId<TSelf, SnowflakeId> where TSelf : IPrefixedId<TSelf> {
    static abstract string Prefix { get; }
}
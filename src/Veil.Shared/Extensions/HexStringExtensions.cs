using System.Security.Cryptography;

#pragma warning disable IDE0130
namespace Wiaoj.Primitives;
#pragma warning restore IDE0130

public static class HexStringExtensions {
    /// <summary>
    /// Performs a constant-time comparison of two hex strings by converting them to byte arrays.
    /// This prevents timing attacks when validating cryptographic hashes.
    /// </summary>
    public static bool FixedTimeEquals(this HexString left, HexString right) {
        return CryptographicOperations.FixedTimeEquals(left.ToBytes(), right.ToBytes());
    }
}
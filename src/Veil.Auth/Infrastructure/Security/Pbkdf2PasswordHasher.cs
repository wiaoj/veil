using System.Security.Cryptography;

namespace Veil.Auth.Infrastructure.Security;

/// <summary>
/// PBKDF2-SHA256 password hashing. Encoded format:
/// <c>pbkdf2-sha256$&lt;iterations&gt;$&lt;saltBase64&gt;$&lt;hashBase64&gt;</c> —
/// iterations travel with the hash so they can be raised without breaking
/// existing credentials.
/// </summary>
public static class Pbkdf2PasswordHasher {
    private const int Iterations = 210_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static string Hash(string password) {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string encoded) {
        string[] parts = encoded.Split('$');
        if(parts is not ["pbkdf2-sha256", _, _, _])
            return false;

        if(!int.TryParse(parts[1], out int iterations) || iterations < 1)
            return false;

        byte[] salt;
        byte[] expected;
        try {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch(FormatException) {
            return false;
        }

        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

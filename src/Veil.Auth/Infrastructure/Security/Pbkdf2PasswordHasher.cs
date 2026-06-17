using System.Security.Cryptography;

namespace Veil.Auth.Infrastructure.Security;

/// <summary>
/// PBKDF2-SHA256 password hashing. Encoded format:
/// <c>pbkdf2-sha256$&lt;iterations&gt;$&lt;saltBase64&gt;$&lt;hashBase64&gt;</c> —
/// iterations travel with the hash so they can be raised without breaking
/// existing credentials.
/// </summary>
public static class Pbkdf2PasswordHasher {
    private const string AlgorithmIdentifier = "pbkdf2-sha256";
    private const int Iterations = 210_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    // Hash işlemindeki Span'ları Closure (Lambda) içerisine taşımak için State Struct'ı
    private ref struct HashState {
        public Span<byte> Salt;
        public Span<byte> Hash;
        public int Iterations;
    }

    public static string Hash(Secret<char> password) {
        Span<byte> salt = stackalloc byte[SaltSize];
        RandomNumberGenerator.Fill(salt);
         
        Span<byte> hash = stackalloc byte[HashSize];

        // Spanları bir State içerisine sarıyoruz
        HashState state = new() {
            Salt = salt,
            Hash = hash,
            Iterations = Iterations
        };

        // Static lambda kullanarak dışarıdaki hiçbir nesnenin yakalanmamasını (capture) garanti ediyoruz
        password.Expose(state, static (s, passSpan) => {
            Rfc2898DeriveBytes.Pbkdf2(passSpan, s.Salt, s.Hash, s.Iterations, HashAlgorithmName.SHA256);
        });

        Base64String saltB64 = Base64String.FromBytes(salt);
        Base64String hashB64 = Base64String.FromBytes(hash);

        return $"{AlgorithmIdentifier}${Iterations}${saltB64.Value}${hashB64.Value}";
    }


    // Verify işlemindeki Span'ları Closure (Lambda) içerisine taşımak için State Struct'ı
    private ref struct VerifyState {
        public Span<byte> Salt;
        public Span<byte> Expected;
        public Span<byte> Actual;
        public int Iterations;
    }

    public static bool Verify(Secret<char> password, string encoded) {
        string[] parts = encoded.Split('$');
        if(parts is not [AlgorithmIdentifier, _, _, _])
            return false;

        if(!int.TryParse(parts[1], out int iterations) || iterations < 1)
            return false;

        if(!Base64String.TryParse(parts[2], out Base64String saltB64) ||
            !Base64String.TryParse(parts[3], out Base64String expectedB64)) {
            return false;
        }

        int saltLen = saltB64.GetDecodedLength();
        int expectedLen = expectedB64.GetDecodedLength();

        if(saltLen == 0 || expectedLen == 0)
            return false;

        Span<byte> salt = stackalloc byte[saltLen];
        saltB64.TryDecode(salt, out _);

        Span<byte> expected = stackalloc byte[expectedLen];
        expectedB64.TryDecode(expected, out _);

        Span<byte> actual = stackalloc byte[expectedLen];

        // Verify state'i oluşturuyoruz
        VerifyState state = new() {
            Salt = salt,
            Expected = expected,
            Actual = actual,
            Iterations = iterations
        };

        // Static func vererek hem Span taşıyoruz hem de sonucu dışarıya döndürüyoruz
        return password.Expose(state, static (s, passSpan) => {
            Rfc2898DeriveBytes.Pbkdf2(passSpan, s.Salt, s.Actual, s.Iterations, HashAlgorithmName.SHA256);
            return CryptographicOperations.FixedTimeEquals(s.Actual, s.Expected);
        });
    }
}
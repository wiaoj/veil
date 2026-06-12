using System.Security.Cryptography;

namespace Veil.Certificates.Infrastructure.Security;

/// <summary>
/// AES-256-GCM encryption for private keys at rest. Wire format (base64):
/// <c>nonce(12) || tag(16) || ciphertext</c>. The key comes from
/// <c>Certificates:EncryptionKey</c> (64 hex chars).
/// </summary>
public sealed class PrivateKeyProtector {
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public PrivateKeyProtector(byte[] key) {
        if(key.Length != 32)
            throw new ArgumentException("AES-256-GCM key must be 32 bytes.", nameof(key));
        this._key = key;
    }

    public static PrivateKeyProtector? FromHex(string? hex) {
        if(string.IsNullOrWhiteSpace(hex)) return null;
        try {
            byte[] key = Convert.FromHexString(hex);
            return key.Length == 32 ? new PrivateKeyProtector(key) : null;
        }
        catch(FormatException) {
            return null;
        }
    }

    public string Encrypt(string plaintextPem) {
        byte[] plaintext = System.Text.Encoding.UTF8.GetBytes(plaintextPem);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] tag = new byte[TagSize];
        byte[] ciphertext = new byte[plaintext.Length];

        using AesGcm aes = new(this._key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        byte[] payload = new byte[NonceSize + TagSize + ciphertext.Length];
        nonce.CopyTo(payload, 0);
        tag.CopyTo(payload, NonceSize);
        ciphertext.CopyTo(payload, NonceSize + TagSize);
        return Convert.ToBase64String(payload);
    }

    public string Decrypt(string encryptedBase64) {
        byte[] payload = Convert.FromBase64String(encryptedBase64);
        ReadOnlySpan<byte> nonce = payload.AsSpan(0, NonceSize);
        ReadOnlySpan<byte> tag = payload.AsSpan(NonceSize, TagSize);
        ReadOnlySpan<byte> ciphertext = payload.AsSpan(NonceSize + TagSize);

        byte[] plaintext = new byte[ciphertext.Length];
        using AesGcm aes = new(this._key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return System.Text.Encoding.UTF8.GetString(plaintext);
    }
}

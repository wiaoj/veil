using Microsoft.EntityFrameworkCore;
using Veil.Certificates.Domain;
using Veil.Certificates.Infrastructure.Persistence;
using Wiaoj.Security;

namespace Veil.Certificates.Infrastructure.Security;

public sealed class PrivateKeyDataRotator(
    CertificatesDbContext db,
    ISecretProtector<PrivateKeySecretContext> protector) : IDataRotator<PrivateKeySecretContext> {
    
    public async Task<int> RotateBatchAsync(int batchSize, CancellationToken ct = default) {
        // Aktif anahtar versiyonu
        string activePrefix = $"{(int)protector.CurrentKeyVersion}:";

        // Henüz güncel anahtarla şifrelenmemiş (yani eski versiyonda kalmış) sertifikaları bul
        var stale = await db.Certificates
            .Where(c => c.EncryptedPrivateKey != null 
                     && !EF.Functions.Like(EF.Property<string>(c, nameof(Certificate.EncryptedPrivateKey)), $"{activePrefix}%"))
            .Take(batchSize)
            .ToListAsync(ct);

        foreach(var cert in stale) {
            // Wiaoj.Security.Rotation eklentisinin getirdiği Rotate() metoduyla 
            // eski şifreli metin mevcut plaintext'e çevrilip anında yeni versiyon ile tekrar şifreleniyor
            var rotated = protector.Rotate(cert.EncryptedPrivateKey!.Value);

            cert.RotatePrivateKey(rotated);
        }

        await db.SaveChangesAsync(ct);
        return stale.Count;
    }

    public async Task<bool> IsCompleteAsync(CancellationToken ct = default) {
        string activePrefix = $"{(int)protector.CurrentKeyVersion}:";

        // Tüm private key'ler aktif versiyonla şifrelendiyse (başka versiyon kalmadıysa) rotasyon tamamlanmıştır.
        return !await db.Certificates.AnyAsync(
            c => c.EncryptedPrivateKey != null 
              && !EF.Functions.Like(EF.Property<string>(c, nameof(Certificate.EncryptedPrivateKey)), $"{activePrefix}%"), ct);
    }
}

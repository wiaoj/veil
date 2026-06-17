using Microsoft.EntityFrameworkCore;
using Veil.Auth.Domain;
using Veil.Auth.Infrastructure.Persistence;
using Wiaoj.Security;

namespace Veil.Auth.Infrastructure.Security;

public sealed class EmailDataRotator(
    AuthDbContext db,
    ISecretProtector<EmailSecretContext> protector) : IDataRotator<EmailSecretContext> {
    public async Task<int> RotateBatchAsync(int batchSize, CancellationToken ct = default) {
        // Örneğin aktif sürüm 2 ise, veritabanında "2:" öneki ile BAŞLAMAYANLARI (eski sürüm olanları) arıyoruz.
        string activePrefix = $"{(int)protector.CurrentKeyVersion}:";

        // EF.Property<string>(u, "Email") ile EF Core'un ham veritabanı kolon değerine (string olarak) erişiyoruz.
        var stale = await db.Users
            .Where(u => !EF.Functions.Like(EF.Property<string>(u, "Email"), $"{activePrefix}%"))
            .Take(batchSize)
            .ToListAsync(ct);

        foreach(var user in stale) {
            // user.Email zaten kütüphane tarafından çözülmüş durumdadır. Doğrudan rotasyon yapabiliriz:
            var rotated = protector.Rotate(user.EncryptedEmail);

            // Domain metodumuzu çağırarak güncelliyoruz
            user.RotateEmailKey(rotated);
        }

        await db.SaveChangesAsync(ct);
        return stale.Count;
    }

    public async Task<bool> IsCompleteAsync(CancellationToken ct = default) {
        string activePrefix = $"{(int)protector.CurrentKeyVersion}:";

        // Aktif sürümle başlamayan hiçbir kayıt kalmadıysa rotasyon tamamlanmıştır.
        return !await db.Users.AnyAsync(
            u => !EF.Functions.Like(EF.Property<string>(u, "Email"), $"{activePrefix}%"), ct);
    }
}
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Veil.Auth.Domain;
using Veil.Auth.Domain.ValueObjects;
using Wiaoj.Security.EntityFrameworkCore;

namespace Veil.Auth.Infrastructure.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User> {
    public void Configure(EntityTypeBuilder<User> builder) {
        builder.ToTable("users", "auth");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasConversion(
                id => id.Value.Value,
                value => UserId.From(value));
         
        builder.Property(x => x.EncryptedEmail)
            .HasEncryptedSecretConversion<EmailSecretContext>()
            .HasMaxLength(1024)
            .IsRequired();
         
        builder.Property(x => x.EmailHash)
            .HasConversion(
                hash => hash.ToString(),
                value => HexString.Parse(value))
            .HasMaxLength(64)
            .IsFixedLength()
            .IsRequired();

        builder.Property(x => x.DisplayName)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.PasswordHash)
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(x => x.Role)
            .HasConversion<string>()
            .HasMaxLength(20);

        // 3. Benzersiz indeks artık e-posta adresinin şifreli haline değil, HASH değerine uygulanıyor.
        builder.HasIndex(x => x.EmailHash)
            .IsUnique();
    }
}
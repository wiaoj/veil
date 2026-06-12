using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Veil.Certificates.Domain;
using Veil.Certificates.Domain.Enums;
using Veil.Certificates.Domain.ValueObjects;

namespace Veil.Certificates.Infrastructure.Persistence.Configurations;

public sealed class CertificateConfiguration : IEntityTypeConfiguration<Certificate> {
    public void Configure(EntityTypeBuilder<Certificate> builder) {
        builder.ToTable("certificates", "certificates");

        builder.HasKey(x => x.Id);

        // Optimistic concurrency via the aggregate's RowVersion is deferred
        // until Wiaoj.Ddd.EntityFrameworkCore (ApplyDddConventions) is adopted.
        builder.Ignore(x => x.Version);

        builder.Property(x => x.Id)
            .HasConversion(
                id => id.Value.Value,
                value => CertificateId.From(value));

        builder.Property(x => x.Hostname)
            .HasMaxLength(253)
            .IsRequired();

        builder.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(x => x.ChainPem);

        builder.Property(x => x.EncryptedPrivateKey);

        builder.Property(x => x.LastError)
            .HasMaxLength(2048);

        // One in-flight or serving certificate per hostname; history rows
        // (Expired/Revoked/Failed) may repeat the hostname.
        builder.HasIndex(x => x.Hostname);
        builder.HasIndex(x => new { x.Hostname, x.Status });
    }
}

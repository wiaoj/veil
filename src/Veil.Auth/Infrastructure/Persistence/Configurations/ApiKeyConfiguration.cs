using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Veil.Auth.Domain;
using Veil.Auth.Domain.ValueObjects;

namespace Veil.Auth.Infrastructure.Persistence.Configurations;

public sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey> {
    public void Configure(EntityTypeBuilder<ApiKey> builder) {
        builder.ToTable("api_keys", "auth");

        builder.HasKey(x => x.Id);
        builder.Ignore(x => x.Version);

        builder.Property(x => x.Id)
            .HasConversion(
                id => id.Value.Value,
                value => ApiKeyId.From(value));

        builder.Property(x => x.Name)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.KeyHash)
            .HasMaxLength(64)
            .IsRequired();

        // Scope list is small and read-only after creation; jsonb keeps the
        // table flat without a join table.
        builder.Property(x => x.Scopes)
            .HasColumnType("jsonb");

        builder.Property(x => x.CreatedBy)
            .HasConversion(
                id => id.Value.Value,
                value => UserId.From(value));

        builder.HasIndex(x => x.KeyHash).IsUnique();
    }
}

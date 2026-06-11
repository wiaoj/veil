using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Veil.Auth.Domain;
using Veil.Auth.Domain.ValueObjects;

namespace Veil.Auth.Infrastructure.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User> {
    public void Configure(EntityTypeBuilder<User> builder) {
        builder.ToTable("users", "auth");

        builder.HasKey(x => x.Id);

        // Optimistic concurrency via the aggregate's RowVersion is deferred
        // until Wiaoj.Ddd.EntityFrameworkCore (ApplyDddConventions) is adopted.
        builder.Ignore(x => x.Version);

        builder.Property(x => x.Id)
            .HasConversion(
                id => id.Value.Value,
                value => UserId.From(value));

        builder.Property(x => x.Email)
            .HasMaxLength(320)
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

        builder.HasIndex(x => x.Email).IsUnique();
    }
}

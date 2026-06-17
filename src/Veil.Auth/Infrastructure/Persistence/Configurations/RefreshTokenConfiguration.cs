using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Veil.Auth.Domain;
using Veil.Auth.Domain.ValueObjects;

namespace Veil.Auth.Infrastructure.Persistence.Configurations;

public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken> {
    public void Configure(EntityTypeBuilder<RefreshToken> builder) {
        builder.ToTable("refresh_tokens", "auth");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasConversion(
                id => id.Value.Value,
                value => RefreshTokenId.From(value));

        builder.Property(x => x.UserId)
            .HasConversion(
                id => id.Value.Value,
                value => UserId.From(value));

        builder.Property(x => x.TokenHash)
             .HasConversion(
                 hash => hash.ToString(),
                 value => HexString.Parse(value))
             .HasMaxLength(64)
             .IsFixedLength()
             .IsRequired();

        builder.Property(x => x.ReplacedByTokenHash)
            .HasMaxLength(64);

        builder.HasIndex(x => x.TokenHash).IsUnique();
        builder.HasIndex(x => x.UserId);
    }
}

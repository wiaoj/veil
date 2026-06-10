using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Veil.Zones.Domain;
using Veil.Zones.Domain.ValueObjects;

namespace Veil.Zones.Infrastructure.Persistence.Configurations;

public sealed class ZoneConfiguration : IEntityTypeConfiguration<Zone> {
    public void Configure(EntityTypeBuilder<Zone> builder) {
        builder.ToTable("zones", "zones");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasConversion(
                id => id.Value.Value,
                value => ZoneId.From(value))
            .HasMaxLength(32);

        builder.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.ComplexProperty(x => x.Hostname, b => {
            b.Property(h => h.Value)
                .HasColumnName("hostname")
                .HasMaxLength(255)
                .IsRequired();
            // We ignore IsWildcard for DB, it can be computed or we map it
            b.Ignore(h => h.IsWildcard);
        });

        builder.OwnsOne(x => x.Upstream, b => {
            b.ToJson();
        });

        builder.OwnsOne(x => x.Challenge, b => {
            b.ToJson();
        });

        // Rules are mapped in RuleConfiguration
        builder.HasMany(x => x.Rules)
            .WithOne()
            .HasForeignKey("ZoneId")
            .OnDelete(DeleteBehavior.Cascade);

        // EF Core mapping for private field
        builder.Metadata.FindNavigation(nameof(Zone.Rules))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

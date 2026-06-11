using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System.Text.Json;
using Veil.Zones.Domain;
using Veil.Zones.Domain.ValueObjects;

namespace Veil.Zones.Infrastructure.Persistence.Configurations;

public sealed class ZoneConfiguration : IEntityTypeConfiguration<Zone> {
    public void Configure(EntityTypeBuilder<Zone> builder) {
        builder.ToTable("zones", "zones");

        builder.HasKey(x => x.Id);

        // Optimistic concurrency via the aggregate's RowVersion is deferred
        // until Wiaoj.Ddd.EntityFrameworkCore (ApplyDddConventions) is adopted.
        builder.Ignore(x => x.Version);

        builder.Property(x => x.Id)
            .HasConversion(
                id => id.Value.Value,
                value => ZoneId.From(value));

        builder.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.ComplexProperty(x => x.Hostname, b => {
            b.Property(h => h.Value)
                .HasColumnName("hostname")
                .HasMaxLength(255)
                .IsRequired();
        });

        // Upstream and Challenge are stored as jsonb through persistence DTOs
        // (JsonColumnData) because the domain value objects are not
        // constructible by EF or System.Text.Json by design.
        builder.Property(x => x.Upstream)
            .HasColumnName("upstream")
            .HasColumnType("jsonb")
            .IsRequired()
            .HasConversion(
                v => JsonSerializer.Serialize(UpstreamConfigData.FromDomain(v), JsonSerializerOptions.Default),
                v => JsonSerializer.Deserialize<UpstreamConfigData>(v, JsonSerializerOptions.Default)!.ToDomain());

        builder.Property(x => x.Challenge)
            .HasColumnName("challenge")
            .HasColumnType("jsonb")
            .IsRequired()
            .HasConversion(
                v => JsonSerializer.Serialize(ChallengeConfigData.FromDomain(v), JsonSerializerOptions.Default),
                v => JsonSerializer.Deserialize<ChallengeConfigData>(v, JsonSerializerOptions.Default)!.ToDomain());

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

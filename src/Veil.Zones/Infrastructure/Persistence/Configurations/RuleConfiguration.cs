using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System.Text.Json;
using Veil.Zones.Domain;
using Veil.Zones.Domain.ValueObjects;

namespace Veil.Zones.Infrastructure.Persistence.Configurations;

public sealed class RuleConfiguration : IEntityTypeConfiguration<Rule> {
    public void Configure(EntityTypeBuilder<Rule> builder) {
        builder.ToTable("rules", "zones");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasConversion(
                id => id.Value.Value,
                value => RuleId.From(value));

        builder.Property(x => x.Name).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Action).HasConversion<string>().HasMaxLength(20);

        builder.Property(x => x.Conditions)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
                v => JsonSerializer.Deserialize<List<RuleCondition>>(v, JsonSerializerOptions.Default) ?? new List<RuleCondition>()
            );

        builder.OwnsOne(x => x.RateLimit, b => {
            b.ToJson();
        });
    }
}

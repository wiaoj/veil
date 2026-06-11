using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System.Text.Json;
using Veil.Zones.Domain;
using Veil.Zones.Domain.ValueObjects;

namespace Veil.Zones.Infrastructure.Persistence.Configurations;

public sealed class RuleConfiguration : IEntityTypeConfiguration<Rule> {
    /// <summary>
    /// jsonb does not preserve key order, so the polymorphic "type"
    /// discriminator may come back mid-object — allow that on read.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Default) {
        AllowOutOfOrderMetadataProperties = true,
    };

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
                v => JsonSerializer.Serialize(v, JsonOptions),
                v => JsonSerializer.Deserialize<List<RuleCondition>>(v, JsonOptions) ?? new List<RuleCondition>()
            )
            .Metadata.SetValueComparer(new ValueComparer<IReadOnlyList<RuleCondition>>(
                (l, r) => JsonSerializer.Serialize(l, JsonOptions)
                       == JsonSerializer.Serialize(r, JsonOptions),
                v => JsonSerializer.Serialize(v, JsonOptions).GetHashCode(),
                v => JsonSerializer.Deserialize<List<RuleCondition>>(
                         JsonSerializer.Serialize(v, JsonOptions), JsonOptions)!));

        builder.Property(x => x.RateLimit)
            .HasColumnName("rate_limit")
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(RateLimitData.FromDomain(v!), JsonSerializerOptions.Default),
                v => JsonSerializer.Deserialize<RateLimitData>(v, JsonSerializerOptions.Default)!.ToDomain());
    }
}

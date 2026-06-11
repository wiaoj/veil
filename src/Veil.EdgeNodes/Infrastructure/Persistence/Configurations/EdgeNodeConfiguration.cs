using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Veil.EdgeNodes.Domain;
using Veil.EdgeNodes.Domain.ValueObjects;

namespace Veil.EdgeNodes.Infrastructure.Persistence.Configurations;

public sealed class EdgeNodeConfiguration : IEntityTypeConfiguration<EdgeNode> {
    public void Configure(EntityTypeBuilder<EdgeNode> builder) {
        builder.ToTable("edge_nodes", "edge_nodes");

        builder.HasKey(x => x.Id);

        // Optimistic concurrency via the aggregate's RowVersion is deferred
        // until Wiaoj.Ddd.EntityFrameworkCore (ApplyDddConventions) is adopted.
        builder.Ignore(x => x.Version);

        builder.Property(x => x.Id)
            .HasConversion(
                id => id.Value.Value,
                value => EdgeNodeId.From(value));

        builder.Property(x => x.Name)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.Address)
            .HasMaxLength(2048)
            .HasConversion(
                uri => uri.ToString(),
                value => new Uri(value))
            .IsRequired();

        builder.Property(x => x.TokenHash)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.HasIndex(x => x.TokenHash);
    }
}

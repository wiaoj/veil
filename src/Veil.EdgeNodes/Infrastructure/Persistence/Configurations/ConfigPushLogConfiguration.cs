using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Veil.EdgeNodes.Domain;
using Veil.EdgeNodes.Domain.ValueObjects;

namespace Veil.EdgeNodes.Infrastructure.Persistence.Configurations;

public sealed class ConfigPushLogConfiguration : IEntityTypeConfiguration<ConfigPushLog> {
    public void Configure(EntityTypeBuilder<ConfigPushLog> builder) {
        builder.ToTable("config_push_log", "edge_nodes");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasConversion(
                id => id.Value.Value,
                value => ConfigPushLogId.From(value));

        builder.Property(x => x.EdgeNodeId)
            .HasConversion(
                id => id.Value.Value,
                value => EdgeNodeId.From(value));

        builder.Property(x => x.Error)
            .HasMaxLength(2048);

        builder.HasIndex(x => x.EdgeNodeId);

        builder.HasOne<EdgeNode>()
            .WithMany()
            .HasForeignKey(x => x.EdgeNodeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

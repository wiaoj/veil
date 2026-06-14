using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Veil.Auth.Audit;

namespace Veil.Auth.Infrastructure.Persistence.Configurations;

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent> {
    public void Configure(EntityTypeBuilder<AuditEvent> builder) {
        builder.ToTable("audit_events", "auth");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Action).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Outcome).HasMaxLength(16).IsRequired();
        builder.Property(x => x.Actor).HasMaxLength(320);
        builder.Property(x => x.ActorIp).HasMaxLength(64);
        builder.Property(x => x.Target).HasMaxLength(256);
        builder.Property(x => x.Detail).HasMaxLength(1024);

        // Common queries: recent events, and events for a given action.
        builder.HasIndex(x => x.TimestampUtc);
        builder.HasIndex(x => x.Action);
    }
}

using Microsoft.EntityFrameworkCore;
using Veil.Zones.Domain;
using Wiaoj.Ddd.EntityFrameworkCore;
using Wiaoj.Ddd.EntityFrameworkCore.Outbox;

namespace Veil.Zones.Infrastructure.Persistence;

public sealed class ZonesDbContext(DbContextOptions<ZonesDbContext> options) : DbContext(options), IDddOutbox {
    public DbSet<Zone> Zones => Set<Zone>();
    public DbSet<Rule> Rules => Set<Rule>();

    DbSet<OutboxMessage> IDddOutbox.OutboxMessages { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.HasDefaultSchema("zones");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ZonesDbContext).Assembly);
        // Transactional outbox for domain → integration event publishing.
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
        base.OnModelCreating(modelBuilder);
    }
}
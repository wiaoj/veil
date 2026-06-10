using Microsoft.EntityFrameworkCore;
using Veil.Zones.Domain;

namespace Veil.Zones.Infrastructure.Persistence;

public sealed class ZonesDbContext(DbContextOptions<ZonesDbContext> options) : DbContext(options) {
    public DbSet<Zone> Zones => Set<Zone>();
    public DbSet<Rule> Rules => Set<Rule>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.HasDefaultSchema("zones");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ZonesDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
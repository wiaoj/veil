using Microsoft.EntityFrameworkCore;
using Veil.EdgeNodes.Domain;
using Wiaoj.Ddd.EntityFrameworkCore;

namespace Veil.EdgeNodes.Infrastructure.Persistence;

public sealed class EdgeNodesDbContext(DbContextOptions<EdgeNodesDbContext> options) : DbContext(options) {
    public DbSet<EdgeNode> EdgeNodes => Set<EdgeNode>();
    public DbSet<ConfigPushLog> ConfigPushLogs => Set<ConfigPushLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.HasDefaultSchema("edge_nodes");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EdgeNodesDbContext).Assembly);
        // Transactional outbox for domain → integration event publishing.
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
        base.OnModelCreating(modelBuilder);
    }
}

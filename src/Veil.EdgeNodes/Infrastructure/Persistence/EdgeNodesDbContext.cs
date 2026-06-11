using Microsoft.EntityFrameworkCore;
using Veil.EdgeNodes.Domain;

namespace Veil.EdgeNodes.Infrastructure.Persistence;

public sealed class EdgeNodesDbContext(DbContextOptions<EdgeNodesDbContext> options) : DbContext(options) {
    public DbSet<EdgeNode> EdgeNodes => Set<EdgeNode>();
    public DbSet<ConfigPushLog> ConfigPushLogs => Set<ConfigPushLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.HasDefaultSchema("edge_nodes");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EdgeNodesDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}

using Microsoft.EntityFrameworkCore;
using Veil.Auth.Audit;
using Veil.Auth.Domain;
using Wiaoj.Ddd.EntityFrameworkCore;

namespace Veil.Auth.Infrastructure.Persistence;

public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options) : DbContext(options) {
    public DbSet<User> Users => Set<User>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.HasDefaultSchema("auth");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AuthDbContext).Assembly);
        // Transactional outbox for domain → integration event publishing.
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
        base.OnModelCreating(modelBuilder);
    }
}

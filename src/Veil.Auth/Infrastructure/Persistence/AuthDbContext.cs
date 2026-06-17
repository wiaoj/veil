using Microsoft.EntityFrameworkCore;
using Veil.Auth.Audit;
using Veil.Auth.Domain;
using Wiaoj.Ddd.EntityFrameworkCore;
using Wiaoj.Ddd.EntityFrameworkCore.Extensions;
using Wiaoj.Ddd.EntityFrameworkCore.ValueConverters;
using Wiaoj.Ddd.ValueObjects;

namespace Veil.Auth.Infrastructure.Persistence;

public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options) : DbContext(options) {
    public DbSet<User> Users => Set<User>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) {
        configurationBuilder.Properties<RowVersion>()
            .HaveConversion<RowVersionConverter>();

        base.ConfigureConventions(configurationBuilder);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.HasDefaultSchema("auth");
        modelBuilder.ApplyDddConventions();
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AuthDbContext).Assembly);
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
        base.OnModelCreating(modelBuilder);
    }
}

using Microsoft.EntityFrameworkCore;
using Veil.Certificates.Domain;
using Wiaoj.Ddd.EntityFrameworkCore;

namespace Veil.Certificates.Infrastructure.Persistence;

public sealed class CertificatesDbContext(DbContextOptions<CertificatesDbContext> options) : DbContext(options) {
    public DbSet<Certificate> Certificates => Set<Certificate>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.HasDefaultSchema("certificates");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CertificatesDbContext).Assembly);
        // Transactional outbox for domain → integration event publishing.
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
        base.OnModelCreating(modelBuilder);
    }
}

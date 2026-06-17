using Microsoft.EntityFrameworkCore;
using Wiaoj.Security;
using Wiaoj.Security.EntityFrameworkCore;

namespace Veil.Infrastructure.Security;
public class SecurityDbContext(DbContextOptions<SecurityDbContext> options) : DbContext(options), IEncryptionKeyDbContext {
    public DbSet<EncryptionKeyRecord> EncryptionKeys { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.HasDefaultSchema("security");
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new EncryptionKeyRecordConfiguration());
    }
}
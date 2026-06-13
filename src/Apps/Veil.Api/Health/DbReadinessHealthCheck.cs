using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Veil.Api.Health;

/// <summary>
/// Readiness probe: the service is ready only when it can reach its
/// PostgreSQL database. All modules share one database, so pinging any one
/// context's connection is representative.
/// </summary>
public sealed class DbReadinessHealthCheck<TContext>(IDbContextFactory<TContext> dbFactory) : IHealthCheck
    where TContext : DbContext {

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) {
        try {
            await using TContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
            bool canConnect = await db.Database.CanConnectAsync(cancellationToken);
            return canConnect
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Database is unreachable.");
        }
        catch(Exception ex) {
            return HealthCheckResult.Unhealthy("Database connection failed.", ex);
        }
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Veil.Analytics.Worker.Health;

/// <summary>
/// Readiness probe: the worker is ready only when it can reach PostgreSQL
/// (used to authenticate edge node tokens). ClickHouse ingestion is
/// fire-and-forget and deliberately excluded — an analytics outage must not
/// take the ingest endpoint out of rotation.
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

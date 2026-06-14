using Microsoft.EntityFrameworkCore;
using Veil.Auth.Infrastructure.Persistence;

namespace Veil.Auth.Audit;

/// <summary>
/// Persists audit records to the <c>auth.audit_events</c> table via a short-
/// lived context, so it is safe to resolve as a singleton and call from any
/// scope. Writes use their own context to keep audit persistence independent
/// of the caller's unit of work.
/// </summary>
public sealed class AuditLogger(
    IDbContextFactory<AuthDbContext> dbFactory,
    TimeProvider timeProvider) : IAuditLogger {

    public async Task WriteAsync(
        string action,
        string outcome,
        string? actor = null,
        string? actorIp = null,
        string? target = null,
        string? detail = null,
        CancellationToken cancellationToken = default) {
        await using AuthDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.AuditEvents.Add(AuditEvent.Record(
            timeProvider.GetUtcNow(), action, outcome, actor, actorIp, target, detail));
        await db.SaveChangesAsync(cancellationToken);
    }
}

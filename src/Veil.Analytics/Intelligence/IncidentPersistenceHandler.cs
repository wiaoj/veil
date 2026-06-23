using Microsoft.Extensions.Logging;
using Tyto;

namespace Veil.Analytics.Intelligence;

/// <summary>
/// Bus subscriber that persists each AI incident to the durable archive, so the
/// feed survives a worker restart. An independent handler alongside the webhook
/// + SIEM sinks (best-effort, never throws) — a DB hiccup can't disrupt the
/// analysis loop or the other sinks. No-ops when no archive is configured
/// (<see cref="NullIncidentArchive"/>).
/// </summary>
public sealed class IncidentPersistenceHandler(
    IIncidentArchive archive,
    ILogger<IncidentPersistenceHandler> logger) : IEventHandler<IncidentRaised> {

    public async ValueTask HandleAsync(IMessageContext<IncidentRaised> context, CancellationToken cancellationToken = default) {
        try {
            await archive.SaveAsync(context.Message.Incident, cancellationToken);
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) { }
        catch(Exception ex) {
            logger.LogWarning(ex, "Persisting incident {Id} failed", context.Message.Incident.Id);
        }
    }
}

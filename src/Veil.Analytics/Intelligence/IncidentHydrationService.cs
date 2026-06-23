using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Veil.Analytics.Intelligence;

/// <summary>
/// On startup, ensures the archive schema exists and rehydrates the in-memory
/// <see cref="IncidentStore"/> ring from the most recent persisted incidents, so
/// the dashboard's live feed isn't empty after a worker restart. Runs once and
/// returns; ongoing persistence is handled by <see cref="IncidentPersistenceHandler"/>.
/// </summary>
public sealed class IncidentHydrationService(
    IIncidentArchive archive,
    IncidentStore store,
    IOptions<IntelligenceOptions> options,
    ILogger<IncidentHydrationService> logger) : IHostedService {

    private readonly IntelligenceOptions _options = options.Value;

    public async Task StartAsync(CancellationToken cancellationToken) {
        try {
            await archive.EnsureSchemaAsync(cancellationToken);

            IReadOnlyList<TrafficIncident> recent =
                await archive.RecentAsync(this._options.MaxIncidents, cancellationToken);

            // RecentAsync returns newest-first; add oldest-first so the ring's
            // ordering matches a fresh run.
            foreach(TrafficIncident incident in recent.Reverse())
                store.Add(incident);

            if(recent.Count > 0)
                logger.LogInformation("Rehydrated {Count} incident(s) from the archive", recent.Count);
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) { }
        catch(Exception ex) {
            // Never block worker startup on archive availability.
            logger.LogWarning(ex, "Incident rehydration failed; starting with an empty live feed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

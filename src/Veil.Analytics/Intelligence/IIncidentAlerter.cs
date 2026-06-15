namespace Veil.Analytics.Intelligence;

/// <summary>
/// Fans an AI-detected incident out to external sinks (webhook, SIEM). Like the
/// SIEM exporter, this is best-effort and must never throw — an alerting outage
/// cannot be allowed to disrupt the analysis loop.
/// </summary>
public interface IIncidentAlerter {
    Task AlertAsync(TrafficIncident incident, CancellationToken cancellationToken);
}

/// <summary>No-op alerter used when no webhook/SIEM sink is configured.</summary>
public sealed class NullIncidentAlerter : IIncidentAlerter {
    public Task AlertAsync(TrafficIncident incident, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

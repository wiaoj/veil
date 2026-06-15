namespace Veil.Analytics.Intelligence;

/// <summary>
/// Triages a detected anomaly with an LLM. Invoked only when the cheap
/// statistical layer flags a window — never per request — so cost and latency
/// stay bounded.
/// </summary>
public interface IAnalystClient {
    Task<AnalystVerdict?> AnalyzeAsync(TrafficIncident incident, CancellationToken cancellationToken);
}

/// <summary>Used when no API key is configured: detection runs, triage is skipped.</summary>
public sealed class NullAnalystClient : IAnalystClient {
    public Task<AnalystVerdict?> AnalyzeAsync(TrafficIncident incident, CancellationToken cancellationToken) =>
        Task.FromResult<AnalystVerdict?>(null);
}

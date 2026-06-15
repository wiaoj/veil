using Veil.Analytics.Ingestion;

namespace Veil.Analytics.Intelligence;

/// <summary>
/// Live, in-memory traffic analyzer. <see cref="Observe"/> is called from the
/// ingest hot path for every batch (cheap, lock-guarded counter updates);
/// <see cref="Sweep"/> is called once per interval by the analysis loop to score
/// each zone and emit anomaly incidents (without AI triage — that is layered on
/// by the caller).
/// </summary>
public interface ITrafficAnalyzer {
    void Observe(IReadOnlyList<RequestLogRow> batch);
    IReadOnlyList<TrafficIncident> Sweep(DateTimeOffset nowUtc);
}

/// <summary>No-op analyzer registered when the intelligence layer is disabled.</summary>
public sealed class NullTrafficAnalyzer : ITrafficAnalyzer {
    public void Observe(IReadOnlyList<RequestLogRow> batch) { }
    public IReadOnlyList<TrafficIncident> Sweep(DateTimeOffset nowUtc) => [];
}

using Veil.Analytics.Ingestion;

namespace Veil.Analytics.Siem;

/// <summary>
/// Forwards request-log batches to an external SIEM. Implementations are
/// fire-and-forget and must never throw — a SIEM outage cannot be allowed to
/// back-pressure or fail ingestion.
/// </summary>
public interface ISiemExporter {
    Task ExportAsync(IReadOnlyList<RequestLogRow> batch, CancellationToken cancellationToken);
}

/// <summary>No-op exporter used when no SIEM endpoint is configured.</summary>
public sealed class NullSiemExporter : ISiemExporter {
    public Task ExportAsync(IReadOnlyList<RequestLogRow> batch, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

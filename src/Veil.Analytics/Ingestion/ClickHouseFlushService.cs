using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Veil.Analytics.ClickHouse;
using Veil.Analytics.Siem;
using Veil.Shared.Observability;

namespace Veil.Analytics.Ingestion;

/// <summary>
/// Drains the ingest queue into ClickHouse. A failed insert drops the batch
/// and logs — the pipeline is fire-and-forget end to end, a ClickHouse
/// outage must never back-pressure ingestion or the edge.
/// </summary>
public sealed class ClickHouseFlushService(
    RequestLogQueue queue,
    ClickHouseWriter writer,
    ISiemExporter siemExporter,
    MetricsCollector metrics,
    ILogger<ClickHouseFlushService> logger) : BackgroundService {

    private const string RowsWritten = "veil_clickhouse_rows_written_total";
    private const string WriteFailures = "veil_clickhouse_write_failures_total";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        await EnsureSchemaAsync(stoppingToken);

        await foreach(IReadOnlyList<RequestLogRow> batch in queue.ReadAllAsync(stoppingToken)) {
            // Best-effort SIEM mirror, fire-and-forget so a slow/failing SIEM
            // never back-pressures the ClickHouse path (the exporter swallows
            // its own errors).
            _ = siemExporter.ExportAsync(batch, stoppingToken);

            try {
                await writer.InsertAsync(batch, stoppingToken);
                metrics.IncrementCounter(RowsWritten, "Request log rows written to ClickHouse.", batch.Count);
                logger.LogDebug("Flushed {Count} request log rows to ClickHouse", batch.Count);
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) {
                throw;
            }
            catch(Exception ex) {
                metrics.IncrementCounter(WriteFailures, "ClickHouse insert failures (dropped batches).");
                logger.LogWarning(ex, "Dropped {Count} request log rows: ClickHouse insert failed", batch.Count);
            }
        }
    }

    /// <summary>
    /// Schema creation retries with backoff so the worker survives starting
    /// before ClickHouse. Inserts keep failing (and dropping) until it lands.
    /// </summary>
    private async Task EnsureSchemaAsync(CancellationToken stoppingToken) {
        TimeSpan backoff = TimeSpan.FromSeconds(2);

        while(!stoppingToken.IsCancellationRequested) {
            try {
                await writer.EnsureSchemaAsync(stoppingToken);
                logger.LogInformation("ClickHouse request_logs schema ensured");
                return;
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) {
                return;
            }
            catch(Exception ex) {
                logger.LogWarning(ex, "ClickHouse schema init failed; retrying in {Backoff}", backoff);
                await Task.Delay(backoff, stoppingToken);
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
            }
        }
    }
}

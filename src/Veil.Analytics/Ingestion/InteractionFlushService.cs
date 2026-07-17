using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Veil.Analytics.ClickHouse;
using Veil.Shared.Observability;

namespace Veil.Analytics.Ingestion;

/// <summary>
/// Drains the interaction queue into ClickHouse. Fire-and-forget end to end,
/// like <see cref="ClickHouseFlushService"/>: a failed insert drops the batch
/// and logs — a ClickHouse outage must never back-pressure the edge.
/// </summary>
public sealed class InteractionFlushService(
    InteractionQueue queue,
    ClickHouseWriter writer,
    MetricsCollector metrics,
    ILogger<InteractionFlushService> logger) : BackgroundService {

    private const string RowsWritten = "veil_interaction_rows_written_total";
    private const string WriteFailures = "veil_interaction_write_failures_total";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        await EnsureSchemaAsync(stoppingToken);

        await foreach(IReadOnlyList<InteractionRow> batch in queue.ReadAllAsync(stoppingToken)) {
            try {
                await writer.InsertInteractionsAsync(batch, stoppingToken);
                metrics.IncrementCounter(RowsWritten, "Interaction rows written to ClickHouse.", batch.Count);
                logger.LogDebug("Flushed {Count} interaction rows to ClickHouse", batch.Count);
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) {
                throw;
            }
            catch(Exception ex) {
                metrics.IncrementCounter(WriteFailures, "ClickHouse interaction insert failures (dropped batches).");
                logger.LogWarning(ex, "Dropped {Count} interaction rows: ClickHouse insert failed", batch.Count);
            }
        }
    }

    /// <summary>Schema creation retries with backoff so the worker survives
    /// starting before ClickHouse.</summary>
    private async Task EnsureSchemaAsync(CancellationToken stoppingToken) {
        TimeSpan backoff = TimeSpan.FromSeconds(2);

        while(!stoppingToken.IsCancellationRequested) {
            try {
                await writer.EnsureInteractionSchemaAsync(stoppingToken);
                logger.LogInformation("ClickHouse {Table} schema ensured", ClickHouseWriter.InteractionTableName);
                return;
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) {
                return;
            }
            catch(Exception ex) {
                logger.LogWarning(ex, "ClickHouse interaction schema init failed; retrying in {Backoff}", backoff);
                await Task.Delay(backoff, stoppingToken);
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
            }
        }
    }
}

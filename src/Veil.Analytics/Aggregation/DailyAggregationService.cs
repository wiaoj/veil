using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Veil.Analytics.ClickHouse;

namespace Veil.Analytics.Aggregation;

/// <summary>
/// Nightly rollup: aggregates the request-log verdict counts per zone per day
/// out of ClickHouse into PostgreSQL (<c>analytics.daily_summary</c>). Runs
/// shortly after each UTC midnight and re-aggregates a short trailing window
/// so late-arriving rows are folded in; the upsert keeps it idempotent.
/// </summary>
public sealed class DailyAggregationService(
    ClickHouseReader reader,
    DailySummaryStore store,
    TimeProvider timeProvider,
    ILogger<DailyAggregationService> logger) : BackgroundService {

    /// Minutes past midnight UTC to run, leaving room for the final flush of
    /// the previous day's rows to land in ClickHouse.
    private static readonly TimeSpan RunAfterMidnight = TimeSpan.FromMinutes(30);

    /// How many trailing days to re-aggregate each run (today + yesterday).
    private const int WindowDays = 2;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        // Catch-up pass at startup so a worker that was down over midnight
        // still fills the gap without waiting a full day.
        await SafeAggregateAsync(stoppingToken);

        while(!stoppingToken.IsCancellationRequested) {
            TimeSpan delay = TimeUntilNextRun();
            logger.LogInformation("Next daily aggregation in {Delay}", delay);
            try {
                await Task.Delay(delay, stoppingToken);
            }
            catch(OperationCanceledException) {
                break;
            }
            await SafeAggregateAsync(stoppingToken);
        }
    }

    private async Task SafeAggregateAsync(CancellationToken cancellationToken) {
        try {
            await AggregateAsync(cancellationToken);
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) {
            throw;
        }
        catch(Exception ex) {
            logger.LogWarning(ex, "Daily aggregation failed; will retry on the next cycle");
        }
    }

    private async Task AggregateAsync(CancellationToken cancellationToken) {
        await store.EnsureSchemaAsync(cancellationToken);

        DateTimeOffset now = timeProvider.GetUtcNow();
        DateOnly fromDay = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-(WindowDays - 1));
        string fromUtc = fromDay.ToDateTime(TimeOnly.MinValue).ToString("yyyy-MM-dd HH:mm:ss");

        const string sql = """
            SELECT
                toDate(ts) AS day,
                zone,
                count() AS total,
                countIf(verdict = 'allow') AS allowed,
                countIf(verdict = 'block') AS blocked,
                countIf(verdict = 'challenge') AS challenged,
                countIf(verdict = 'challenge_pass') AS challenge_passed,
                countIf(verdict = 'rate_limited') AS rate_limited,
                uniqExact(client_ip) AS unique_ips
            FROM request_logs
            WHERE ts >= {fromUtc:DateTime}
            GROUP BY day, zone
            """;

        List<DailySummaryRow> rows = await reader.QueryAsync<DailySummaryRow>(
            sql,
            new Dictionary<string, string> { ["fromUtc"] = fromUtc },
            cancellationToken);

        await store.UpsertAsync(rows, cancellationToken);
        logger.LogInformation(
            "Daily aggregation upserted {Rows} zone-day rows from {From}", rows.Count, fromDay);
    }

    private TimeSpan TimeUntilNextRun() {
        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset todayRun = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero) + RunAfterMidnight;
        DateTimeOffset next = now < todayRun ? todayRun : todayRun.AddDays(1);
        return next - now;
    }
}

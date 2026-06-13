using Npgsql;

namespace Veil.Analytics.Aggregation;

/// <summary>
/// PostgreSQL sink for the daily analytics rollup. Raw Npgsql, no EF — the
/// analytics module deliberately owns its persistence as thin clients (same
/// philosophy as <see cref="ClickHouse.ClickHouseWriter"/>). The table lives
/// in its own <c>analytics</c> schema and is keyed by <c>(day, zone)</c> so
/// re-aggregating a day is an idempotent upsert.
/// </summary>
public sealed class DailySummaryStore(string connectionString) {
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken) {
        const string ddl = """
            CREATE SCHEMA IF NOT EXISTS analytics;
            CREATE TABLE IF NOT EXISTS analytics.daily_summary (
                day               date    NOT NULL,
                zone              text    NOT NULL,
                total             bigint  NOT NULL,
                allowed           bigint  NOT NULL,
                blocked           bigint  NOT NULL,
                challenged        bigint  NOT NULL,
                challenge_passed  bigint  NOT NULL,
                rate_limited      bigint  NOT NULL,
                unique_ips        bigint  NOT NULL,
                PRIMARY KEY (day, zone)
            );
            """;

        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = new(ddl, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Upserts a day's rollup rows; existing (day, zone) rows are replaced.</summary>
    public async Task UpsertAsync(IReadOnlyList<DailySummaryRow> rows, CancellationToken cancellationToken) {
        if(rows.Count == 0)
            return;

        const string sql = """
            INSERT INTO analytics.daily_summary
                (day, zone, total, allowed, blocked, challenged, challenge_passed, rate_limited, unique_ips)
            VALUES (@day, @zone, @total, @allowed, @blocked, @challenged, @challenge_passed, @rate_limited, @unique_ips)
            ON CONFLICT (day, zone) DO UPDATE SET
                total = EXCLUDED.total,
                allowed = EXCLUDED.allowed,
                blocked = EXCLUDED.blocked,
                challenged = EXCLUDED.challenged,
                challenge_passed = EXCLUDED.challenge_passed,
                rate_limited = EXCLUDED.rate_limited,
                unique_ips = EXCLUDED.unique_ips;
            """;

        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlTransaction tx = await connection.BeginTransactionAsync(cancellationToken);

        foreach(DailySummaryRow row in rows) {
            await using NpgsqlCommand command = new(sql, connection, tx);
            command.Parameters.AddWithValue("day", DateOnly.Parse(row.Day));
            command.Parameters.AddWithValue("zone", row.Zone);
            command.Parameters.AddWithValue("total", row.Total);
            command.Parameters.AddWithValue("allowed", row.Allowed);
            command.Parameters.AddWithValue("blocked", row.Blocked);
            command.Parameters.AddWithValue("challenged", row.Challenged);
            command.Parameters.AddWithValue("challenge_passed", row.ChallengePassed);
            command.Parameters.AddWithValue("rate_limited", row.RateLimited);
            command.Parameters.AddWithValue("unique_ips", row.UniqueIps);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }
}

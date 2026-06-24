using Npgsql;
using Veil.Analytics.Aggregation;
using Xunit;

namespace Veil.IntegrationTests;

/// <summary>
/// <see cref="DailySummaryStore"/> against a real PostgreSQL: schema DDL and the
/// idempotent (day, zone) upsert the nightly aggregation relies on for its
/// trailing re-aggregation window.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class DailySummaryStoreTests(PostgresFixture postgres) {
    private static DailySummaryRow Row(string day, string zone, long total) =>
        new(day, zone, total, total, 0, 0, 0, 0, 1);

    [Fact]
    public async Task Upsert_is_idempotent_per_day_and_zone() {
        DailySummaryStore store = new(postgres.ConnectionString);
        await store.EnsureSchemaAsync(CancellationToken.None);

        const string day = "2026-06-20";
        const string zone = "agg.example.com";

        await store.UpsertAsync([Row(day, zone, 100)], CancellationToken.None);
        // Re-aggregating the same day must replace, not duplicate, the row.
        await store.UpsertAsync([Row(day, zone, 250)], CancellationToken.None);

        await using NpgsqlConnection conn = new(postgres.ConnectionString);
        await conn.OpenAsync(CancellationToken.None);
        await using NpgsqlCommand cmd = new(
            "SELECT count(*), max(total) FROM analytics.daily_summary WHERE day = @d AND zone = @z", conn);
        cmd.Parameters.AddWithValue("d", DateOnly.Parse(day));
        cmd.Parameters.AddWithValue("z", zone);

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(1, reader.GetInt64(0));     // exactly one row
        Assert.Equal(250, reader.GetInt64(1));   // latest value won
    }
}

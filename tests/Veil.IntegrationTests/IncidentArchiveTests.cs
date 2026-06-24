using Npgsql;
using Veil.Analytics.Intelligence;
using Xunit;

namespace Veil.IntegrationTests;

/// <summary>
/// <see cref="NpgsqlIncidentArchive"/> against a real PostgreSQL: schema DDL,
/// the jsonb payload round-trip, and newest-first ordering that the worker's
/// startup rehydration relies on. Each test starts from an empty table.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class IncidentArchiveTests(PostgresFixture postgres) : IAsyncLifetime {
    private NpgsqlIncidentArchive Archive => new(postgres.ConnectionString);

    public async Task InitializeAsync() {
        await this.Archive.EnsureSchemaAsync(CancellationToken.None);
        await using NpgsqlConnection conn = new(postgres.ConnectionString);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = new("TRUNCATE intelligence.incidents", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static TrafficIncident Incident(string id, string zone, DateTimeOffset detectedAt) => new() {
        Id = id,
        DetectedAtUtc = detectedAt,
        Zone = zone,
        AnomalyScore = 87,
        Signals = ["ml_rate_spike", "single_asn (AS14061 = 80%)"],
        RatePerSecond = 1200,
        BaselineRatePerSecond = 30,
        BlockedRatio = 0.4,
        DistinctIps = 250,
        TopIps = [new TrafficCount("203.0.113.7", 90)],
        TopPaths = [new TrafficCount("/login", 800)],
        TopAsns = [new TrafficCount("14061", 960)],
        Classification = "distributed_asn_flood",
        SuggestedRule = new SuggestedRule("asn", "14061", "challenge"),
        Action = IncidentAction.Shadowed,
    };

    [Fact]
    public async Task Saves_and_reads_back_newest_first_with_payload() {
        NpgsqlIncidentArchive archive = this.Archive;
        // EnsureSchema is idempotent — InitializeAsync already called it once.
        await archive.EnsureSchemaAsync(CancellationToken.None);

        DateTimeOffset t0 = DateTimeOffset.UtcNow;
        await archive.SaveAsync(Incident("inc-old", "a.example.com", t0), CancellationToken.None);
        await archive.SaveAsync(Incident("inc-new", "b.example.com", t0.AddSeconds(5)), CancellationToken.None);
        // Duplicate id is ignored (ON CONFLICT DO NOTHING).
        await archive.SaveAsync(Incident("inc-new", "b.example.com", t0.AddSeconds(5)), CancellationToken.None);

        IReadOnlyList<TrafficIncident> recent = await archive.RecentAsync(10, CancellationToken.None);

        Assert.Equal(2, recent.Count);
        Assert.Equal("inc-new", recent[0].Id);   // newest first
        Assert.Equal("inc-old", recent[1].Id);

        TrafficIncident newest = recent[0];
        Assert.Equal("b.example.com", newest.Zone);
        Assert.Equal("distributed_asn_flood", newest.Classification);
        Assert.Equal(IncidentAction.Shadowed, newest.Action);
        Assert.Equal("asn", newest.SuggestedRule?.ConditionType);
        Assert.Equal("14061", newest.SuggestedRule?.Value);
        Assert.Contains("ml_rate_spike", newest.Signals);
        Assert.Equal("14061", newest.TopAsns[0].Value);
    }

    [Fact]
    public async Task Recent_limit_is_respected() {
        NpgsqlIncidentArchive archive = this.Archive;

        DateTimeOffset t0 = DateTimeOffset.UtcNow;
        for(int i = 0; i < 5; i++)
            await archive.SaveAsync(Incident($"lim-{i}", "c.example.com", t0.AddSeconds(i)), CancellationToken.None);

        IReadOnlyList<TrafficIncident> recent = await archive.RecentAsync(3, CancellationToken.None);
        Assert.Equal(3, recent.Count);
        Assert.Equal("lim-4", recent[0].Id);   // newest of the batch
    }
}

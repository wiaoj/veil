using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;

namespace Veil.Analytics.Intelligence;

/// <summary>
/// Durable store for AI traffic incidents. The <see cref="IncidentStore"/> ring
/// is the fast, process-local "live" view that the dashboard reads; this archive
/// makes incidents survive a worker restart (the ring is hydrated from here on
/// startup) and gives the feed history beyond the ring's capacity.
/// </summary>
public interface IIncidentArchive {
    Task EnsureSchemaAsync(CancellationToken cancellationToken);
    Task SaveAsync(TrafficIncident incident, CancellationToken cancellationToken);
    /// <summary>Most recent incidents, newest first.</summary>
    Task<IReadOnlyList<TrafficIncident>> RecentAsync(int limit, CancellationToken cancellationToken);
}

/// <summary>
/// No-op archive used when no PostgreSQL connection is configured (e.g. a
/// ClickHouse-only setup). Keeps the bus handler + hydration service resolvable
/// without persisting anything — incidents stay in the in-memory ring only.
/// </summary>
public sealed class NullIncidentArchive : IIncidentArchive {
    public Task EnsureSchemaAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task SaveAsync(TrafficIncident incident, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<IReadOnlyList<TrafficIncident>> RecentAsync(int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TrafficIncident>>([]);
}

/// <summary>
/// PostgreSQL incident archive. Raw Npgsql, no EF — same thin-client philosophy
/// as <see cref="Aggregation.DailySummaryStore"/>. The full incident is stored
/// as <c>jsonb</c> (it has nested arrays/records) alongside a few flat columns
/// for ordering and ad-hoc querying. Lives in its own <c>intelligence</c> schema.
/// </summary>
public sealed class NpgsqlIncidentArchive(string connectionString) : IIncidentArchive {
    /// <summary>Hard cap on archived rows; oldest beyond this are pruned on write.</summary>
    private const int RetentionLimit = 5000;

    // Enum-as-string so payloads round-trip readable (matches the alert sinks).
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken) {
        const string ddl = """
            CREATE SCHEMA IF NOT EXISTS intelligence;
            CREATE TABLE IF NOT EXISTS intelligence.incidents (
                id              text        NOT NULL PRIMARY KEY,
                detected_at     timestamptz NOT NULL,
                zone            text        NOT NULL,
                anomaly_score   int         NOT NULL,
                classification  text        NOT NULL,
                action          text        NOT NULL,
                payload         jsonb       NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_incidents_detected_at
                ON intelligence.incidents (detected_at DESC);
            """;

        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = new(ddl, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveAsync(TrafficIncident incident, CancellationToken cancellationToken) {
        const string sql = """
            INSERT INTO intelligence.incidents
                (id, detected_at, zone, anomaly_score, classification, action, payload)
            VALUES (@id, @detected_at, @zone, @anomaly_score, @classification, @action, @payload)
            ON CONFLICT (id) DO NOTHING;

            DELETE FROM intelligence.incidents
            WHERE id NOT IN (
                SELECT id FROM intelligence.incidents
                ORDER BY detected_at DESC
                LIMIT @keep
            );
            """;

        string payload = JsonSerializer.Serialize(incident, Json);

        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("id", incident.Id);
        command.Parameters.AddWithValue("detected_at", incident.DetectedAtUtc);
        command.Parameters.AddWithValue("zone", incident.Zone);
        command.Parameters.AddWithValue("anomaly_score", incident.AnomalyScore);
        command.Parameters.AddWithValue("classification", incident.Classification);
        command.Parameters.AddWithValue("action", incident.Action.ToString());
        command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = payload });
        command.Parameters.AddWithValue("keep", RetentionLimit);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TrafficIncident>> RecentAsync(int limit, CancellationToken cancellationToken) {
        const string sql = """
            SELECT payload FROM intelligence.incidents
            ORDER BY detected_at DESC
            LIMIT @limit;
            """;

        List<TrafficIncident> incidents = [];
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("limit", limit);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while(await reader.ReadAsync(cancellationToken)) {
            string payload = reader.GetString(0);
            TrafficIncident? incident = JsonSerializer.Deserialize<TrafficIncident>(payload, Json);
            if(incident is not null)
                incidents.Add(incident);
        }
        return incidents;
    }
}

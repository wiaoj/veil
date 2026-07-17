using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;

namespace Veil.Analytics.ClickHouse;

/// <summary>
/// Thin client over the ClickHouse HTTP interface — bulk inserts via
/// <c>FORMAT JSONEachRow</c> (one JSON object per line), no driver
/// dependency. Errors propagate to the caller, which decides whether the
/// batch is dropped.
/// </summary>
public sealed class ClickHouseWriter(IHttpClientFactory httpClientFactory, IOptions<ClickHouseOptions> options) {
    public const string HttpClientName = "clickhouse";
    public const string TableName = "request_logs";
    public const string InteractionTableName = "challenge_interactions";

    private static readonly JsonSerializerOptions RowSerializerOptions = new();

    private readonly ClickHouseOptions options = options.Value;

    /// <summary>
    /// Creates the request log table if it does not exist yet. Idempotent;
    /// called at worker startup.
    /// </summary>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken) {
        string ddl = $"""
            CREATE TABLE IF NOT EXISTS {TableName} (
                ts DateTime64(3, 'UTC'),
                node_id LowCardinality(String),
                zone LowCardinality(String),
                host String,
                method LowCardinality(String),
                path String,
                status UInt16,
                verdict LowCardinality(String),
                rule_id String,
                client_ip String,
                user_agent String,
                duration_ms UInt32,
                asn UInt32 DEFAULT 0
            )
            ENGINE = MergeTree
            PARTITION BY toYYYYMMDD(ts)
            ORDER BY (zone, ts)
            TTL toDateTime(ts) + INTERVAL {this.options.RetentionDays} DAY
            """;

        await ExecuteAsync(ddl, body: null, cancellationToken);

        // Idempotently add columns introduced after a table was first created,
        // so existing deployments pick them up without a manual migration.
        await ExecuteAsync(
            $"ALTER TABLE {TableName} ADD COLUMN IF NOT EXISTS asn UInt32 DEFAULT 0",
            body: null, cancellationToken);
    }

    public async Task InsertAsync(IReadOnlyList<Ingestion.RequestLogRow> rows, CancellationToken cancellationToken) {
        StringBuilder ndjson = new(rows.Count * 256);
        foreach(Ingestion.RequestLogRow row in rows)
            ndjson.AppendLine(JsonSerializer.Serialize(row, RowSerializerOptions));

        await ExecuteAsync(
            $"INSERT INTO {TableName} FORMAT JSONEachRow",
            ndjson.ToString(),
            cancellationToken);
    }

    /// <summary>Creates the human-verification interaction table if absent.
    /// Idempotent; called at worker startup.</summary>
    public async Task EnsureInteractionSchemaAsync(CancellationToken cancellationToken) {
        string ddl = $"""
            CREATE TABLE IF NOT EXISTS {InteractionTableName} (
                ts DateTime64(3, 'UTC'),
                node_id LowCardinality(String),
                zone LowCardinality(String),
                kind LowCardinality(String),
                tier UInt8,
                outcome LowCardinality(String),
                reason LowCardinality(String),
                client_ip String,
                asn UInt32 DEFAULT 0,
                country LowCardinality(String),
                event_count UInt32 DEFAULT 0,
                path_length Float64 DEFAULT 0,
                straight_line Float64 DEFAULT 0,
                duration_ms UInt32 DEFAULT 0,
                time_to_first_ms UInt32 DEFAULT 0,
                timing_jitter_ms Float64 DEFAULT 0
            )
            ENGINE = MergeTree
            PARTITION BY toYYYYMMDD(ts)
            ORDER BY (zone, ts)
            TTL toDateTime(ts) + INTERVAL {this.options.RetentionDays} DAY
            """;

        await ExecuteAsync(ddl, body: null, cancellationToken);
    }

    public async Task InsertInteractionsAsync(
        IReadOnlyList<Ingestion.InteractionRow> rows, CancellationToken cancellationToken) {
        StringBuilder ndjson = new(rows.Count * 256);
        foreach(Ingestion.InteractionRow row in rows)
            ndjson.AppendLine(JsonSerializer.Serialize(row, RowSerializerOptions));

        await ExecuteAsync(
            $"INSERT INTO {InteractionTableName} FORMAT JSONEachRow",
            ndjson.ToString(),
            cancellationToken);
    }

    private async Task ExecuteAsync(string query, string? body, CancellationToken cancellationToken) {
        // best_effort lets DateTime64 parse the ISO 8601 timestamps the rows
        // serialise to.
        string url = $"{this.options.Url.TrimEnd('/')}/" +
            $"?database={Uri.EscapeDataString(this.options.Database)}" +
            $"&query={Uri.EscapeDataString(query)}" +
            "&date_time_input_format=best_effort";

        using HttpRequestMessage request = new(HttpMethod.Post, url);
        request.Headers.Add("X-ClickHouse-User", this.options.Username);
        request.Headers.Add("X-ClickHouse-Key", this.options.Password);
        if(body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson");

        HttpClient client = httpClientFactory.CreateClient(HttpClientName);
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);

        if(!response.IsSuccessStatusCode) {
            string detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"ClickHouse returned {(int)response.StatusCode}: {Truncate(detail)}");
        }
    }

    private static string Truncate(string value) {
        return value.Length <= 500 ? value : value[..500];
    }
}

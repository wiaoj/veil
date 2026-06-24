using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Veil.Analytics.ClickHouse;
using Veil.Analytics.Ingestion;
using Xunit;

namespace Veil.IntegrationTests;

/// <summary>
/// <see cref="ClickHouseWriter"/> + <see cref="ClickHouseReader"/> against a real
/// ClickHouse: schema creation (incl. the <c>asn</c> column added later) and a
/// JSONEachRow insert/read round-trip that proves ASN survives the pipeline.
/// </summary>
[Collection(nameof(ClickHouseCollection))]
public sealed class ClickHouseLogsTests(ClickHouseFixture clickhouse) {
    private (ClickHouseWriter Writer, ClickHouseReader Reader) Clients() {
        IOptions<ClickHouseOptions> options = Options.Create(new ClickHouseOptions {
            Url = $"http://{clickhouse.Host}:{clickhouse.HttpPort}",
            Username = "veil",
            Password = "veil",
            Database = "veil",
            RetentionDays = 7,
        });
        return (new ClickHouseWriter(TestInfra.HttpClientFactory, options),
                new ClickHouseReader(TestInfra.HttpClientFactory, options));
    }

    private sealed record AsnRow([property: JsonPropertyName("client_ip")] string ClientIp,
                                 [property: JsonPropertyName("asn")] uint Asn);

    private static RequestLogRow Row(string clientIp, uint asn) => new(
        DateTime.UtcNow, "node-1", "z.example.com", "z.example.com", "GET", "/", 200,
        "allow", "-", clientIp, "test-agent", 5, asn);

    [Fact]
    public async Task Insert_then_read_round_trips_asn() {
        (ClickHouseWriter writer, ClickHouseReader reader) = this.Clients();
        await writer.EnsureSchemaAsync(CancellationToken.None);
        // Idempotent (also re-runs the ALTER ADD COLUMN IF NOT EXISTS).
        await writer.EnsureSchemaAsync(CancellationToken.None);

        await writer.InsertAsync(
            [Row("203.0.113.10", 14061), Row("203.0.113.11", 0)],
            CancellationToken.None);

        List<AsnRow> rows = await reader.QueryAsync<AsnRow>(
            "SELECT client_ip, asn FROM request_logs ORDER BY client_ip",
            parameters: null,
            CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Equal(14061u, rows[0].Asn);   // 203.0.113.10
        Assert.Equal(0u, rows[1].Asn);       // 203.0.113.11 (unknown ASN)
    }
}

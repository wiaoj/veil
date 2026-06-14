using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using Veil.Analytics.ClickHouse;
using Wiaoj.Endpoints;

namespace Veil.Analytics.Features.Queries;

/// <summary>
/// Server-Sent Events stream of live request-log entries (Phase 7.5). The
/// control plane short-polls ClickHouse for rows newer than a per-connection
/// cursor and writes each as an SSE <c>data:</c> event. One-way push, so SSE
/// fits — it rides the existing HTTP/JWT path and reconnects for free.
/// </summary>
public sealed class StreamRequestLogsEndpoint : IEndpoint {
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private const int BatchCap = 200;

    private static readonly JsonSerializerOptions Json = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void Map(IEndpointRouteBuilder app) {
        app.MapGet("/v1/analytics/stream", Handle)
           .WithName("StreamRequestLogs")
           .WithTags("Analytics")
           .WithSummary("Live request-log stream (SSE)")
           .WithDescription("text/event-stream of request-log entries as they land, newest after connect. Optional ?zone= filter.");
    }

    private sealed record Row(
        [property: JsonPropertyName("ts_ms")] long TsMs,
        [property: JsonPropertyName("zone")] string Zone,
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("status")] int Status,
        [property: JsonPropertyName("verdict")] string Verdict,
        [property: JsonPropertyName("client_ip")] string ClientIp);

    private sealed record StreamEvent(
        long TsMs,
        string Zone,
        string Method,
        string Path,
        int Status,
        string Verdict,
        string ClientIp);

    private static async Task Handle(
        HttpContext http,
        ClickHouseReader reader,
        ILoggerFactory loggerFactory,
        string? zone = null) {

        CancellationToken cancellationToken = http.RequestAborted;
        ILogger logger = loggerFactory.CreateLogger<StreamRequestLogsEndpoint>();

        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        // Disable proxy buffering so events flush immediately.
        http.Response.Headers["X-Accel-Buffering"] = "no";

        // Only stream entries that arrive after the client connects.
        long cursor = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        string where = "ts > fromUnixTimestamp64Milli({cursor:Int64})";
        if(!string.IsNullOrWhiteSpace(zone))
            where += " AND zone = {zone:String}";

        // Initial comment line opens the stream (some proxies need first bytes).
        await http.Response.WriteAsync(": connected\n\n", cancellationToken);
        await http.Response.Body.FlushAsync(cancellationToken);

        try {
            while(!cancellationToken.IsCancellationRequested) {
                List<Row> rows;
                try {
                    Dictionary<string, string> parameters = new() { ["cursor"] = cursor.ToString() };
                    if(!string.IsNullOrWhiteSpace(zone))
                        parameters["zone"] = zone;

                    rows = await reader.QueryAsync<Row>($"""
                        SELECT
                            toUnixTimestamp64Milli(ts) AS ts_ms,
                            zone, method, path, status, verdict, client_ip
                        FROM request_logs
                        WHERE {where}
                        ORDER BY ts ASC
                        LIMIT {BatchCap}
                        """, parameters, cancellationToken);
                }
                catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) {
                    break;
                }
                catch(Exception ex) {
                    // A transient ClickHouse error must not drop the stream.
                    logger.LogDebug(ex, "Live stream poll failed; retrying");
                    rows = [];
                }

                foreach(Row row in rows) {
                    if(row.TsMs > cursor)
                        cursor = row.TsMs;
                    var payload = JsonSerializer.Serialize(
                        new StreamEvent(row.TsMs, row.Zone, row.Method, row.Path, row.Status, row.Verdict, row.ClientIp),
                        Json);
                    await http.Response.WriteAsync($"data: {payload}\n\n", cancellationToken);
                }

                if(rows.Count > 0)
                    await http.Response.Body.FlushAsync(cancellationToken);

                await Task.Delay(PollInterval, cancellationToken);
            }
        }
        catch(OperationCanceledException) {
            // Client disconnected — normal.
        }
    }
}

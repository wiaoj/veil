using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Veil.Analytics.Ingestion;

namespace Veil.Analytics.Siem;

/// <summary>
/// Posts request-log batches to a SIEM endpoint as newline-delimited JSON
/// (one <see cref="RequestLogRow"/> per line — the common ingestion format for
/// Splunk HEC raw, Elastic, Loki push adapters, etc.). Errors are swallowed and
/// logged: SIEM delivery is best-effort and must not affect the pipeline.
/// </summary>
public sealed class HttpSiemExporter(
    IHttpClientFactory httpClientFactory,
    IOptions<SiemOptions> options,
    ILogger<HttpSiemExporter> logger) : ISiemExporter {

    public const string HttpClientName = "siem";

    private readonly SiemOptions _options = options.Value;

    public async Task ExportAsync(IReadOnlyList<RequestLogRow> batch, CancellationToken cancellationToken) {
        if(batch.Count == 0 || string.IsNullOrWhiteSpace(this._options.Endpoint))
            return;

        try {
            StringBuilder ndjson = new(batch.Count * 256);
            foreach(RequestLogRow row in batch)
                ndjson.Append(JsonSerializer.Serialize(row)).Append('\n');

            using HttpRequestMessage request = new(HttpMethod.Post, this._options.Endpoint) {
                Content = new StringContent(ndjson.ToString(), Encoding.UTF8, "application/x-ndjson")
            };
            if(!string.IsNullOrWhiteSpace(this._options.ApiKeyHeader) && !string.IsNullOrWhiteSpace(this._options.ApiKey))
                request.Headers.TryAddWithoutValidation(this._options.ApiKeyHeader, this._options.ApiKey);

            HttpClient client = httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            if(!response.IsSuccessStatusCode)
                logger.LogWarning("SIEM export returned {Status} for {Count} rows", response.StatusCode, batch.Count);
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) {
            // Shutting down — ignore.
        }
        catch(Exception ex) {
            logger.LogWarning(ex, "SIEM export failed for {Count} rows (dropped)", batch.Count);
        }
    }
}

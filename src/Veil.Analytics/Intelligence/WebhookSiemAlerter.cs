using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Veil.Analytics.Siem;

namespace Veil.Analytics.Intelligence;

/// <summary>
/// Sends AI incidents to two reused channels (Phase 11.3 alerting):
/// <list type="bullet">
/// <item>a <b>webhook</b> — the incident as a JSON POST (mirrors the edge's
/// attack-webhook idea, but fired from the control plane on AI detection);</item>
/// <item><b>SIEM</b> — the incident as a single NDJSON line to the same endpoint
/// the request-log exporter already uses (<c>Siem</c> section).</item>
/// </list>
/// Both are best-effort and independent; a failure in one never blocks the other
/// or the analysis loop.
/// </summary>
public sealed class WebhookSiemAlerter(
    IHttpClientFactory httpClientFactory,
    IOptions<IntelligenceOptions> intelligenceOptions,
    IOptions<SiemOptions> siemOptions,
    ILogger<WebhookSiemAlerter> logger) : IIncidentAlerter {

    public const string HttpClientName = "intelligence-alert";

    // Enum-as-string so "Shadowed"/"Enforced" land in the payload, not ordinals.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IntelligenceOptions _options = intelligenceOptions.Value;
    private readonly SiemOptions _siem = siemOptions.Value;

    public async Task AlertAsync(TrafficIncident incident, CancellationToken cancellationToken) {
        string payload = JsonSerializer.Serialize(incident, Json);
        HttpClient client = httpClientFactory.CreateClient(HttpClientName);

        await Task.WhenAll(
            SendWebhookAsync(client, payload, cancellationToken),
            MirrorToSiemAsync(client, payload, cancellationToken));
    }

    private async Task SendWebhookAsync(HttpClient client, string payload, CancellationToken cancellationToken) {
        if(string.IsNullOrWhiteSpace(this._options.WebhookUrl))
            return;
        try {
            using HttpRequestMessage request = new(HttpMethod.Post, this._options.WebhookUrl) {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            if(!string.IsNullOrWhiteSpace(this._options.WebhookAuthHeader)
               && !string.IsNullOrWhiteSpace(this._options.WebhookAuthValue))
                request.Headers.TryAddWithoutValidation(this._options.WebhookAuthHeader, this._options.WebhookAuthValue);

            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            if(!response.IsSuccessStatusCode)
                logger.LogWarning("Incident webhook returned {Status}", response.StatusCode);
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) { }
        catch(Exception ex) {
            logger.LogWarning(ex, "Incident webhook failed");
        }
    }

    private async Task MirrorToSiemAsync(HttpClient client, string payload, CancellationToken cancellationToken) {
        if(!this._options.MirrorIncidentsToSiem || string.IsNullOrWhiteSpace(this._siem.Endpoint))
            return;
        try {
            // One NDJSON line, same content type the request-log exporter uses.
            using HttpRequestMessage request = new(HttpMethod.Post, this._siem.Endpoint) {
                Content = new StringContent(payload + "\n", Encoding.UTF8, "application/x-ndjson")
            };
            if(!string.IsNullOrWhiteSpace(this._siem.ApiKeyHeader) && !string.IsNullOrWhiteSpace(this._siem.ApiKey))
                request.Headers.TryAddWithoutValidation(this._siem.ApiKeyHeader, this._siem.ApiKey);

            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            if(!response.IsSuccessStatusCode)
                logger.LogWarning("Incident SIEM mirror returned {Status}", response.StatusCode);
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) { }
        catch(Exception ex) {
            logger.LogWarning(ex, "Incident SIEM mirror failed");
        }
    }
}

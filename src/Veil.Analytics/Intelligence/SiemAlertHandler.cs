using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tyto;
using Veil.Analytics.Siem;

namespace Veil.Analytics.Intelligence;

/// <summary>
/// Bus subscriber (Phase 12 Slice 3): mirrors each AI incident to the SIEM as a
/// single NDJSON line, reusing the same endpoint the request-log exporter uses
/// (<c>Siem</c> section). Best-effort — no-ops when SIEM mirroring is off or no
/// endpoint is configured, and never throws.
/// </summary>
public sealed class SiemAlertHandler(
    IHttpClientFactory httpClientFactory,
    IOptions<IntelligenceOptions> intelligenceOptions,
    IOptions<SiemOptions> siemOptions,
    ILogger<SiemAlertHandler> logger) : IEventHandler<IncidentRaised> {

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IntelligenceOptions _options = intelligenceOptions.Value;
    private readonly SiemOptions _siem = siemOptions.Value;

    public async ValueTask HandleAsync(IMessageContext<IncidentRaised> context, CancellationToken cancellationToken = default) {
        if(!this._options.MirrorIncidentsToSiem || string.IsNullOrWhiteSpace(this._siem.Endpoint))
            return;

        string payload = JsonSerializer.Serialize(context.Message.Incident, Json);
        try {
            // One NDJSON line, same content type the request-log exporter uses.
            HttpClient client = httpClientFactory.CreateClient(WebhookAlertHandler.HttpClientName);
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

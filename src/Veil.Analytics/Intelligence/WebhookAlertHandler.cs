using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tyto;

namespace Veil.Analytics.Intelligence;

/// <summary>
/// Bus subscriber (Phase 12 Slice 3): posts each AI incident to the configured
/// webhook as a JSON body. Best-effort — no-ops when no webhook is configured,
/// never throws, so a webhook outage can't disrupt the analysis loop or the
/// SIEM mirror (a separate handler).
/// </summary>
public sealed class WebhookAlertHandler(
    IHttpClientFactory httpClientFactory,
    IOptions<IntelligenceOptions> intelligenceOptions,
    ILogger<WebhookAlertHandler> logger) : IEventHandler<IncidentRaised> {

    public const string HttpClientName = "intelligence-alert";

    // Enum-as-string so "Shadowed"/"Enforced" land in the payload, not ordinals.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IntelligenceOptions _options = intelligenceOptions.Value;

    public async ValueTask HandleAsync(IMessageContext<IncidentRaised> context, CancellationToken cancellationToken = default) {
        if(string.IsNullOrWhiteSpace(this._options.WebhookUrl))
            return;

        string payload = JsonSerializer.Serialize(context.Message.Incident, Json);
        try {
            HttpClient client = httpClientFactory.CreateClient(HttpClientName);
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
}

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Veil.Analytics.Intelligence;

/// <summary>
/// Triages anomalies with Claude via the Anthropic Messages API. Uses structured
/// outputs (<c>output_config.format</c>) so the reply is a parseable verdict, and
/// adaptive thinking (the required mode for Opus 4.8). Hand-rolled over
/// <see cref="IHttpClientFactory"/> to match the codebase's thin-client idiom
/// (ClickHouse, SIEM) rather than pulling in the SDK.
/// </summary>
public sealed class AnthropicAnalystClient(
    IHttpClientFactory httpClientFactory,
    IOptions<IntelligenceOptions> options,
    ILogger<AnthropicAnalystClient> logger) : IAnalystClient {

    public const string HttpClientName = "anthropic";
    private const string AnthropicVersion = "2023-06-01";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IntelligenceOptions _options = options.Value;

    public async Task<AnalystVerdict?> AnalyzeAsync(TrafficIncident incident, CancellationToken cancellationToken) {
        if(string.IsNullOrWhiteSpace(this._options.AnthropicApiKey))
            return null;

        try {
            object body = BuildRequestBody(incident);
            using HttpRequestMessage request = new(HttpMethod.Post, this._options.ApiBaseUrl) {
                Content = new StringContent(JsonSerializer.Serialize(body, SerializerOptions), Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("x-api-key", this._options.AnthropicApiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            HttpClient client = httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            if(!response.IsSuccessStatusCode) {
                logger.LogWarning("Anthropic triage returned {Status} for zone {Zone}", response.StatusCode, incident.Zone);
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseVerdict(json);
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) {
            return null;
        }
        catch(Exception ex) {
            logger.LogWarning(ex, "Anthropic triage failed for zone {Zone}", incident.Zone);
            return null;
        }
    }

    private object BuildRequestBody(TrafficIncident incident) => new {
        model = this._options.Model,
        max_tokens = 1024,
        thinking = new { type = "adaptive" },
        system =
            "You are a senior edge-security analyst for an L7 WAF. You receive a statistical summary " +
            "of one zone's HTTP traffic over a short window that was already flagged as anomalous. " +
            "Classify the most likely cause, give a calibrated confidence (0..1), a concise human " +
            "summary, and — only when you are confident a single matcher would mitigate it without " +
            "harming legitimate users — a suggested edge rule. Prefer 'challenge' over 'block' when " +
            "unsure. If the spike looks like legitimate traffic, classify it 'benign_spike' with no rule.",
        messages = new[] {
            new { role = "user", content = BuildPrompt(incident) }
        },
        output_config = new { format = new { type = "json_schema", schema = VerdictSchema } }
    };

    private static string BuildPrompt(TrafficIncident incident) {
        StringBuilder sb = new();
        sb.AppendLine($"Zone: {incident.Zone}");
        sb.AppendLine($"Anomaly score: {incident.AnomalyScore}/100");
        sb.AppendLine($"Triggered signals: {string.Join(", ", incident.Signals)}");
        sb.AppendLine($"Request rate: {incident.RatePerSecond:F1} req/s (baseline ~{incident.BaselineRatePerSecond:F1} req/s)");
        sb.AppendLine($"Enforced block/rate-limit ratio: {incident.BlockedRatio:P0}");
        sb.AppendLine($"Distinct client IPs this window: {incident.DistinctIps}");
        sb.AppendLine("Top client IPs (ip × count): " +
            string.Join(", ", incident.TopIps.Select(static t => $"{t.Value}×{t.Count}")));
        sb.AppendLine("Top paths (path × count): " +
            string.Join(", ", incident.TopPaths.Select(static t => $"{t.Value}×{t.Count}")));
        return sb.ToString();
    }

    private static readonly object VerdictSchema = new Dictionary<string, object> {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["properties"] = new Dictionary<string, object> {
            ["classification"] = new Dictionary<string, object> {
                ["type"] = "string",
                ["enum"] = new[] { "http_flood", "credential_stuffing", "scraping", "scanning", "injection_probe", "benign_spike", "unknown" }
            },
            ["confidence"] = new Dictionary<string, object> { ["type"] = "number" },
            ["summary"] = new Dictionary<string, object> { ["type"] = "string" },
            ["suggested_rule"] = new Dictionary<string, object> {
                ["anyOf"] = new object[] {
                    new Dictionary<string, object> {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["properties"] = new Dictionary<string, object> {
                            ["condition_type"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["enum"] = new[] { "ip", "country", "asn", "path_regex", "user_agent" }
                            },
                            ["value"] = new Dictionary<string, object> { ["type"] = "string" },
                            ["action"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["enum"] = new[] { "block", "challenge", "rate_limit" }
                            }
                        },
                        ["required"] = new[] { "condition_type", "value", "action" }
                    },
                    new Dictionary<string, object> { ["type"] = "null" }
                }
            }
        },
        ["required"] = new[] { "classification", "confidence", "summary", "suggested_rule" }
    };

    private static AnalystVerdict? ParseVerdict(string responseJson) {
        using JsonDocument doc = JsonDocument.Parse(responseJson);
        if(!doc.RootElement.TryGetProperty("content", out JsonElement content))
            return null;

        foreach(JsonElement block in content.EnumerateArray()) {
            if(block.TryGetProperty("type", out JsonElement type) && type.GetString() == "text"
               && block.TryGetProperty("text", out JsonElement text)) {
                VerdictDto? dto = JsonSerializer.Deserialize<VerdictDto>(text.GetString() ?? "", SerializerOptions);
                if(dto is null)
                    return null;
                SuggestedRule? rule = dto.SuggestedRule is { } r
                    ? new SuggestedRule(r.ConditionType, r.Value, r.Action)
                    : null;
                return new AnalystVerdict(dto.Classification, dto.Confidence, dto.Summary, rule);
            }
        }
        return null;
    }

    private sealed record VerdictDto(
        [property: JsonPropertyName("classification")] string Classification,
        [property: JsonPropertyName("confidence")] double Confidence,
        [property: JsonPropertyName("summary")] string Summary,
        [property: JsonPropertyName("suggested_rule")] SuggestedRuleDto? SuggestedRule);

    private sealed record SuggestedRuleDto(
        [property: JsonPropertyName("condition_type")] string ConditionType,
        [property: JsonPropertyName("value")] string Value,
        [property: JsonPropertyName("action")] string Action);
}

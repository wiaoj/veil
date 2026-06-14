using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Veil.Analytics.Intelligence;

/// <summary>
/// Applies an AI-suggested rule by calling the control plane (Veil.Api) over its
/// authenticated REST API. The worker owns the live stream; Zones live in
/// Veil.Api, so this resolves the zone by hostname and POSTs a rule.
///
/// Shadow vs enforce maps onto the rule action: an enforced rule uses the real
/// action (block / challenge / rate_limit); a shadowed rule uses <c>Log</c> —
/// it is still evaluated and logged at the edge but never blocks traffic, giving
/// per-rule shadowing without touching the zone-wide shadow flag.
/// </summary>
public sealed class HttpRuleApplier(
    IHttpClientFactory httpClientFactory,
    IOptions<IntelligenceOptions> options,
    ILogger<HttpRuleApplier> logger) : IRuleApplier {

    public const string HttpClientName = "control-plane";
    private const string ApiKeyHeader = "X-Api-Key";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntelligenceOptions _options = options.Value;

    public async Task ApplyAsync(string zone, SuggestedRule rule, bool shadow, CancellationToken cancellationToken) {
        RuleConditionRequest? condition = MapCondition(rule);
        if(condition is null) {
            logger.LogWarning("AI rule for {Zone} skipped: unmappable condition '{Type}'", zone, rule.ConditionType);
            return;
        }

        try {
            HttpClient client = httpClientFactory.CreateClient(HttpClientName);
            client.BaseAddress = new Uri(this._options.ControlPlaneUrl);
            client.DefaultRequestHeaders.Remove(ApiKeyHeader);
            client.DefaultRequestHeaders.Add(ApiKeyHeader, this._options.ControlPlaneApiKey);

            string? zoneId = await ResolveZoneIdAsync(client, zone, cancellationToken);
            if(zoneId is null) {
                logger.LogWarning("AI rule for {Zone} skipped: zone not found in control plane", zone);
                return;
            }

            (string action, RateLimitRequest? rateLimit) = MapAction(rule.Action, shadow);
            AddRuleRequest body = new(
                Name: $"AI {(shadow ? "shadow" : "auto")}: {rule.ConditionType}={rule.Value}",
                Priority: 50,
                Action: action,
                Conditions: [condition],
                RateLimit: rateLimit);

            using HttpResponseMessage response =
                await client.PostAsJsonAsync($"/v1/zones/{zoneId}/rules", body, Json, cancellationToken);

            if(response.IsSuccessStatusCode)
                logger.LogInformation("AI rule {Mode} on {Zone}: {Type}={Value} → {Action}",
                    shadow ? "SHADOW" : "ENFORCE", zone, rule.ConditionType, rule.Value, action);
            else
                logger.LogWarning("AI rule apply on {Zone} returned {Status}", zone, response.StatusCode);
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) {
            // Shutting down.
        }
        catch(Exception ex) {
            logger.LogWarning(ex, "AI rule apply failed for {Zone}", zone);
        }
    }

    private async Task<string?> ResolveZoneIdAsync(HttpClient client, string hostname, CancellationToken cancellationToken) {
        ListZonesResponse? zones =
            await client.GetFromJsonAsync<ListZonesResponse>("/v1/zones?pageSize=200", Json, cancellationToken);
        return zones?.Items.FirstOrDefault(z =>
            string.Equals(z.Hostname, hostname, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    /// <summary>Maps the suggestion's condition vocabulary onto the flat rule-condition DTO.</summary>
    private static RuleConditionRequest? MapCondition(SuggestedRule rule) => rule.ConditionType switch {
        "ip" => new RuleConditionRequest("ip_match", Value: rule.Value),
        "country" => new RuleConditionRequest("country", Value: rule.Value),
        "asn" when int.TryParse(rule.Value, out int asn) => new RuleConditionRequest("asn", Asn: asn),
        "path_regex" => new RuleConditionRequest("path_regex", Value: rule.Value),
        "user_agent" => new RuleConditionRequest("user_agent", Value: rule.Value),
        _ => null
    };

    /// <summary>Shadow → Log (observe-only). Enforce → the real action.</summary>
    private (string Action, RateLimitRequest? RateLimit) MapAction(string suggested, bool shadow) {
        if(shadow)
            return ("Log", null);

        return suggested switch {
            "block" => ("Block", null),
            "rate_limit" => ("RateLimit",
                new RateLimitRequest(this._options.DefaultRateLimitRequests, this._options.DefaultRateLimitWindowSeconds)),
            _ => ("Challenge", null)   // default to the softer action
        };
    }

    // Minimal mirrors of the Veil.Api contracts (worker doesn't reference Veil.Zones).
    private sealed record AddRuleRequest(
        string Name, int Priority, string Action,
        List<RuleConditionRequest> Conditions, RateLimitRequest? RateLimit);

    private sealed record RuleConditionRequest(
        string Type, string? Value = null, string? Name = null, int? Asn = null, string? Mode = null);

    private sealed record RateLimitRequest(int Requests, int WindowSecs);

    private sealed record ListZonesResponse([property: JsonPropertyName("items")] List<ZoneItem> Items);
    private sealed record ZoneItem(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("hostname")] string Hostname);
}

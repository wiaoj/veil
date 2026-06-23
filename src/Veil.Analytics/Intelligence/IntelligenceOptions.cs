namespace Veil.Analytics.Intelligence;

/// <summary>
/// Configuration for the live AI traffic-analysis layer (Phase 11). The whole
/// layer is opt-in: when <see cref="Enabled"/> is false a no-op analyzer is
/// registered and the ingest hot path stays untouched.
/// </summary>
public sealed record IntelligenceOptions {
    public const string SectionName = "Intelligence";

    /// <summary>Master switch. When false, nothing runs and ingest is unaffected.</summary>
    public bool Enabled { get; init; }

    /// <summary>How often the in-memory windows are swept and scored, in seconds.</summary>
    public int IntervalSeconds { get; init; } = 10;

    /// <summary>ML spike-detector confidence (higher = fewer, stronger alerts).</summary>
    public double MlConfidence { get; init; } = 95.0;

    /// <summary>Rolling rate samples kept per zone (also the ML p-value history floor).</summary>
    public int RateHistoryLength { get; init; } = 60;

    /// <summary>Samples required before ML detection activates (warm-up).</summary>
    public int MlMinHistory { get; init; } = 20;

    /// <summary>Spikes below this absolute req/s are ignored (avoids noise on idle zones).</summary>
    public double MinRequestsPerSecond { get; init; } = 5.0;

    /// <summary>Enforced block+rate-limit ratio above this flags an attack.</summary>
    public double BlockedRatioThreshold { get; init; } = 0.4;

    /// <summary>Single-IP share of traffic above this flags a single-source flood.</summary>
    public double TopIpShareThreshold { get; init; } = 0.5;

    /// <summary>
    /// Single-ASN share of traffic above this flags a distributed flood
    /// concentrated in one network (many IPs, one provider) — caught even when
    /// no single IP dominates.
    /// </summary>
    public double AsnShareThreshold { get; init; } = 0.6;

    /// <summary>Anomaly score (0..100) at or above which an incident is raised.</summary>
    public int IncidentScoreThreshold { get; init; } = 60;

    /// <summary>Quiet period per zone after an incident, in seconds (avoids alert storms).</summary>
    public int CooldownSeconds { get; init; } = 300;

    /// <summary>Max in-memory incidents retained per process (drop-oldest).</summary>
    public int MaxIncidents { get; init; } = 200;

    /// <summary>Cap on distinct IP/path keys tracked per zone per interval (memory guard).</summary>
    public int MaxTrackedKeys { get; init; } = 10_000;

    // --- Claude (Anthropic API) triage ---

    /// <summary>Anthropic API key. When unset, statistical detection still runs but triage is skipped.</summary>
    public string? AnthropicApiKey { get; init; }

    /// <summary>Model id used for triage.</summary>
    public string Model { get; init; } = "claude-opus-4-8";

    /// <summary>Anthropic messages endpoint (override for proxies / gateways).</summary>
    public string ApiBaseUrl { get; init; } = "https://api.anthropic.com/v1/messages";

    // --- Automated action ---

    /// <summary>
    /// When true, a suggested rule whose confidence ≥ <see cref="AutoApplyMinConfidence"/>
    /// is applied automatically; otherwise it is staged in shadow mode. The hard
    /// safety floor against false positives.
    /// </summary>
    public bool AutoApplyRules { get; init; }

    /// <summary>Confidence (0..1) required to auto-enforce a rule rather than shadow it.</summary>
    public double AutoApplyMinConfidence { get; init; } = 0.9;

    // --- Control plane (Veil.Api) for applying rules ---

    /// <summary>Veil.Api base URL. When set with an API key, rules are really applied.</summary>
    public string ControlPlaneUrl { get; init; } = "http://localhost:5210";

    /// <summary>API key (X-Api-Key) for the control plane. Unset → applier only logs.</summary>
    public string? ControlPlaneApiKey { get; init; }

    /// <summary>Requests/window for an enforced rate_limit rule (the suggestion carries no numbers).</summary>
    public int DefaultRateLimitRequests { get; init; } = 100;
    public int DefaultRateLimitWindowSeconds { get; init; } = 60;

    // --- Alerting (incident → webhook / SIEM) ---

    /// <summary>Webhook URL that receives the incident as JSON. Unset → no webhook.</summary>
    public string? WebhookUrl { get; init; }

    /// <summary>Optional webhook auth header name, e.g. <c>Authorization</c>.</summary>
    public string? WebhookAuthHeader { get; init; }

    /// <summary>Optional webhook auth header value.</summary>
    public string? WebhookAuthValue { get; init; }

    /// <summary>Mirror incidents to the configured SIEM endpoint (reuses the <c>Siem</c> section).</summary>
    public bool MirrorIncidentsToSiem { get; init; } = true;
}

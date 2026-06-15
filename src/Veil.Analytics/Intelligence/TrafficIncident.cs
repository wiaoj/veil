namespace Veil.Analytics.Intelligence;

/// <summary>A key (IP or path) with its hit count over a window. Serializes cleanly.</summary>
public sealed record TrafficCount(string Value, int Count);

/// <summary>
/// A suggested edge rule produced by the AI triage. Mirrors the edge's rule
/// vocabulary loosely — the control plane is the authority on the exact schema,
/// so this is advisory until applied.
/// </summary>
public sealed record SuggestedRule(
    string ConditionType,   // e.g. "ip", "country", "asn", "path_regex"
    string Value,           // the matcher value, e.g. an IP/CIDR or country code
    string Action);         // "block" | "challenge" | "rate_limit"

/// <summary>The AI's assessment of one anomaly window.</summary>
public sealed record AnalystVerdict(
    string Classification,  // e.g. "http_flood", "credential_stuffing", "scraping", "scanning", "benign_spike"
    double Confidence,      // 0..1
    string Summary,         // human-readable explanation
    SuggestedRule? SuggestedRule);

/// <summary>How an action decision resolved for an incident.</summary>
public enum IncidentAction { None, Suggested, Shadowed, Enforced }

/// <summary>
/// One detected traffic anomaly: the statistical signal that triggered it, the
/// AI verdict (if triage ran), and what action was taken.
/// </summary>
public sealed record TrafficIncident {
    public required string Id { get; init; }
    public required DateTimeOffset DetectedAtUtc { get; init; }
    public required string Zone { get; init; }
    public required int AnomalyScore { get; init; }
    public required string[] Signals { get; init; }
    public required double RatePerSecond { get; init; }
    public required double BaselineRatePerSecond { get; init; }
    public required double BlockedRatio { get; init; }
    public required int DistinctIps { get; init; }
    public required TrafficCount[] TopIps { get; init; }
    public required TrafficCount[] TopPaths { get; init; }

    /// <summary>Deterministic, LLM-free classification derived from the signals.</summary>
    public required string Classification { get; init; }

    /// <summary>Deterministic rule proposed from the signals (no LLM needed).</summary>
    public SuggestedRule? SuggestedRule { get; init; }

    /// <summary>Optional LLM enrichment — null in ML-only mode.</summary>
    public AnalystVerdict? Verdict { get; set; }
    public IncidentAction Action { get; set; } = IncidentAction.None;
}

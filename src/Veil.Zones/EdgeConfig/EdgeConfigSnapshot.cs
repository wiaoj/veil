using System.Text.Json.Serialization;
using Veil.Shared;
using Veil.Zones.Domain;
using Veil.Zones.Domain.Enums;
using Veil.Zones.Domain.ValueObjects;

namespace Veil.Zones.EdgeConfig;

// The wire contract pushed to edge nodes. The edge's veil.json format is
// canonical — property names are snake_case and condition discriminators
// match the Rust serde model exactly (see edge/src/config/mod.rs). Domain
// concepts the edge cannot enforce yet (country/ASN/regex conditions, Log
// action, non-http upstreams) are omitted fail-safe: the whole rule (or
// zone) is dropped rather than enforcing a weakened version of it.

public sealed record EdgeConfigSnapshot(
    [property: JsonPropertyName("trust_forwarded_headers")] bool TrustForwardedHeaders,
    [property: JsonPropertyName("zones")] List<EdgeZoneConfig> Zones);

public sealed record EdgeZoneConfig(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("hosts")] List<string> Hosts,
    // Either a bare URL string (single target) or a structured
    // EdgeUpstreamConfig (multiple targets + strategy) — the edge accepts both.
    [property: JsonPropertyName("upstream")] object Upstream,
    [property: JsonPropertyName("rules")] List<EdgeRuleConfig> Rules,
    [property: JsonPropertyName("tls"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    EdgeZoneTlsConfig? Tls = null,
    [property: JsonPropertyName("cache"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    EdgeCacheConfig? Cache = null,
    [property: JsonPropertyName("managed_rules"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    EdgeManagedRulesConfig? ManagedRules = null,
    [property: JsonPropertyName("shadow"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool Shadow = false,
    [property: JsonPropertyName("challenge"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    EdgeChallengeConfig? Challenge = null);

/// <summary>Per-zone challenge knobs the edge consumes: Tier-2 risk threshold,
/// base PoW difficulty and pass-token lifetime.</summary>
public sealed record EdgeChallengeConfig(
    [property: JsonPropertyName("tier2_risk_threshold")] int Tier2RiskThreshold,
    [property: JsonPropertyName("base_difficulty")] int BaseDifficulty,
    [property: JsonPropertyName("token_ttl_secs")] int TokenTtlSecs);

/// <summary>Presence enables the edge response cache (defaults on the edge side).</summary>
public sealed record EdgeCacheConfig();

/// <summary>Managed signature toggles pushed to the edge (snake_case action).</summary>
public sealed record EdgeManagedRulesConfig(
    [property: JsonPropertyName("sql_injection")] bool SqlInjection,
    [property: JsonPropertyName("xss")] bool Xss,
    [property: JsonPropertyName("path_traversal")] bool PathTraversal,
    [property: JsonPropertyName("inspect_body")] bool InspectBody,
    [property: JsonPropertyName("action")] string Action);

/// <summary>Structured upstream sent when a zone has more than one target.</summary>
public sealed record EdgeUpstreamConfig(
    [property: JsonPropertyName("targets")] List<EdgeUpstreamTarget> Targets,
    [property: JsonPropertyName("strategy")] string Strategy);

public sealed record EdgeUpstreamTarget(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("weight")] int Weight);

/// <summary>
/// TLS material the edge serves for the zone's hosts (SNI). The key is the
/// decrypted PEM — pushes carry it to the node, which holds it in memory
/// only.
/// </summary>
public sealed record EdgeZoneTlsConfig(
    [property: JsonPropertyName("cert_pem")] string CertPem,
    [property: JsonPropertyName("key_pem")] string KeyPem);

public sealed record EdgeRuleConfig(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("priority")] int Priority,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("conditions")] List<EdgeConditionConfig> Conditions,
    [property: JsonPropertyName("rate_limit"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    EdgeRateLimitConfig? RateLimit = null);

public sealed record EdgeConditionConfig(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Name = null);

public sealed record EdgeRateLimitConfig(
    [property: JsonPropertyName("requests")] int Requests,
    [property: JsonPropertyName("window_secs")] int WindowSecs);

public static class EdgeConfigSnapshotBuilder {
    /// <param name="certificates">Active TLS material keyed by hostname
    /// (lowercase); zones without an entry are served plaintext.</param>
    public static EdgeConfigSnapshot Build(
        IReadOnlyList<Zone> zones,
        IObfuscator<RuleId> ruleObfuscator,
        IReadOnlyDictionary<string, EdgeZoneTlsConfig>? certificates = null) {
        List<EdgeZoneConfig> edgeZones = [];

        foreach(Zone zone in zones) {
            if(zone.Status is ZoneStatus.Error)
                continue;

            // The edge data plane speaks both http and https to upstreams
            // (TLS chosen from the URI scheme). A zone with no servable target
            // at all cannot be served.
            List<UpstreamTarget> targets = [.. zone.Upstream.Targets.Where(
                t => t.Url.Scheme is "http" or "https")];
            if(targets.Count == 0)
                continue;

            // Single target → a bare URL string (back-compat); multiple →
            // structured with the load-balancing strategy. The edge accepts both.
            object upstream = targets.Count == 1
                ? targets[0].Url.ToString().TrimEnd('/')
                : new EdgeUpstreamConfig(
                    [.. targets.Select(t => new EdgeUpstreamTarget(t.Url.ToString().TrimEnd('/'), t.Weight))],
                    MapStrategy(zone.Upstream.Strategy));

            // A paused zone passes traffic through unfiltered.
            List<EdgeRuleConfig> rules = zone.Status is ZoneStatus.Paused
                ? []
                : MapRules(zone, ruleObfuscator);

            EdgeZoneTlsConfig? tls = null;
            certificates?.TryGetValue(zone.Hostname.Value.ToLowerInvariant(), out tls);

            // A paused zone passes through unfiltered → no managed inspection either.
            EdgeManagedRulesConfig? managed = zone.Status is not ZoneStatus.Paused && zone.ManagedRules.IsActive
                ? new EdgeManagedRulesConfig(
                    zone.ManagedRules.SqlInjection,
                    zone.ManagedRules.Xss,
                    zone.ManagedRules.PathTraversal,
                    zone.ManagedRules.InspectBody,
                    zone.ManagedRules.Action == ManagedRuleAction.Challenge ? "challenge" : "block")
                : null;

            EdgeChallengeConfig? challenge = zone.Status is not ZoneStatus.Paused && zone.Challenge.Enabled
                ? new EdgeChallengeConfig(
                    zone.Challenge.RiskThreshold,
                    zone.Challenge.PowDifficulty.Value,
                    (int)zone.Challenge.TokenTtl.Value.TotalSeconds)
                : null;

            edgeZones.Add(new EdgeZoneConfig(
                zone.Hostname.Value,
                [zone.Hostname.Value],
                upstream,
                rules,
                tls,
                zone.CacheEnabled ? new EdgeCacheConfig() : null,
                managed,
                zone.Shadow,
                challenge));
        }

        return new EdgeConfigSnapshot(TrustForwardedHeaders: false, edgeZones);
    }

    private static List<EdgeRuleConfig> MapRules(Zone zone, IObfuscator<RuleId> ruleObfuscator) {
        List<EdgeRuleConfig> rules = [];

        foreach(Rule rule in zone.Rules.Where(r => r.IsEnabled).OrderBy(r => r.Priority)) {
            string? action = MapAction(rule.Action);
            if(action is null)
                continue;

            List<EdgeConditionConfig>? conditions = MapConditions(rule.Conditions);
            if(conditions is null)
                continue;

            rules.Add(new EdgeRuleConfig(
                ruleObfuscator.Encode(rule.Id),
                rule.Priority,
                action,
                conditions,
                rule.RateLimit is null
                    ? null
                    : new EdgeRateLimitConfig(rule.RateLimit.Requests, rule.RateLimit.WindowSecs)));
        }

        return rules;
    }

    private static string? MapAction(RuleAction action) {
        return action switch {
            RuleAction.Allow => "allow",
            RuleAction.Block => "block",
            RuleAction.Challenge => "challenge",
            RuleAction.RateLimit => "rate_limit",
            // The edge has no log action yet.
            _ => null
        };
    }

    /// <summary>Snake_case strategy name matching the edge's <c>LbStrategy</c>.</summary>
    private static string MapStrategy(LoadBalanceStrategy strategy) {
        return strategy switch {
            LoadBalanceStrategy.LeastConnections => "least_connections",
            LoadBalanceStrategy.IpHash => "ip_hash",
            _ => "round_robin"
        };
    }

    /// <summary>
    /// Returns null when any condition is unsupported by the edge. Conditions
    /// are AND-ed, so dropping one would make the rule fire more broadly than
    /// configured — the only safe degradation is dropping the whole rule.
    /// </summary>
    private static List<EdgeConditionConfig>? MapConditions(IReadOnlyList<RuleCondition> conditions) {
        List<EdgeConditionConfig> mapped = [];

        foreach(RuleCondition condition in conditions) {
            EdgeConditionConfig? edge = condition switch {
                // The edge "ip" condition accepts both a bare IP and CIDR.
                IpMatchCondition c => new EdgeConditionConfig("ip", c.Ip),
                IpRangeMatchCondition c => new EdgeConditionConfig("ip", c.Cidr),
                PathMatchCondition c => new EdgeConditionConfig(
                    c.Mode == PathMatchMode.Exact ? "path_exact" : "path_prefix", c.Pattern),
                HeaderMatchCondition c => new EdgeConditionConfig("header", c.Value, Name: c.Name),
                UserAgentMatchCondition c => new EdgeConditionConfig("user_agent_contains", c.Pattern),
                // GeoIP country + path regex + TLS fingerprints are all enforced
                // by the edge.
                CountryMatchCondition c => new EdgeConditionConfig("country", c.CountryCode),
                AsnMatchCondition c => new EdgeConditionConfig("asn", c.Asn.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                PathRegexMatchCondition c => new EdgeConditionConfig("path_regex", c.Regex),
                MethodMatchCondition c => new EdgeConditionConfig("method", c.Method),
                QueryRegexMatchCondition c => new EdgeConditionConfig("query_regex", c.Regex),
                HeaderRegexMatchCondition c => new EdgeConditionConfig("header_regex", c.Regex, Name: c.Name),
                BodyRegexMatchCondition c => new EdgeConditionConfig("body_regex", c.Regex),
                Ja3MatchCondition c => new EdgeConditionConfig("ja3", c.Fingerprint),
                Ja4MatchCondition c => new EdgeConditionConfig("ja4", c.Fingerprint),
                _ => null
            };

            if(edge is null)
                return null;

            mapped.Add(edge);
        }

        return mapped;
    }
}

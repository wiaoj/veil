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
    [property: JsonPropertyName("upstream")] string Upstream,
    [property: JsonPropertyName("rules")] List<EdgeRuleConfig> Rules,
    [property: JsonPropertyName("tls"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    EdgeZoneTlsConfig? Tls = null);

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
            // (TLS chosen from the URI scheme). A zone with no target at all
            // cannot be served.
            UpstreamTarget? target = zone.Upstream.Targets.FirstOrDefault(
                t => t.Url.Scheme is "http" or "https");
            if(target is null)
                continue;

            // A paused zone passes traffic through unfiltered.
            List<EdgeRuleConfig> rules = zone.Status is ZoneStatus.Paused
                ? []
                : MapRules(zone, ruleObfuscator);

            EdgeZoneTlsConfig? tls = null;
            certificates?.TryGetValue(zone.Hostname.Value.ToLowerInvariant(), out tls);

            edgeZones.Add(new EdgeZoneConfig(
                zone.Hostname.Value,
                [zone.Hostname.Value],
                target.Url.ToString().TrimEnd('/'),
                rules,
                tls));
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
                // country / asn (GeoIP) and path_regex are not implemented on
                // the edge yet.
                _ => null
            };

            if(edge is null)
                return null;

            mapped.Add(edge);
        }

        return mapped;
    }
}

//! Local configuration snapshot.
//!
//! The canonical config will eventually live in the control plane and be
//! synced at startup / pushed at runtime (Phase 3). Until then the edge node
//! loads a JSON file (`VEIL_CONFIG_PATH`, default `veil.json`) with the same
//! shape the config sync payload will use.

pub mod cache;
pub mod store;
pub mod sync;

use std::fmt;
use std::net::IpAddr;

use hyper::Uri;
use ipnet::IpNet;
use regex::Regex;
use serde::{Deserialize, Deserializer};

/// A regex compiled at config-load time. Deserializes from a pattern string;
/// an invalid pattern fails the whole config load (fail-safe).
#[derive(Debug, Clone)]
pub struct CompiledRegex(pub Regex);

impl<'de> Deserialize<'de> for CompiledRegex {
    fn deserialize<D: Deserializer<'de>>(de: D) -> Result<Self, D::Error> {
        let pattern = String::deserialize(de)?;
        Regex::new(&pattern)
            .map(CompiledRegex)
            .map_err(|e| serde::de::Error::custom(format!("invalid regex '{pattern}': {e}")))
    }
}

/// A JSON Schema compiled once at config-load time. Deserializes from an inline
/// JSON Schema object; an invalid schema fails the whole config load (fail-safe).
/// Validation is offline only — no remote `$ref` resolution.
pub struct CompiledSchema(pub jsonschema::Validator);

impl std::fmt::Debug for CompiledSchema {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("CompiledSchema(..)")
    }
}

impl<'de> Deserialize<'de> for CompiledSchema {
    fn deserialize<D: Deserializer<'de>>(de: D) -> Result<Self, D::Error> {
        let schema = serde_json::Value::deserialize(de)?;
        jsonschema::validator_for(&schema)
            .map(CompiledSchema)
            .map_err(|e| serde::de::Error::custom(format!("invalid JSON Schema: {e}")))
    }
}

#[derive(Debug, Deserialize)]
pub struct Config {
    pub zones: Vec<Zone>,
    /// Trust `X-Forwarded-For` for client IP extraction. Enable only when a
    /// trusted load balancer sits in front of the edge node.
    #[serde(default)]
    pub trust_forwarded_headers: bool,
}

#[derive(Debug, Deserialize)]
pub struct Zone {
    pub name: String,
    /// Host names this zone serves. `"*"` acts as a catch-all fallback.
    pub hosts: Vec<String>,
    /// Upstream origin(s). Accepts either a bare URL string (single target) or
    /// `{ "targets": [{"url","weight"}], "strategy": "..." }` for load balancing.
    pub upstream: Upstream,
    #[serde(default)]
    pub rules: Vec<Rule>,
    /// Control-plane-provisioned certificate material for this zone's hosts
    /// (Phase 5). Picked by SNI on the HTTPS listener.
    #[serde(default)]
    pub tls: Option<ZoneTls>,
    /// Per-zone challenge tuning (Phase 4.3). Absent → engine defaults.
    #[serde(default)]
    pub challenge: Option<ChallengeSettings>,
    /// Managed signature rule set (OWASP-CRS-style SQLi/XSS/traversal).
    /// Absent → no managed inspection for this zone.
    #[serde(default)]
    pub managed_rules: Option<ManagedRules>,
    /// Shadow (dry-run) mode: rules and managed signatures are evaluated and
    /// the would-be verdict is logged, but nothing is enforced — every request
    /// is forwarded. Lets a rule set be validated against live traffic first.
    #[serde(default)]
    pub shadow: bool,
    /// Opt-in response caching for this zone. Absent → no caching (default).
    #[serde(default)]
    pub cache: Option<CacheSettings>,
}

/// Per-zone response-cache tuning. Its presence enables caching; the cache
/// itself only stores explicitly-cacheable `GET` responses (see `response_cache`).
#[derive(Debug, Deserialize)]
pub struct CacheSettings {
    /// Largest response body (bytes) to cache. Responses without a
    /// `Content-Length` or larger than this stream through uncached.
    #[serde(default = "default_cache_max_body")]
    pub max_body_bytes: usize,
}

fn default_cache_max_body() -> usize {
    1024 * 1024
}

/// Load-balancing strategy across a zone's upstream targets. Mirrors the
/// control plane's `LoadBalanceStrategy`.
#[derive(Debug, Clone, Copy, Deserialize, Default, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum LbStrategy {
    #[default]
    RoundRobin,
    /// The edge has no upstream in-flight counters yet, so this currently
    /// behaves as weighted round-robin.
    LeastConnections,
    IpHash,
}

/// One upstream target and its relative weight (round-robin picks each target
/// proportionally to its weight).
#[derive(Debug, Clone)]
pub struct UpstreamTarget {
    pub url: String,
    pub weight: u32,
}

/// A zone's upstream: one or more targets plus a selection strategy. Selection
/// is per request; the round-robin cursor lives for the life of a config
/// snapshot (a config push resets it, which is harmless).
#[derive(Debug, Deserialize)]
#[serde(from = "UpstreamWire")]
pub struct Upstream {
    pub targets: Vec<UpstreamTarget>,
    pub strategy: LbStrategy,
    /// Target indices expanded by weight, for O(1) weighted round-robin.
    weighted: Vec<usize>,
    cursor: std::sync::atomic::AtomicUsize,
}

impl Upstream {
    /// Picks the upstream URL for this request per the zone's strategy. Empty
    /// string when the zone has no target (malformed config) — the forward then
    /// fails as a gateway error rather than panicking.
    pub fn select(&self, client_ip: IpAddr) -> &str {
        if self.targets.len() <= 1 {
            return self.targets.first().map_or("", |t| t.url.as_str());
        }
        let index = match self.strategy {
            LbStrategy::IpHash => {
                use std::hash::{Hash, Hasher};
                let mut hasher = std::collections::hash_map::DefaultHasher::new();
                client_ip.hash(&mut hasher);
                (hasher.finish() as usize) % self.targets.len()
            }
            // RoundRobin + LeastConnections (no in-flight tracking yet).
            _ => {
                let n = self.cursor.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
                self.weighted[n % self.weighted.len()]
            }
        };
        self.targets.get(index).map_or("", |t| t.url.as_str())
    }
}

/// Deserialization shim: accept a bare URL string (single target) or the
/// structured `{ targets, strategy }` form.
#[derive(Deserialize)]
#[serde(untagged)]
enum UpstreamWire {
    Single(String),
    Structured {
        targets: Vec<TargetWire>,
        #[serde(default)]
        strategy: LbStrategy,
    },
}

#[derive(Deserialize)]
struct TargetWire {
    url: String,
    #[serde(default = "default_weight")]
    weight: u32,
}

fn default_weight() -> u32 {
    1
}

impl From<UpstreamWire> for Upstream {
    fn from(wire: UpstreamWire) -> Self {
        let (targets, strategy) = match wire {
            UpstreamWire::Single(url) => {
                (vec![UpstreamTarget { url, weight: 1 }], LbStrategy::RoundRobin)
            }
            UpstreamWire::Structured { targets, strategy } => (
                targets
                    .into_iter()
                    .map(|t| UpstreamTarget { url: t.url, weight: t.weight.max(1) })
                    .collect(),
                strategy,
            ),
        };
        let mut weighted = Vec::new();
        for (i, target) in targets.iter().enumerate() {
            for _ in 0..target.weight.max(1) {
                weighted.push(i);
            }
        }
        if weighted.is_empty() {
            weighted.push(0);
        }
        Upstream { targets, strategy, weighted, cursor: std::sync::atomic::AtomicUsize::new(0) }
    }
}

impl Zone {
    /// Whether any configured rule or the managed rule set needs the request
    /// body buffered for inspection.
    pub fn needs_body_inspection(&self) -> bool {
        self.managed_rules.as_ref().is_some_and(|m| m.inspect_body)
            || self
                .rules
                .iter()
                .flat_map(|r| &r.conditions)
                .any(|c| matches!(c,
                    Condition::BodyRegex { .. }
                    | Condition::BodyJson { .. }
                    | Condition::BodySchema { .. }))
    }
}

#[derive(Debug, Clone, Copy, Deserialize)]
pub struct ChallengeSettings {
    /// Risk score (`0..=100`) at or above which the Tier 2 interaction
    /// challenge is served instead of the Tier 1 PoW page.
    pub tier2_risk_threshold: u8,
    /// Per-zone base PoW difficulty (leading zero bits) before risk scaling.
    /// Absent → the engine default (`VEIL_POW_DIFFICULTY`).
    #[serde(default)]
    pub base_difficulty: Option<u32>,
    /// Per-zone pass-token lifetime, seconds. Absent → the engine default
    /// (`VEIL_CHALLENGE_TTL`).
    #[serde(default)]
    pub token_ttl_secs: Option<u32>,
}

/// Managed signature rule set toggles (Phase 8.x). Each category enables a
/// built-in family of attack-pattern signatures evaluated against the request
/// line, query string, inspected headers and (optionally) the body.
#[derive(Debug, Clone, Copy, Deserialize)]
pub struct ManagedRules {
    #[serde(default)]
    pub sql_injection: bool,
    #[serde(default)]
    pub xss: bool,
    #[serde(default)]
    pub path_traversal: bool,
    /// Buffer and scan the request body in addition to the URL and headers.
    #[serde(default)]
    pub inspect_body: bool,
    /// What to do on a signature match.
    #[serde(default)]
    pub action: ManagedAction,
    /// When `inspect_body` is on, reject bodies larger than the inspection cap
    /// instead of forwarding them un-inspected. Closes the "pad the payload past
    /// the cap to skip the WAF" bypass, at the cost of blocking legitimately
    /// large uploads — so it is opt-in.
    #[serde(default)]
    pub block_oversized_body: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum ManagedAction {
    #[default]
    Block,
    Challenge,
}

#[derive(Debug, Deserialize)]
pub struct ZoneTls {
    /// PEM certificate chain, leaf first.
    pub cert_pem: String,
    /// PEM private key.
    pub key_pem: String,
}

#[derive(Debug, Deserialize)]
pub struct Rule {
    pub id: String,
    /// Lower number = evaluated first.
    pub priority: i32,
    pub action: Action,
    /// All conditions must match (AND).
    pub conditions: Vec<Condition>,
    /// Required when `action` is `rate_limit`.
    #[serde(default)]
    pub rate_limit: Option<RateLimitParams>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Action {
    Allow,
    Block,
    Challenge,
    RateLimit,
}

#[derive(Debug, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum Condition {
    /// Single IP or CIDR range, e.g. `"203.0.113.7"` or `"203.0.113.0/24"`.
    Ip {
        #[serde(deserialize_with = "de_ip_net")]
        value: IpNet,
    },
    PathPrefix { value: String },
    PathExact { value: String },
    Method { value: String },
    Header { name: String, value: String },
    UserAgentContains { value: String },
    /// Regex over the request path.
    PathRegex { value: CompiledRegex },
    /// Regex over the raw query string (empty string when absent).
    QueryRegex { value: CompiledRegex },
    /// Regex over a named request header's value.
    HeaderRegex { name: String, value: CompiledRegex },
    /// Regex over the request body. Forces body buffering for the zone.
    BodyRegex { value: CompiledRegex },
    /// Regex over a single field of a JSON request body, selected by a dotted
    /// path (`"$.user.name"` / `"user.name"`). Only matches when the body parses
    /// as JSON and the field is a string/number. Forces body buffering.
    BodyJson {
        path: String,
        value: CompiledRegex,
    },
    /// Positive validation: matches when the JSON request body does **not**
    /// conform to the given JSON Schema (or isn't valid JSON at all). Paired with
    /// a `block` action this enforces a contract — only conforming bodies pass —
    /// while staying in the negative-model machinery, so shadow mode logs the
    /// would-be rejects for free. Forces body buffering.
    BodySchema {
        #[serde(rename = "schema")]
        value: CompiledSchema,
    },
    /// Matches the client's GeoIP country (ISO 3166-1 alpha-2, e.g. `"TR"`).
    /// Case-insensitive; never matches when no GeoIP database is loaded.
    Country { value: String },
    /// Matches the client's GeoIP ASN (as a decimal string, e.g. `"64500"`).
    /// Never matches when no ASN database is loaded.
    Asn { value: String },
    /// Matches the client's JA3 TLS fingerprint (MD5 hex). Useful for
    /// blocklisting known bot/tooling fingerprints. HTTPS only.
    Ja3 { value: String },
    /// Matches the client's JA4 TLS fingerprint (FoxIO). More robust than JA3
    /// against extension-order randomisation. HTTPS only.
    Ja4 { value: String },
}

#[derive(Debug, Clone, Copy, Deserialize)]
pub struct RateLimitParams {
    pub requests: u32,
    pub window_secs: u64,
}

impl Config {
    pub fn from_file(path: &str) -> Result<Self, ConfigError> {
        let raw = std::fs::read_to_string(path).map_err(ConfigError::Io)?;
        Self::from_json(&raw)
    }

    pub fn from_json(raw: &str) -> Result<Self, ConfigError> {
        let mut config: Config = serde_json::from_str(raw).map_err(ConfigError::Parse)?;
        config.validate()?;
        for zone in &mut config.zones {
            zone.rules.sort_by_key(|r| r.priority);
        }
        Ok(config)
    }

    /// Resolves the zone for a `Host` header value. Exact host match wins;
    /// a zone listing `"*"` serves as fallback.
    pub fn resolve_zone(&self, host: &str) -> Option<&Zone> {
        let host = host
            .rsplit_once(':')
            .map_or(host, |(h, _)| h)
            .to_ascii_lowercase();
        self.zones
            .iter()
            .find(|z| z.hosts.iter().any(|h| h.eq_ignore_ascii_case(&host)))
            .or_else(|| self.zones.iter().find(|z| z.hosts.iter().any(|h| h == "*")))
    }

    fn validate(&self) -> Result<(), ConfigError> {
        for zone in &self.zones {
            if zone.hosts.is_empty() {
                return Err(ConfigError::Invalid(format!(
                    "zone '{}' has no hosts",
                    zone.name
                )));
            }
            if zone.upstream.targets.is_empty() {
                return Err(ConfigError::Invalid(format!(
                    "zone '{}' has no upstream target",
                    zone.name
                )));
            }
            for target in &zone.upstream.targets {
                let uri: Uri = target.url.parse().map_err(|_| {
                    ConfigError::Invalid(format!(
                        "zone '{}' upstream '{}' is not a valid URI",
                        zone.name, target.url
                    ))
                })?;
                if !matches!(uri.scheme_str(), Some("http" | "https")) || uri.authority().is_none() {
                    return Err(ConfigError::Invalid(format!(
                        "zone '{}' upstream '{}' must be an absolute http:// or https:// URI",
                        zone.name, target.url
                    )));
                }
            }
            for rule in &zone.rules {
                if rule.action == Action::RateLimit && rule.rate_limit.is_none() {
                    return Err(ConfigError::Invalid(format!(
                        "rule '{}' has action rate_limit but no rate_limit params",
                        rule.id
                    )));
                }
                if rule.action != Action::RateLimit && rule.rate_limit.is_some() {
                    return Err(ConfigError::Invalid(format!(
                        "rule '{}' has rate_limit params but its action is not rate_limit",
                        rule.id
                    )));
                }
            }
        }
        Ok(())
    }
}

impl Condition {
    pub fn matches_ip(net: &IpNet, ip: IpAddr) -> bool {
        net.contains(&ip)
    }
}

/// Accepts both a bare IP (`"1.2.3.4"`) and CIDR notation (`"1.2.3.0/24"`).
fn de_ip_net<'de, D: Deserializer<'de>>(de: D) -> Result<IpNet, D::Error> {
    let raw = String::deserialize(de)?;
    raw.parse::<IpNet>()
        .or_else(|_| raw.parse::<IpAddr>().map(IpNet::from))
        .map_err(|_| serde::de::Error::custom(format!("invalid IP or CIDR: {raw}")))
}

#[derive(Debug)]
pub enum ConfigError {
    Io(std::io::Error),
    Parse(serde_json::Error),
    Invalid(String),
}

impl fmt::Display for ConfigError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            ConfigError::Io(e) => write!(f, "failed to read config file: {e}"),
            ConfigError::Parse(e) => write!(f, "failed to parse config JSON: {e}"),
            ConfigError::Invalid(msg) => write!(f, "invalid config: {msg}"),
        }
    }
}

impl std::error::Error for ConfigError {}

#[cfg(test)]
mod tests {
    use super::*;

    const SAMPLE: &str = r#"{
        "zones": [{
            "name": "example",
            "hosts": ["example.com", "*"],
            "upstream": "http://127.0.0.1:3000",
            "rules": [
                {"id": "b", "priority": 20, "action": "block",
                 "conditions": [{"type": "path_prefix", "value": "/admin"}]},
                {"id": "a", "priority": 10, "action": "allow",
                 "conditions": [{"type": "ip", "value": "10.0.0.0/8"}]}
            ]
        }]
    }"#;

    #[test]
    fn parses_and_sorts_rules_by_priority() {
        let config = Config::from_json(SAMPLE).unwrap();
        let rules = &config.zones[0].rules;
        assert_eq!(rules[0].id, "a");
        assert_eq!(rules[1].id, "b");
    }

    #[test]
    fn resolves_zone_by_host_and_strips_port() {
        let config = Config::from_json(SAMPLE).unwrap();
        assert!(config.resolve_zone("example.com:8080").is_some());
        assert!(config.resolve_zone("EXAMPLE.COM").is_some());
        // unknown host falls back to the "*" zone
        assert!(config.resolve_zone("other.io").is_some());
    }

    #[test]
    fn bare_ip_condition_parses_as_single_host_net() {
        let config = Config::from_json(
            r#"{"zones": [{"name": "z", "hosts": ["*"], "upstream": "http://h:1",
                "rules": [{"id": "r", "priority": 1, "action": "block",
                           "conditions": [{"type": "ip", "value": "1.2.3.4"}]}]}]}"#,
        )
        .unwrap();
        let Condition::Ip { value } = &config.zones[0].rules[0].conditions[0] else {
            panic!("expected ip condition");
        };
        assert!(value.contains(&"1.2.3.4".parse::<IpAddr>().unwrap()));
        assert!(!value.contains(&"1.2.3.5".parse::<IpAddr>().unwrap()));
    }

    #[test]
    fn rate_limit_action_requires_params() {
        let err = Config::from_json(
            r#"{"zones": [{"name": "z", "hosts": ["*"], "upstream": "http://h:1",
                "rules": [{"id": "r", "priority": 1, "action": "rate_limit",
                           "conditions": []}]}]}"#,
        )
        .unwrap_err();
        assert!(matches!(err, ConfigError::Invalid(_)));
    }

    #[test]
    fn rate_limit_params_rejected_on_other_actions() {
        let err = Config::from_json(
            r#"{"zones": [{"name": "z", "hosts": ["*"], "upstream": "http://h:1",
                "rules": [{"id": "r", "priority": 1, "action": "block",
                           "conditions": [],
                           "rate_limit": {"requests": 5, "window_secs": 10}}]}]}"#,
        )
        .unwrap_err();
        assert!(matches!(err, ConfigError::Invalid(_)));
    }

    fn zone_with_upstream(upstream_json: &str) -> Zone {
        let config = Config::from_json(&format!(
            r#"{{"zones": [{{"name": "z", "hosts": ["*"], "upstream": {upstream_json}, "rules": []}}]}}"#
        ))
        .unwrap();
        config.zones.into_iter().next().unwrap()
    }

    #[test]
    fn bare_string_upstream_is_a_single_target() {
        let zone = zone_with_upstream(r#""http://127.0.0.1:3000""#);
        assert_eq!(zone.upstream.targets.len(), 1);
        assert_eq!(zone.upstream.strategy, LbStrategy::RoundRobin);
        let ip: IpAddr = "1.2.3.4".parse().unwrap();
        assert_eq!(zone.upstream.select(ip), "http://127.0.0.1:3000");
    }

    #[test]
    fn weighted_round_robin_respects_weights() {
        let zone = zone_with_upstream(
            r#"{"targets": [{"url": "http://a:1", "weight": 1}, {"url": "http://b:1", "weight": 3}],
                "strategy": "round_robin"}"#,
        );
        let ip: IpAddr = "1.2.3.4".parse().unwrap();
        let mut a = 0;
        let mut b = 0;
        for _ in 0..8 {
            match zone.upstream.select(ip) {
                "http://a:1" => a += 1,
                "http://b:1" => b += 1,
                other => panic!("unexpected target {other}"),
            }
        }
        // Weight 1:3 over 8 picks → 2 and 6.
        assert_eq!((a, b), (2, 6));
    }

    #[test]
    fn ip_hash_is_stable_per_client() {
        let zone = zone_with_upstream(
            r#"{"targets": [{"url": "http://a:1"}, {"url": "http://b:1"}], "strategy": "ip_hash"}"#,
        );
        let ip: IpAddr = "203.0.113.9".parse().unwrap();
        let first = zone.upstream.select(ip).to_owned();
        for _ in 0..10 {
            assert_eq!(zone.upstream.select(ip), first, "same IP must be sticky");
        }
    }

    #[test]
    fn challenge_settings_parse_with_overrides() {
        let zone = zone_with_upstream(r#""http://h:1""#);
        assert!(zone.challenge.is_none());

        let config = Config::from_json(
            r#"{"zones": [{"name": "z", "hosts": ["*"], "upstream": "http://h:1", "rules": [],
                "challenge": {"tier2_risk_threshold": 80, "base_difficulty": 24, "token_ttl_secs": 300}}]}"#,
        )
        .unwrap();
        let ch = config.zones[0].challenge.unwrap();
        assert_eq!(ch.tier2_risk_threshold, 80);
        assert_eq!(ch.base_difficulty, Some(24));
        assert_eq!(ch.token_ttl_secs, Some(300));
    }

    #[test]
    fn empty_targets_rejected() {
        let err = Config::from_json(
            r#"{"zones": [{"name": "z", "hosts": ["*"], "upstream": {"targets": []}, "rules": []}]}"#,
        )
        .unwrap_err();
        assert!(matches!(err, ConfigError::Invalid(_)));
    }
}

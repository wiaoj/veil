//! Local configuration snapshot.
//!
//! The canonical config will eventually live in the control plane and be
//! synced at startup / pushed at runtime (Phase 3). Until then the edge node
//! loads a JSON file (`VEIL_CONFIG_PATH`, default `veil.json`) with the same
//! shape the config sync payload will use.

use std::fmt;
use std::net::IpAddr;

use hyper::Uri;
use ipnet::IpNet;
use serde::{Deserialize, Deserializer};

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
    /// Upstream origin, e.g. `http://127.0.0.1:3000`.
    pub upstream: String,
    #[serde(default)]
    pub rules: Vec<Rule>,
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
            let uri: Uri = zone.upstream.parse().map_err(|_| {
                ConfigError::Invalid(format!(
                    "zone '{}' upstream '{}' is not a valid URI",
                    zone.name, zone.upstream
                ))
            })?;
            if uri.scheme_str() != Some("http") || uri.authority().is_none() {
                return Err(ConfigError::Invalid(format!(
                    "zone '{}' upstream must be an absolute http:// URI",
                    zone.name
                )));
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
}

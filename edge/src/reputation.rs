//! IP reputation denylist.
//!
//! Loads a feed of known-bad IPs and CIDR ranges from a file
//! (`VEIL_IP_REPUTATION_PATH`) — one entry per line, `#` comments and blank
//! lines ignored. Single IPs go into a hash set (O(1) lookups); CIDR ranges
//! into a small vector (linear scan). A client IP that matches is blocked
//! *after* the zone's own rules, so explicit allow/block rules still take
//! precedence and an operator can whitelist.

use std::collections::HashSet;
use std::net::IpAddr;

use ipnet::IpNet;
use tracing::{info, warn};

pub struct IpReputation {
    ips: HashSet<IpAddr>,
    nets: Vec<IpNet>,
}

impl IpReputation {
    /// Loads the feed named by `VEIL_IP_REPUTATION_PATH`. Returns `None` when
    /// unset, or when the file can't be read (logged) — reputation is simply
    /// inactive rather than failing the node.
    pub fn from_env() -> Option<Self> {
        let path = std::env::var("VEIL_IP_REPUTATION_PATH").ok()?;
        match std::fs::read_to_string(&path) {
            Ok(contents) => {
                let feed = Self::parse(&contents);
                info!(path, ips = feed.ips.len(), nets = feed.nets.len(), "loaded IP reputation feed");
                Some(feed)
            }
            Err(err) => {
                warn!(path, error = %err, "failed to read IP reputation feed; disabled");
                None
            }
        }
    }

    fn parse(contents: &str) -> Self {
        let mut ips = HashSet::new();
        let mut nets = Vec::new();
        for raw in contents.lines() {
            let line = raw.split('#').next().unwrap_or("").trim();
            if line.is_empty() {
                continue;
            }
            if let Ok(ip) = line.parse::<IpAddr>() {
                ips.insert(ip);
            } else if let Ok(net) = line.parse::<IpNet>() {
                nets.push(net);
            }
            // Unparseable lines are skipped silently (feeds vary in format).
        }
        Self { ips, nets }
    }

    /// Whether `ip` is on the denylist.
    pub fn contains(&self, ip: IpAddr) -> bool {
        self.ips.contains(&ip) || self.nets.iter().any(|net| net.contains(&ip))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn feed() -> IpReputation {
        IpReputation::parse(
            "# bad actors\n203.0.113.7\n198.51.100.0/24\n\n  2001:db8::1  # ipv6\ngarbage-line\n",
        )
    }

    #[test]
    fn matches_single_ip() {
        assert!(feed().contains("203.0.113.7".parse().unwrap()));
    }

    #[test]
    fn matches_cidr_range() {
        let f = feed();
        assert!(f.contains("198.51.100.42".parse().unwrap()));
        assert!(!f.contains("198.51.101.1".parse().unwrap()));
    }

    #[test]
    fn matches_ipv6_and_ignores_comments_and_junk() {
        let f = feed();
        assert!(f.contains("2001:db8::1".parse().unwrap()));
        assert!(!f.contains("8.8.8.8".parse().unwrap()));
    }
}

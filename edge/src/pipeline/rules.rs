//! Stage 2 — the rule engine.
//!
//! Rules are evaluated in priority order (sorted at config load) with
//! short-circuit semantics: the first matching terminal rule (allow / block /
//! challenge) produces the verdict. A matching `rate_limit` rule only
//! terminates evaluation when its limit is exceeded; otherwise evaluation
//! continues, so later rules still apply.
//!
//! The compiled decision tree from the architecture docs comes later — at
//! this scale a linear scan over a sorted slice is plenty, and it doubles as
//! the reference implementation the compiled tree will be property-tested
//! against.

use crate::config::{Action, Condition, Rule, Zone};

use super::rate_limit::RateLimiter;
use super::{RequestContext, Verdict};

pub fn evaluate(zone: &Zone, ctx: &RequestContext, limiter: &RateLimiter) -> Verdict {
    for rule in &zone.rules {
        if !rule.conditions.iter().all(|c| matches(c, ctx)) {
            continue;
        }
        match rule.action {
            Action::Allow => return Verdict::Allow,
            Action::Block => return Verdict::Block { rule_id: rule.id.clone() },
            Action::Challenge => return Verdict::Challenge { rule_id: rule.id.clone() },
            Action::RateLimit => {
                if is_rate_limited(rule, ctx, limiter) {
                    return Verdict::RateLimited { rule_id: rule.id.clone() };
                }
            }
        }
    }
    Verdict::Allow
}

fn is_rate_limited(rule: &Rule, ctx: &RequestContext, limiter: &RateLimiter) -> bool {
    let params = rule
        .rate_limit
        .expect("validated at config load: rate_limit action has params");
    // Per-rule key namespace: a per-IP rule and a per-path rule never share
    // counters.
    let key = format!("{}:{}", rule.id, ctx.client_ip);
    !limiter.allow(&key, params.requests, params.window_secs)
}

fn matches(condition: &Condition, ctx: &RequestContext) -> bool {
    match condition {
        Condition::Ip { value } => value.contains(&ctx.client_ip),
        Condition::PathPrefix { value } => ctx.path.starts_with(value),
        Condition::PathExact { value } => ctx.path == *value,
        Condition::Method { value } => ctx.method.as_str().eq_ignore_ascii_case(value),
        Condition::Header { name, value } => ctx
            .headers
            .get(name)
            .and_then(|v| v.to_str().ok())
            .is_some_and(|v| v == value),
        Condition::UserAgentContains { value } => ctx
            .user_agent
            .as_deref()
            .is_some_and(|ua| ua.to_ascii_lowercase().contains(&value.to_ascii_lowercase())),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::Config;
    use hyper::{HeaderMap, Method};

    fn ctx(ip: &str, path: &str) -> RequestContext {
        RequestContext {
            client_ip: ip.parse().unwrap(),
            host: "example.com".into(),
            method: Method::GET,
            path: path.into(),
            user_agent: Some("Mozilla/5.0 TestBrowser".into()),
            headers: HeaderMap::new(),
        }
    }

    fn zone(rules_json: &str) -> Zone {
        let config = Config::from_json(&format!(
            r#"{{"zones": [{{"name": "z", "hosts": ["*"],
                "upstream": "http://127.0.0.1:1", "rules": {rules_json}}}]}}"#
        ))
        .unwrap();
        config.zones.into_iter().next().unwrap()
    }

    #[test]
    fn empty_rule_set_allows() {
        let zone = zone("[]");
        assert_eq!(
            evaluate(&zone, &ctx("1.1.1.1", "/"), &RateLimiter::new()),
            Verdict::Allow
        );
    }

    #[test]
    fn first_matching_rule_by_priority_wins() {
        let zone = zone(
            r#"[
            {"id": "block-all", "priority": 20, "action": "block", "conditions": []},
            {"id": "allow-vip", "priority": 10, "action": "allow",
             "conditions": [{"type": "ip", "value": "10.0.0.0/8"}]}
        ]"#,
        );
        let limiter = RateLimiter::new();
        // VIP IP hits the priority-10 allow before the catch-all block.
        assert_eq!(evaluate(&zone, &ctx("10.1.2.3", "/"), &limiter), Verdict::Allow);
        assert_eq!(
            evaluate(&zone, &ctx("8.8.8.8", "/"), &limiter),
            Verdict::Block { rule_id: "block-all".into() }
        );
    }

    #[test]
    fn all_conditions_must_match() {
        let zone = zone(
            r#"[{"id": "r", "priority": 1, "action": "block", "conditions": [
                {"type": "path_prefix", "value": "/admin"},
                {"type": "method", "value": "POST"}
            ]}]"#,
        );
        let limiter = RateLimiter::new();
        // GET /admin matches only one of the two conditions.
        assert_eq!(evaluate(&zone, &ctx("1.1.1.1", "/admin"), &limiter), Verdict::Allow);
    }

    #[test]
    fn user_agent_match_is_case_insensitive() {
        let zone = zone(
            r#"[{"id": "ua", "priority": 1, "action": "challenge",
                 "conditions": [{"type": "user_agent_contains", "value": "testbrowser"}]}]"#,
        );
        assert_eq!(
            evaluate(&zone, &ctx("1.1.1.1", "/"), &RateLimiter::new()),
            Verdict::Challenge { rule_id: "ua".into() }
        );
    }

    #[test]
    fn rate_limit_rule_passes_through_until_exceeded() {
        let zone = zone(
            r#"[{"id": "rl", "priority": 1, "action": "rate_limit",
                 "conditions": [{"type": "path_prefix", "value": "/api"}],
                 "rate_limit": {"requests": 2, "window_secs": 60}}]"#,
        );
        let limiter = RateLimiter::new();
        let c = ctx("1.1.1.1", "/api/users");
        assert_eq!(evaluate(&zone, &c, &limiter), Verdict::Allow);
        assert_eq!(evaluate(&zone, &c, &limiter), Verdict::Allow);
        assert_eq!(
            evaluate(&zone, &c, &limiter),
            Verdict::RateLimited { rule_id: "rl".into() }
        );
        // A different client IP has its own counter.
        assert_eq!(evaluate(&zone, &ctx("2.2.2.2", "/api/users"), &limiter), Verdict::Allow);
    }
}

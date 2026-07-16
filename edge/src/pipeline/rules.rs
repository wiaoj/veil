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

pub async fn evaluate(
    zone: &Zone,
    ctx: &RequestContext,
    limiter: &RateLimiter,
    body: Option<&[u8]>,
) -> Verdict {
    for rule in &zone.rules {
        if !rule.conditions.iter().all(|c| matches(c, ctx, body)) {
            continue;
        }
        match rule.action {
            Action::Allow => return Verdict::Allow,
            Action::Block => return Verdict::Block { rule_id: rule.id.clone() },
            Action::Challenge => return Verdict::Challenge { rule_id: rule.id.clone() },
            Action::RateLimit => {
                if is_rate_limited(rule, ctx, limiter).await {
                    return Verdict::RateLimited { rule_id: rule.id.clone() };
                }
            }
        }
    }
    Verdict::Allow
}

async fn is_rate_limited(rule: &Rule, ctx: &RequestContext, limiter: &RateLimiter) -> bool {
    let params = rule
        .rate_limit
        .expect("validated at config load: rate_limit action has params");
    // Per-rule key namespace: a per-IP rule and a per-path rule never share
    // counters.
    let key = format!("{}:{}", rule.id, ctx.client_ip);
    !limiter.allow(&key, params.requests, params.window_secs).await
}

fn matches(condition: &Condition, ctx: &RequestContext, body: Option<&[u8]>) -> bool {
    match condition {
        Condition::Ip { value } => value.contains(&ctx.client_ip),
        Condition::PathPrefix { value } => value == "*" || ctx.path.starts_with(value),
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
        Condition::PathRegex { value } => value.0.is_match(&ctx.path),
        Condition::QueryRegex { value } => value.0.is_match(ctx.query.as_deref().unwrap_or("")),
        Condition::HeaderRegex { name, value } => ctx
            .headers
            .get(name)
            .and_then(|v| v.to_str().ok())
            .is_some_and(|v| value.0.is_match(v)),
        Condition::BodyRegex { value } => {
            body.is_some_and(|b| value.0.is_match(&String::from_utf8_lossy(b)))
        }
        Condition::BodyJson { path, value } => body
            .and_then(json_field)
            .and_then(|json| lookup_json_path(&json, path))
            .is_some_and(|field| value.0.is_match(&field)),
        // Positive validation: this condition matches (→ the paired block fires)
        // when a body is present but is not valid JSON, or is valid JSON that
        // does not conform to the schema. A request with no body is left alone.
        Condition::BodySchema { value } => match body {
            None => false,
            Some(b) => match serde_json::from_slice::<serde_json::Value>(b) {
                Ok(instance) => !value.0.is_valid(&instance),
                Err(_) => true, // sent a body but it isn't JSON → contract violated
            },
        },
        Condition::Country { value } => ctx
            .country
            .as_deref()
            .is_some_and(|c| c.eq_ignore_ascii_case(value)),
        Condition::Ja3 { value } => ctx.ja3.as_deref().is_some_and(|j| j == value),
        Condition::Ja4 { value } => ctx.ja4.as_deref().is_some_and(|j| j == value),
        Condition::Asn { value } => ctx.asn.is_some_and(|a| a.to_string() == *value),
    }
}

/// Parses the body as JSON. Only successful parses of an already-buffered body
/// (≤ the inspection cap) are handled, so there is no unbounded-input risk here;
/// serde_json also rejects pathologically deep nesting on its own.
fn json_field(body: &[u8]) -> Option<serde_json::Value> {
    serde_json::from_slice(body).ok()
}

/// Resolves a dotted path (`"$.user.name"`, `"user.name"`) to a scalar field and
/// returns it as a string for regex matching. Objects/arrays/null yield `None`
/// (there is nothing to match a string pattern against).
fn lookup_json_path(root: &serde_json::Value, path: &str) -> Option<String> {
    let mut current = root;
    for segment in path.trim_start_matches("$.").trim_start_matches('.').split('.') {
        if segment.is_empty() {
            continue;
        }
        current = current.get(segment)?;
    }
    match current {
        serde_json::Value::String(s) => Some(s.clone()),
        serde_json::Value::Number(n) => Some(n.to_string()),
        serde_json::Value::Bool(b) => Some(b.to_string()),
        _ => None,
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
            query: None,
            user_agent: Some("Mozilla/5.0 TestBrowser".into()),
            headers: HeaderMap::new(),
            country: None,
            asn: None,
            ja3: None,
            ja4: None,
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

    #[tokio::test]
    async fn empty_rule_set_allows() {
        let zone = zone("[]");
        assert_eq!(
            evaluate(&zone, &ctx("1.1.1.1", "/"), &RateLimiter::in_memory(), None).await,
            Verdict::Allow
        );
    }

    #[tokio::test]
    async fn first_matching_rule_by_priority_wins() {
        let zone = zone(
            r#"[
            {"id": "block-all", "priority": 20, "action": "block", "conditions": []},
            {"id": "allow-vip", "priority": 10, "action": "allow",
             "conditions": [{"type": "ip", "value": "10.0.0.0/8"}]}
        ]"#,
        );
        let limiter = RateLimiter::in_memory();
        // VIP IP hits the priority-10 allow before the catch-all block.
        assert_eq!(evaluate(&zone, &ctx("10.1.2.3", "/"), &limiter, None).await, Verdict::Allow);
        assert_eq!(
            evaluate(&zone, &ctx("8.8.8.8", "/"), &limiter, None).await,
            Verdict::Block { rule_id: "block-all".into() }
        );
    }

    #[tokio::test]
    async fn all_conditions_must_match() {
        let zone = zone(
            r#"[{"id": "r", "priority": 1, "action": "block", "conditions": [
                {"type": "path_prefix", "value": "/admin"},
                {"type": "method", "value": "POST"}
            ]}]"#,
        );
        let limiter = RateLimiter::in_memory();
        // GET /admin matches only one of the two conditions.
        assert_eq!(evaluate(&zone, &ctx("1.1.1.1", "/admin"), &limiter, None).await, Verdict::Allow);
    }

    #[tokio::test]
    async fn user_agent_match_is_case_insensitive() {
        let zone = zone(
            r#"[{"id": "ua", "priority": 1, "action": "challenge",
                 "conditions": [{"type": "user_agent_contains", "value": "testbrowser"}]}]"#,
        );
        assert_eq!(
            evaluate(&zone, &ctx("1.1.1.1", "/"), &RateLimiter::in_memory(), None).await,
            Verdict::Challenge { rule_id: "ua".into() }
        );
    }

    #[tokio::test]
    async fn rate_limit_rule_passes_through_until_exceeded() {
        let zone = zone(
            r#"[{"id": "rl", "priority": 1, "action": "rate_limit",
                 "conditions": [{"type": "path_prefix", "value": "/api"}],
                 "rate_limit": {"requests": 2, "window_secs": 60}}]"#,
        );
        let limiter = RateLimiter::in_memory();
        let c = ctx("1.1.1.1", "/api/users");
        assert_eq!(evaluate(&zone, &c, &limiter, None).await, Verdict::Allow);
        assert_eq!(evaluate(&zone, &c, &limiter, None).await, Verdict::Allow);
        assert_eq!(
            evaluate(&zone, &c, &limiter, None).await,
            Verdict::RateLimited { rule_id: "rl".into() }
        );
        // A different client IP has its own counter.
        assert_eq!(evaluate(&zone, &ctx("2.2.2.2", "/api/users"), &limiter, None).await, Verdict::Allow);
    }

    #[tokio::test]
    async fn query_and_body_regex_conditions_match() {
        let zone = zone(
            r#"[{"id": "rx", "priority": 1, "action": "block", "conditions": [
                {"type": "query_regex", "value": "(?i)debug=true"},
                {"type": "body_regex", "value": "(?i)secret"}
            ]}]"#,
        );
        let limiter = RateLimiter::in_memory();
        let mut c = ctx("1.1.1.1", "/x");
        c.query = Some("debug=true&x=1".into());
        // Body condition needs the buffered body.
        assert_eq!(
            evaluate(&zone, &c, &limiter, Some(b"payload secret here")).await,
            Verdict::Block { rule_id: "rx".into() }
        );
        // Without the matching body, the AND-ed rule does not fire.
        assert_eq!(evaluate(&zone, &c, &limiter, Some(b"clean")).await, Verdict::Allow);
    }

    #[tokio::test]
    async fn country_condition_blocks_by_geoip() {
        let zone = zone(
            r#"[{"id": "geo", "priority": 1, "action": "block",
                 "conditions": [{"type": "country", "value": "RU"}]}]"#,
        );
        let limiter = RateLimiter::in_memory();
        let mut c = ctx("1.1.1.1", "/");
        // No GeoIP data → never matches.
        assert_eq!(evaluate(&zone, &c, &limiter, None).await, Verdict::Allow);
        // Country resolved (case-insensitive) → blocked.
        c.country = Some("ru".into());
        assert_eq!(
            evaluate(&zone, &c, &limiter, None).await,
            Verdict::Block { rule_id: "geo".into() }
        );
    }

    #[tokio::test]
    async fn body_json_matches_only_the_targeted_field() {
        let zone = zone(
            r#"[{"id": "js", "priority": 1, "action": "block",
                 "conditions": [{"type": "body_json", "path": "$.comment", "value": "(?i)<script"}]}]"#,
        );
        let limiter = RateLimiter::in_memory();
        let c = ctx("1.1.1.1", "/comment");

        // Payload in the targeted field → blocked.
        let hit = br#"{"user":"ok","comment":"<script>x</script>"}"#;
        assert_eq!(
            evaluate(&zone, &c, &limiter, Some(hit)).await,
            Verdict::Block { rule_id: "js".into() }
        );

        // Same payload, but in a *different* field → no match. This is the point
        // of field-level rules: far fewer false positives than scanning the whole
        // body, and it can't be tripped by content in an unrelated field.
        let miss = br#"{"user":"<script>x</script>","comment":"hi"}"#;
        assert_eq!(evaluate(&zone, &c, &limiter, Some(miss)).await, Verdict::Allow);

        // Non-JSON body → never matches.
        assert_eq!(evaluate(&zone, &c, &limiter, Some(b"not json")).await, Verdict::Allow);
    }

    #[tokio::test]
    async fn body_schema_enforces_a_contract() {
        // "Only a body matching this schema may pass": an invalid body is blocked,
        // a valid one falls through. Positive validation built out of the negative
        // model — invalid → the condition matches → block.
        let schema = r#"{
            "type": "object",
            "required": ["email", "age"],
            "additionalProperties": false,
            "properties": {
                "email": { "type": "string", "format": "email" },
                "age": { "type": "integer", "minimum": 0, "maximum": 130 }
            }
        }"#;
        let zone = zone(&format!(
            r#"[{{"id": "schema", "priority": 1, "action": "block",
                 "conditions": [{{"type": "body_schema", "schema": {schema}}}]}}]"#
        ));
        let limiter = RateLimiter::in_memory();
        let c = ctx("1.1.1.1", "/users");

        // Conforms → not blocked.
        let ok = br#"{"email":"a@b.com","age":30}"#;
        assert_eq!(evaluate(&zone, &c, &limiter, Some(ok)).await, Verdict::Allow);

        // Missing a required field → blocked.
        let missing = br#"{"email":"a@b.com"}"#;
        assert_eq!(
            evaluate(&zone, &c, &limiter, Some(missing)).await,
            Verdict::Block { rule_id: "schema".into() }
        );

        // Wrong type / out of range → blocked.
        let bad_type = br#"{"email":"a@b.com","age":"old"}"#;
        assert_eq!(
            evaluate(&zone, &c, &limiter, Some(bad_type)).await,
            Verdict::Block { rule_id: "schema".into() }
        );

        // An unexpected extra field (additionalProperties:false) → blocked. This
        // is the payoff over negative rules: you didn't have to anticipate the
        // attack, only describe the valid shape.
        let extra = br#"{"email":"a@b.com","age":30,"is_admin":true}"#;
        assert_eq!(
            evaluate(&zone, &c, &limiter, Some(extra)).await,
            Verdict::Block { rule_id: "schema".into() }
        );

        // A body that isn't JSON at all → blocked.
        assert_eq!(
            evaluate(&zone, &c, &limiter, Some(b"not json")).await,
            Verdict::Block { rule_id: "schema".into() }
        );

        // No body → nothing to validate, left alone.
        assert_eq!(evaluate(&zone, &c, &limiter, None).await, Verdict::Allow);
    }

    #[test]
    fn invalid_schema_fails_config_load() {
        // A malformed schema is caught at load time, not at request time.
        let err = Config::from_json(
            r#"{"zones": [{"name": "z", "hosts": ["*"], "upstream": "http://h:1",
                "rules": [{"id": "r", "priority": 1, "action": "block",
                           "conditions": [{"type": "body_schema",
                                           "schema": {"type": "not-a-real-type"}}]}]}]}"#,
        );
        assert!(err.is_err());
    }

    #[test]
    fn json_path_reads_nested_scalars() {
        let v: serde_json::Value =
            serde_json::from_str(r#"{"a":{"b":{"c":"deep"},"n":42},"s":"x"}"#).unwrap();
        assert_eq!(lookup_json_path(&v, "$.a.b.c"), Some("deep".into()));
        assert_eq!(lookup_json_path(&v, "a.n"), Some("42".into()));
        assert_eq!(lookup_json_path(&v, "s"), Some("x".into()));
        assert_eq!(lookup_json_path(&v, "a.b"), None);      // object, not a scalar
        assert_eq!(lookup_json_path(&v, "missing"), None);
    }
}

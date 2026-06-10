//! The request processing pipeline: inspect → evaluate rules → dispatch.

pub mod inspector;
pub mod rate_limit;
pub mod router;
pub mod rules;

use std::net::IpAddr;

use hyper::{HeaderMap, Method};

/// Everything the rule engine needs, extracted once by the inspector.
/// Downstream stages never re-parse the raw request.
#[derive(Debug)]
pub struct RequestContext {
    pub client_ip: IpAddr,
    pub host: String,
    pub method: Method,
    pub path: String,
    pub user_agent: Option<String>,
    pub headers: HeaderMap,
}

impl RequestContext {
    pub fn wants_html(&self) -> bool {
        self.headers
            .get(hyper::header::ACCEPT)
            .and_then(|v| v.to_str().ok())
            .is_some_and(|v| v.contains("text/html"))
    }
}

/// Outcome of rule evaluation for a single request.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Verdict {
    Allow,
    Block { rule_id: String },
    Challenge { rule_id: String },
    RateLimited { rule_id: String },
}

impl Verdict {
    pub fn label(&self) -> &'static str {
        match self {
            Verdict::Allow => "allow",
            Verdict::Block { .. } => "block",
            Verdict::Challenge { .. } => "challenge",
            Verdict::RateLimited { .. } => "rate_limited",
        }
    }

    pub fn rule_id(&self) -> Option<&str> {
        match self {
            Verdict::Allow => None,
            Verdict::Block { rule_id }
            | Verdict::Challenge { rule_id }
            | Verdict::RateLimited { rule_id } => Some(rule_id),
        }
    }
}

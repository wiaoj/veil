//! The request processing pipeline: inspect → evaluate rules → dispatch.

pub mod inspector;
pub mod rate_limit;
pub mod router;
pub mod rules;
pub mod signatures;

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
    /// Raw query string without the leading `?` (`None` when absent).
    pub query: Option<String>,
    pub user_agent: Option<String>,
    pub headers: HeaderMap,
    /// ISO 3166-1 alpha-2 country code from GeoIP (`None` when no MMDB or no
    /// match). Populated after inspection when a GeoIP database is loaded.
    pub country: Option<String>,
    /// Autonomous system number from the ASN MMDB, when available.
    pub asn: Option<u32>,
    /// JA3 TLS client fingerprint (MD5 hex) for HTTPS connections. `None` for
    /// plaintext HTTP or when the ClientHello could not be parsed.
    pub ja3: Option<String>,
}

impl RequestContext {
    pub fn wants_html(&self) -> bool {
        self.headers
            .get(hyper::header::ACCEPT)
            .and_then(|v| v.to_str().ok())
            .is_some_and(|v| v.contains("text/html"))
    }

    /// Resolves the visitor-facing language for edge-served pages from the
    /// `?locale=`/`?l=` override or the `Accept-Language` header.
    pub fn lang(&self) -> crate::i18n::Lang {
        let accept_language = self
            .headers
            .get(hyper::header::ACCEPT_LANGUAGE)
            .and_then(|v| v.to_str().ok());
        crate::i18n::resolve(self.query.as_deref(), accept_language)
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

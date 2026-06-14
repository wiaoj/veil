//! Managed signature rule set — OWASP-CRS-style attack-pattern detection.
//!
//! A small, explainable set of built-in regex families (SQL injection, XSS,
//! path traversal) evaluated against the request line, query string, a few
//! high-signal headers and (optionally) the body. Each family is a
//! [`RegexSet`] so all of its patterns are matched in a single pass.
//!
//! This is intentionally a starter rule set, not a full CRS port: the goal is
//! to catch the common, high-confidence payloads that the simple match-rule
//! conditions cannot. Patterns are case-insensitive and matched against both
//! the raw and percent-decoded forms of the URL so trivial `%2e%2e` style
//! encoding does not slip past.

use std::sync::OnceLock;

use hyper::header::{COOKIE, REFERER, USER_AGENT};
use regex::RegexSet;

use crate::config::ManagedRules;
use crate::pipeline::RequestContext;

/// SQL-injection payload signatures.
const SQLI: &[&str] = &[
    r"(?i)\bunion\b.{0,40}\bselect\b",
    r"(?i)\bselect\b.{0,80}\bfrom\b",
    r"(?i)\binsert\b.{0,40}\binto\b",
    r#"(?i)\b(or|and)\b\s+['"]?\d+['"]?\s*=\s*['"]?\d+"#,
    r"(?i)'\s*(or|and)\s+'?\d",
    r"(?i)\b(sleep|benchmark|pg_sleep)\s*\(",
    r"(?i)\bwaitfor\b\s+\bdelay\b",
    r"(?i)\b(drop|truncate|alter)\b\s+\b(table|database)\b",
    r"(?i)\binto\b\s+\b(outfile|dumpfile)\b",
    r"(?i)\bload_file\s*\(",
];

/// Cross-site-scripting payload signatures.
const XSS: &[&str] = &[
    r"(?i)<\s*script[\s/>]",
    r"(?i)<\s*/\s*script\s*>",
    r"(?i)javascript:",
    r"(?i)\bon(error|load|click|mouseover|focus|submit)\s*=",
    r"(?i)<\s*iframe[\s/>]",
    r"(?i)<\s*svg[\s/>][^>]*\bonload\b",
    r"(?i)<\s*img[^>]*\bonerror\b",
    r"(?i)document\s*\.\s*cookie",
    r"(?i)\beval\s*\(",
];

/// Path / directory-traversal and local-file-inclusion signatures.
const TRAVERSAL: &[&str] = &[
    r"\.\./",
    r"\.\.\\",
    r"(?i)%2e%2e[/\\%]",
    r"(?i)\.\.%2f",
    r"(?i)/etc/(passwd|shadow|hosts)",
    r"(?i)/proc/self/(environ|cmdline)",
    r"(?i)\b(boot|win)\.ini\b",
    r"(?i)\bfile://",
];

struct Sets {
    sqli: RegexSet,
    xss: RegexSet,
    traversal: RegexSet,
}

fn sets() -> &'static Sets {
    static SETS: OnceLock<Sets> = OnceLock::new();
    SETS.get_or_init(|| Sets {
        sqli: RegexSet::new(SQLI).expect("built-in SQLi patterns compile"),
        xss: RegexSet::new(XSS).expect("built-in XSS patterns compile"),
        traversal: RegexSet::new(TRAVERSAL).expect("built-in traversal patterns compile"),
    })
}

/// Scans the request against the enabled managed signature families. Returns
/// the matched category (`"sqli"`, `"xss"`, `"traversal"`) on the first hit,
/// or `None` if nothing matches.
pub fn scan(
    rules: &ManagedRules,
    ctx: &RequestContext,
    body: Option<&[u8]>,
) -> Option<&'static str> {
    let s = sets();

    let mut targets: Vec<String> = Vec::with_capacity(8);
    push_target(&mut targets, &ctx.path);
    if let Some(q) = &ctx.query {
        push_target(&mut targets, q);
    }
    for name in [USER_AGENT, REFERER, COOKIE] {
        if let Some(v) = ctx.headers.get(name).and_then(|v| v.to_str().ok()) {
            targets.push(v.to_owned());
        }
    }
    if rules.inspect_body && let Some(b) = body {
        targets.push(String::from_utf8_lossy(b).into_owned());
    }

    for t in &targets {
        if rules.sql_injection && s.sqli.is_match(t) {
            return Some("sqli");
        }
        if rules.xss && s.xss.is_match(t) {
            return Some("xss");
        }
        if rules.path_traversal && s.traversal.is_match(t) {
            return Some("traversal");
        }
    }
    None
}

/// Pushes a target plus, when it contains percent-encoding, its decoded form.
fn push_target(targets: &mut Vec<String>, raw: &str) {
    targets.push(raw.to_owned());
    if let Some(decoded) = percent_decode(raw) {
        targets.push(decoded);
    }
}

/// Minimal percent-decoder. Returns `None` when there is nothing to decode so
/// callers avoid a redundant duplicate scan.
fn percent_decode(s: &str) -> Option<String> {
    if !s.contains('%') {
        return None;
    }
    let bytes = s.as_bytes();
    let mut out = Vec::with_capacity(bytes.len());
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == b'%'
            && i + 2 < bytes.len()
            && let (Some(h), Some(l)) = (hex_val(bytes[i + 1]), hex_val(bytes[i + 2]))
        {
            out.push(h * 16 + l);
            i += 3;
            continue;
        }
        out.push(bytes[i]);
        i += 1;
    }
    Some(String::from_utf8_lossy(&out).into_owned())
}

fn hex_val(b: u8) -> Option<u8> {
    match b {
        b'0'..=b'9' => Some(b - b'0'),
        b'a'..=b'f' => Some(b - b'a' + 10),
        b'A'..=b'F' => Some(b - b'A' + 10),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use hyper::header::USER_AGENT;
    use hyper::{HeaderMap, Method};
    use std::net::IpAddr;

    fn ctx(path: &str, query: Option<&str>) -> RequestContext {
        RequestContext {
            client_ip: "203.0.113.5".parse::<IpAddr>().unwrap(),
            host: "example.com".to_owned(),
            method: Method::GET,
            path: path.to_owned(),
            query: query.map(str::to_owned),
            user_agent: None,
            headers: HeaderMap::new(),
            country: None,
            asn: None,
            ja3: None,
        }
    }

    fn all() -> ManagedRules {
        ManagedRules {
            sql_injection: true,
            xss: true,
            path_traversal: true,
            inspect_body: true,
            action: crate::config::ManagedAction::Block,
        }
    }

    #[test]
    fn detects_sqli_in_query() {
        let c = ctx("/products", Some("id=1 UNION SELECT password FROM users"));
        assert_eq!(scan(&all(), &c, None), Some("sqli"));
    }

    #[test]
    fn detects_or_1_equals_1() {
        let c = ctx("/login", Some("u=admin' or 1=1--"));
        assert_eq!(scan(&all(), &c, None), Some("sqli"));
    }

    #[test]
    fn detects_xss_in_query() {
        let c = ctx("/search", Some("q=<script>alert(1)</script>"));
        assert_eq!(scan(&all(), &c, None), Some("xss"));
    }

    #[test]
    fn detects_traversal_encoded() {
        let c = ctx("/files", Some("p=%2e%2e%2f%2e%2e%2fetc/passwd"));
        assert_eq!(scan(&all(), &c, None), Some("traversal"));
    }

    #[test]
    fn detects_payload_in_body() {
        let c = ctx("/comment", None);
        let body = b"text=<script>steal(document.cookie)</script>";
        assert_eq!(scan(&all(), &c, Some(body)), Some("xss"));
    }

    #[test]
    fn body_ignored_when_inspection_disabled() {
        let mut rules = all();
        rules.inspect_body = false;
        let c = ctx("/comment", None);
        let body = b"q=<script>alert(1)</script>";
        assert_eq!(scan(&rules, &c, Some(body)), None);
    }

    #[test]
    fn disabled_category_does_not_match() {
        let rules = ManagedRules {
            sql_injection: false,
            xss: true,
            path_traversal: true,
            inspect_body: false,
            action: crate::config::ManagedAction::Block,
        };
        let c = ctx("/products", Some("id=1 UNION SELECT x FROM y"));
        assert_eq!(scan(&rules, &c, None), None);
    }

    #[test]
    fn clean_request_passes() {
        let mut c = ctx("/products", Some("id=42&sort=price"));
        c.headers.insert(USER_AGENT, "Mozilla/5.0".parse().unwrap());
        assert_eq!(scan(&all(), &c, None), None);
    }
}

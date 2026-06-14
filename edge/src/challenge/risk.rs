//! Request risk scoring — Phase 4.2.
//!
//! A cheap, allocation-light heuristic score in `0..=100` derived from the
//! request's header fingerprint. The challenge engine maps the score to a
//! PoW difficulty: a request that looks like an automated client pays more
//! CPU to pass than one that looks like a real browser.
//!
//! ASN / GeoIP reputation and timing signals are future inputs (they need
//! the MMDB lookup that is still pending in the inspector); the function
//! signature already takes the full [`RequestContext`] so adding them does
//! not ripple outward.

use hyper::header::{ACCEPT, ACCEPT_ENCODING, ACCEPT_LANGUAGE};

use crate::pipeline::RequestContext;

/// Substrings that strongly indicate a non-browser client. Lowercased match.
const BOT_TOKENS: [&str; 9] = [
    "curl", "wget", "python", "go-http", "java/", "scrapy", "bot", "spider", "headless",
];

/// Computes a risk score in `0..=100`. Higher means more suspicious.
pub fn score(ctx: &RequestContext) -> u8 {
    let mut score: u32 = 0;

    match &ctx.user_agent {
        None => score += 45,
        Some(ua) => {
            let ua_lower = ua.to_ascii_lowercase();
            if BOT_TOKENS.iter().any(|t| ua_lower.contains(t)) {
                score += 35;
            }
            if ua.len() < 16 {
                score += 10;
            }
        }
    }

    // Real browsers always send these; their absence is a strong bot tell.
    if !ctx.headers.contains_key(ACCEPT) {
        score += 15;
    }
    if !ctx.headers.contains_key(ACCEPT_LANGUAGE) {
        score += 15;
    }
    if !ctx.headers.contains_key(ACCEPT_ENCODING) {
        score += 10;
    }

    score.min(100) as u8
}

/// Maps a risk score to a PoW difficulty (leading zero bits), adding up to
/// `MAX_EXTRA_BITS` on top of the configured base. Each extra bit doubles the
/// client's expected work, so the ramp is deliberately shallow.
pub fn difficulty_for(base: u32, risk: u8) -> u32 {
    const MAX_EXTRA_BITS: u32 = 4;
    let extra = (u32::from(risk) * MAX_EXTRA_BITS) / 100;
    base + extra
}

#[cfg(test)]
mod tests {
    use super::*;
    use hyper::header::{HeaderMap, ACCEPT, ACCEPT_ENCODING, ACCEPT_LANGUAGE, USER_AGENT};
    use hyper::Method;
    use std::net::IpAddr;

    fn ctx(headers: HeaderMap, user_agent: Option<&str>) -> RequestContext {
        RequestContext {
            client_ip: "203.0.113.5".parse::<IpAddr>().unwrap(),
            host: "example.com".to_owned(),
            method: Method::GET,
            path: "/".to_owned(),
            query: None,
            user_agent: user_agent.map(str::to_owned),
            headers,
            country: None,
            asn: None,
            ja3: None,
        }
    }

    fn browser_headers() -> HeaderMap {
        let mut h = HeaderMap::new();
        h.insert(ACCEPT, "text/html".parse().unwrap());
        h.insert(ACCEPT_LANGUAGE, "en-US".parse().unwrap());
        h.insert(ACCEPT_ENCODING, "gzip".parse().unwrap());
        h.insert(USER_AGENT, "Mozilla/5.0 (Windows NT 10.0)".parse().unwrap());
        h
    }

    #[test]
    fn realistic_browser_scores_low() {
        let ua = "Mozilla/5.0 (Windows NT 10.0)";
        assert_eq!(score(&ctx(browser_headers(), Some(ua))), 0);
    }

    #[test]
    fn bare_client_with_no_headers_scores_high() {
        let s = score(&ctx(HeaderMap::new(), None));
        // missing UA (45) + no accept (15) + no lang (15) + no encoding (10)
        assert_eq!(s, 85);
    }

    #[test]
    fn known_bot_user_agent_is_penalised() {
        let s = score(&ctx(browser_headers(), Some("curl/8.0")));
        // bot token (35) + short UA (<16, 10)
        assert_eq!(s, 45);
    }

    #[test]
    fn difficulty_ramps_with_risk() {
        assert_eq!(difficulty_for(20, 0), 20);
        assert_eq!(difficulty_for(20, 50), 22);
        assert_eq!(difficulty_for(20, 100), 24);
    }
}

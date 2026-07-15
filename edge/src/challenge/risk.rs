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

/// Substring every mainstream browser's User-Agent carries. Tools (curl, wget,
/// python-requests, Go's http client) do not.
const BROWSER_UA_MARKER: &str = "mozilla/";

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

    // The headers above are trivially forged — a bot that sets a full browser
    // header set scores 0 on all of them. The TLS fingerprint is not: it is a
    // property of the client's TLS stack, so claiming to be a browser while
    // handshaking like a tool is a much stronger tell than either signal alone.
    if tls_contradicts_user_agent(ctx) {
        score += 30;
    }

    score.min(100) as u8
}

/// True when the User-Agent claims a mainstream browser but the TLS ClientHello
/// does not look like one.
///
/// Deliberately uses only the stable, structural part of JA4 — no fingerprint
/// database to maintain, nothing that churns with browser releases:
///
/// * every browser sends SNI on HTTPS (`d`),
/// * every modern browser negotiates HTTP/2 over TLS-on-TCP (ALPN `h2`).
///
/// A tool that forges a browser UA but handshakes with its library defaults
/// (e.g. no ALPN at all) contradicts itself here. Returns `false` whenever we
/// cannot know: plaintext HTTP (no ClientHello), no UA, or a UA that never
/// claimed to be a browser — those are already handled above.
fn tls_contradicts_user_agent(ctx: &RequestContext) -> bool {
    let (Some(ja4), Some(ua)) = (ctx.ja4.as_deref(), ctx.user_agent.as_deref()) else {
        return false;
    };
    if !ua.to_ascii_lowercase().contains(BROWSER_UA_MARKER) {
        return false;
    }
    // JA4 a-part: t | version(2) | sni(1) | ciphers(2) | extensions(2) | alpn(2)
    if ja4.len() < 10 {
        return false;
    }
    let sni = &ja4[3..4];
    let alpn = &ja4[8..10];
    sni != "d" || alpn != "h2"
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
        ctx_with_ja4(headers, user_agent, None)
    }

    fn ctx_with_ja4(headers: HeaderMap, user_agent: Option<&str>, ja4: Option<&str>) -> RequestContext {
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
            ja4: ja4.map(str::to_owned),
        }
    }

    /// Shape of a real Chrome ClientHello: SNI + 15 ciphers/16 extensions + h2.
    const CHROME_JA4: &str = "t13d1516h2_8daaf6152771_b186095e22b6";
    /// Shape a real rustls client actually produced (see tests/tls_fingerprint.rs):
    /// SNI, but only 10 ciphers/10 extensions and **no ALPN**.
    const RUSTLS_JA4: &str = "t13d101000_61a7ad8aa9b6_3fcd1a44f3e3";
    const CHROME_UA: &str =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";

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
    fn browser_ua_with_browser_tls_is_not_penalised() {
        let c = ctx_with_ja4(browser_headers(), Some(CHROME_UA), Some(CHROME_JA4));
        assert_eq!(score(&c), 0, "a real browser must stay at zero");
    }

    #[test]
    fn browser_ua_with_tool_tls_is_penalised() {
        // Perfect browser headers + a browser UA — every header-based signal
        // scores 0 — but the TLS handshake is a library's, not a browser's.
        let c = ctx_with_ja4(browser_headers(), Some(CHROME_UA), Some(RUSTLS_JA4));
        assert_eq!(score(&c), 30, "TLS/UA contradiction must be caught");
    }

    #[test]
    fn missing_sni_with_browser_ua_is_penalised() {
        let no_sni = "t13i1516h2_8daaf6152771_b186095e22b6";
        let c = ctx_with_ja4(browser_headers(), Some(CHROME_UA), Some(no_sni));
        assert_eq!(score(&c), 30, "browsers always send SNI");
    }

    #[test]
    fn honest_tool_is_not_double_penalised_by_the_tls_signal() {
        // curl doesn't claim to be a browser, so there is nothing to contradict —
        // it is already caught by the bot-token rule alone.
        let c = ctx_with_ja4(browser_headers(), Some("curl/8.0"), Some(RUSTLS_JA4));
        assert_eq!(score(&c), 45, "bot token (35) + short UA (10), no TLS penalty");
    }

    #[test]
    fn plaintext_http_has_no_tls_signal() {
        // No ClientHello → no fingerprint → the signal must stay silent.
        let c = ctx_with_ja4(browser_headers(), Some(CHROME_UA), None);
        assert_eq!(score(&c), 0);
    }

    #[test]
    fn difficulty_ramps_with_risk() {
        assert_eq!(difficulty_for(20, 0), 20);
        assert_eq!(difficulty_for(20, 50), 22);
        assert_eq!(difficulty_for(20, 100), 24);
    }
}

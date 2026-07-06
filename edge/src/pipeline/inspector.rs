//! Stage 1 — extracts and normalises request metadata into a `RequestContext`.
//!
//! GeoIP / ASN lookups (MMDB) land here later; for now the inspector covers
//! client IP, host, method, path and user-agent.

use std::net::{IpAddr, SocketAddr};

use hyper::header::{HOST, USER_AGENT};
use hyper::{HeaderMap, Request};

use super::RequestContext;

pub fn inspect<B>(req: &Request<B>, peer: SocketAddr, trust_forwarded: bool) -> RequestContext {
    let client_ip = if trust_forwarded {
        forwarded_ip(req.headers()).unwrap_or_else(|| peer.ip())
    } else {
        peer.ip()
    };

    let host = req
        .headers()
        .get(HOST)
        .and_then(|v| v.to_str().ok())
        .map(str::to_owned)
        // HTTP/2 carries the host in the :authority pseudo-header → URI.
        .or_else(|| req.uri().authority().map(|a| a.as_str().to_owned()))
        .unwrap_or_default();

    let user_agent = req
        .headers()
        .get(USER_AGENT)
        .and_then(|v| v.to_str().ok())
        .map(str::to_owned);

    RequestContext {
        client_ip,
        host,
        method: req.method().clone(),
        path: req.uri().path().to_owned(),
        query: req.uri().query().map(str::to_owned),
        user_agent,
        headers: req.headers().clone(),
        // Filled in by the proxy after inspection when a GeoIP database is
        // loaded; the inspector itself has no geo state.
        country: None,
        asn: None,
        ja3: None,
        ja4: None,
    }
}

/// First (leftmost) address in `X-Forwarded-For` — the original client as
/// reported by the trusted proxy in front of us.
fn forwarded_ip(headers: &HeaderMap) -> Option<IpAddr> {
    headers
        .get("x-forwarded-for")?
        .to_str()
        .ok()?
        .split(',')
        .next()?
        .trim()
        .parse()
        .ok()
}

#[cfg(test)]
mod tests {
    use super::*;
    use http_body_util::Empty;
    use hyper::body::Bytes;

    fn peer() -> SocketAddr {
        "192.0.2.1:55555".parse().unwrap()
    }

    fn request(xff: Option<&str>) -> Request<Empty<Bytes>> {
        let mut builder = Request::builder().uri("/a/b?q=1").header(HOST, "example.com");
        if let Some(v) = xff {
            builder = builder.header("x-forwarded-for", v);
        }
        builder.body(Empty::new()).unwrap()
    }

    #[test]
    fn uses_peer_ip_when_forwarded_headers_untrusted() {
        let ctx = inspect(&request(Some("203.0.113.9")), peer(), false);
        assert_eq!(ctx.client_ip, "192.0.2.1".parse::<IpAddr>().unwrap());
    }

    #[test]
    fn uses_first_forwarded_ip_when_trusted() {
        let ctx = inspect(&request(Some("203.0.113.9, 10.0.0.1")), peer(), true);
        assert_eq!(ctx.client_ip, "203.0.113.9".parse::<IpAddr>().unwrap());
    }

    #[test]
    fn falls_back_to_peer_on_garbage_forwarded_header() {
        let ctx = inspect(&request(Some("not-an-ip")), peer(), true);
        assert_eq!(ctx.client_ip, peer().ip());
    }

    #[test]
    fn extracts_host_and_path() {
        let ctx = inspect(&request(None), peer(), false);
        assert_eq!(ctx.host, "example.com");
        assert_eq!(ctx.path, "/a/b");
    }
}

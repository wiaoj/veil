//! Conservative, opt-in HTTP response cache (per-zone).
//!
//! A security proxy must never turn caching into a data leak or a poisoning
//! vector, so this is deliberately strict (a subset of RFC 7234):
//!
//! * Only `GET` requests, and only those **without** `Authorization` or
//!   `Cookie` (never cache anything that could be personalised).
//! * Only `200 OK` responses with **no** `Set-Cookie` and **no** `Vary`.
//! * Freshness must be *explicit* — `Cache-Control: s-maxage`/`max-age`. No
//!   heuristic freshness. `no-store`/`no-cache`/`private` disable caching.
//! * Only responses with a known `Content-Length` at or below the zone cap are
//!   buffered (so a streamed body is never consumed to fill the cache).
//!
//! Storage is a bounded in-memory map, process-local like the rest of the edge
//! hot-path state.

use std::collections::HashMap;
use std::sync::Mutex;
use std::time::{Duration, Instant};

use hyper::header::{HeaderName, HeaderValue};
use hyper::body::Bytes;
use hyper::{HeaderMap, Response, StatusCode};

use crate::response::{full, ProxyBody};

/// Hop-by-hop headers never stored/served from cache (RFC 7230 §6.1).
const HOP_BY_HOP: [&str; 8] = [
    "connection",
    "keep-alive",
    "transfer-encoding",
    "te",
    "trailer",
    "upgrade",
    "proxy-authenticate",
    "proxy-authorization",
];

struct CachedResponse {
    status: StatusCode,
    headers: HeaderMap,
    body: Bytes,
    expires_at: Instant,
}

/// Bounded in-memory response cache. Keyed by host + path + query.
pub struct ResponseCache {
    entries: Mutex<HashMap<String, CachedResponse>>,
    max_entries: usize,
}

impl ResponseCache {
    pub fn new(max_entries: usize) -> Self {
        Self { entries: Mutex::new(HashMap::new()), max_entries }
    }

    /// A fresh cached response for `key`, if any. Expired entries are dropped.
    pub fn get(&self, key: &str) -> Option<Response<ProxyBody>> {
        let mut map = self.entries.lock().expect("cache poisoned");
        let entry = map.get(key)?;
        if entry.expires_at <= Instant::now() {
            map.remove(key);
            return None;
        }
        Some(build(entry.status, &entry.headers, entry.body.clone(), "HIT"))
    }

    fn insert(&self, key: String, status: StatusCode, headers: HeaderMap, body: Bytes, ttl: Duration) {
        let mut map = self.entries.lock().expect("cache poisoned");
        // Simple bound: drop already-expired entries, and if still full, skip
        // the insert rather than growing without limit (no LRU yet).
        if map.len() >= self.max_entries {
            let now = Instant::now();
            map.retain(|_, e| e.expires_at > now);
            if map.len() >= self.max_entries {
                return;
            }
        }
        map.insert(key, CachedResponse { status, headers, body, expires_at: Instant::now() + ttl });
    }
}

/// Cache key: host + path + query (method is always GET at the call site).
pub fn key(host: &str, path: &str, query: Option<&str>) -> String {
    format!("{host}\u{1f}{path}\u{1f}{}", query.unwrap_or(""))
}

/// Explicit freshness lifetime from `Cache-Control`, or `None` when the
/// response must not be cached. `s-maxage` wins over `max-age`.
fn ttl_from_cache_control(headers: &HeaderMap) -> Option<Duration> {
    let cc = headers.get(hyper::header::CACHE_CONTROL)?.to_str().ok()?.to_ascii_lowercase();
    if cc.contains("no-store") || cc.contains("no-cache") || cc.contains("private") {
        return None;
    }
    directive_secs(&cc, "s-maxage")
        .or_else(|| directive_secs(&cc, "max-age"))
        .map(Duration::from_secs)
}

/// Parses `<name>=<seconds>` out of a Cache-Control string.
fn directive_secs(cc: &str, name: &str) -> Option<u64> {
    let start = cc.find(name)? + name.len();
    let rest = cc[start..].trim_start();
    let rest = rest.strip_prefix('=')?.trim_start();
    let digits: String = rest.chars().take_while(|c| c.is_ascii_digit()).collect();
    digits.parse().ok()
}

/// Decides whether a response may be cached and for how long — headers only, so
/// the body is never touched for a non-cacheable response.
pub fn cacheable_ttl(status: StatusCode, headers: &HeaderMap) -> Option<Duration> {
    if status != StatusCode::OK {
        return None;
    }
    if headers.contains_key(hyper::header::SET_COOKIE) || headers.contains_key(hyper::header::VARY) {
        return None;
    }
    ttl_from_cache_control(headers)
}

/// Stores a freshly-fetched response body and returns it (with `X-Veil-Cache:
/// MISS`). Headers are filtered of hop-by-hop fields; content-length is set
/// from the body on serve.
pub fn store_and_build(
    cache: &ResponseCache,
    key: String,
    status: StatusCode,
    headers: &HeaderMap,
    body: Bytes,
    ttl: Duration,
) -> Response<ProxyBody> {
    let filtered = filter_headers(headers);
    cache.insert(key, status, filtered.clone(), body.clone(), ttl);
    build(status, &filtered, body, "MISS")
}

fn filter_headers(headers: &HeaderMap) -> HeaderMap {
    let mut out = HeaderMap::new();
    for (name, value) in headers {
        let lname = name.as_str().to_ascii_lowercase();
        if HOP_BY_HOP.contains(&lname.as_str())
            || lname == "content-length"
            || lname == "set-cookie"
        {
            continue;
        }
        out.append(name.clone(), value.clone());
    }
    out
}

/// Builds a response from cached parts, stamping `X-Veil-Cache` and a fresh
/// content-length.
fn build(status: StatusCode, headers: &HeaderMap, body: Bytes, cache_status: &str) -> Response<ProxyBody> {
    let mut builder = Response::builder().status(status);
    if let Some(map) = builder.headers_mut() {
        *map = headers.clone();
        map.insert(
            hyper::header::CONTENT_LENGTH,
            HeaderValue::from_str(&body.len().to_string()).expect("numeric length"),
        );
        map.insert(
            HeaderName::from_static("x-veil-cache"),
            HeaderValue::from_static(if cache_status == "HIT" { "HIT" } else { "MISS" }),
        );
    }
    builder.body(full(body)).expect("cached response")
}

#[cfg(test)]
mod tests {
    use super::*;

    fn headers(pairs: &[(&str, &str)]) -> HeaderMap {
        let mut h = HeaderMap::new();
        for (k, v) in pairs {
            h.insert(HeaderName::from_bytes(k.as_bytes()).unwrap(), HeaderValue::from_str(v).unwrap());
        }
        h
    }

    #[test]
    fn requires_explicit_max_age() {
        assert!(cacheable_ttl(StatusCode::OK, &headers(&[])).is_none());
        assert_eq!(
            cacheable_ttl(StatusCode::OK, &headers(&[("cache-control", "public, max-age=60")])),
            Some(Duration::from_secs(60))
        );
    }

    #[test]
    fn s_maxage_wins_over_max_age() {
        assert_eq!(
            cacheable_ttl(StatusCode::OK, &headers(&[("cache-control", "max-age=10, s-maxage=99")])),
            Some(Duration::from_secs(99))
        );
    }

    #[test]
    fn no_store_private_and_cookies_block_caching() {
        assert!(cacheable_ttl(StatusCode::OK, &headers(&[("cache-control", "no-store")])).is_none());
        assert!(cacheable_ttl(StatusCode::OK, &headers(&[("cache-control", "private, max-age=60")])).is_none());
        assert!(cacheable_ttl(StatusCode::OK, &headers(&[("set-cookie", "a=b"), ("cache-control", "max-age=60")])).is_none());
        assert!(cacheable_ttl(StatusCode::OK, &headers(&[("vary", "User-Agent"), ("cache-control", "max-age=60")])).is_none());
    }

    #[test]
    fn only_200_is_cacheable() {
        assert!(cacheable_ttl(StatusCode::NOT_FOUND, &headers(&[("cache-control", "max-age=60")])).is_none());
    }

    #[test]
    fn stores_then_serves_hit_until_expiry() {
        let cache = ResponseCache::new(16);
        let k = key("h", "/a", None);
        assert!(cache.get(&k).is_none());
        let _ = store_and_build(&cache, k.clone(), StatusCode::OK, &headers(&[("content-type", "text/plain")]),
            Bytes::from_static(b"hi"), Duration::from_secs(30));
        let hit = cache.get(&k).expect("fresh hit");
        assert_eq!(hit.headers().get("x-veil-cache").unwrap(), "HIT");
        assert_eq!(hit.headers().get("content-length").unwrap(), "2");
    }

    #[test]
    fn expired_entry_is_evicted_on_get() {
        let cache = ResponseCache::new(16);
        let k = key("h", "/a", None);
        let _ = store_and_build(&cache, k.clone(), StatusCode::OK, &headers(&[]),
            Bytes::from_static(b"x"), Duration::from_millis(1));
        std::thread::sleep(Duration::from_millis(5));
        assert!(cache.get(&k).is_none(), "stale entry must not be served");
    }
}

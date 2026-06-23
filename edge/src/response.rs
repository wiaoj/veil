//! Shared response body type and small response builders.
//!
//! Generated responses (`Full<Bytes>`) and proxied upstream responses
//! (`hyper::body::Incoming`) are unified behind one boxed body type so the
//! request handler has a single return type.

use std::error::Error as StdError;
use std::io;

use http_body_util::combinators::BoxBody;
use http_body_util::{BodyExt, Full};
use hyper::body::Bytes;
use hyper::header::{CACHE_CONTROL, CONTENT_TYPE, RETRY_AFTER};
use hyper::{Response, StatusCode};

pub type ProxyBody = BoxBody<Bytes, hyper::Error>;

/// Response header carrying the machine-readable gateway failure code (the same
/// value used in the `problem+json` `type` URL), so operators and monitoring
/// can distinguish origin failure modes without scraping the HTML page.
pub const VEIL_ERROR_HEADER: &str = "x-veil-error";

/// Why a request could not be served from the origin. Maps to a standard wire
/// status (502/503/504) plus a distinct branded page + stable reference code —
/// rather than non-standard Cloudflare-style 52x codes that confuse monitoring
/// and intermediaries.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum GatewayReason {
    /// Origin refused the TCP connection (likely down). → 502
    WebServerDown,
    /// DNS resolution / routing to the origin failed. → 502
    OriginUnreachable,
    /// TLS handshake with the (https) origin failed. → 502
    OriginTlsHandshake,
    /// The origin's TLS certificate could not be validated. → 502
    OriginCertInvalid,
    /// Origin did not send response headers within the deadline. → 504
    OriginTimeout,
    /// Origin sent a malformed / incomplete response. → 502
    BadResponse,
    /// Catch-all upstream failure we could not classify. → 502
    BadGateway,
    /// This edge node has no usable config to serve traffic. → 503
    NotReady,
}

impl GatewayReason {
    /// The wire status code (standard, never a 52x).
    pub fn status(self) -> StatusCode {
        match self {
            GatewayReason::OriginTimeout => StatusCode::GATEWAY_TIMEOUT,
            GatewayReason::NotReady => StatusCode::SERVICE_UNAVAILABLE,
            _ => StatusCode::BAD_GATEWAY,
        }
    }

    /// Stable machine-readable code (header value + `problem+json` type slug).
    pub fn ref_code(self) -> &'static str {
        match self {
            GatewayReason::WebServerDown => "web_server_down",
            GatewayReason::OriginUnreachable => "origin_unreachable",
            GatewayReason::OriginTlsHandshake => "origin_ssl_handshake",
            GatewayReason::OriginCertInvalid => "origin_ssl_invalid",
            GatewayReason::OriginTimeout => "origin_timeout",
            GatewayReason::BadResponse => "origin_bad_response",
            GatewayReason::BadGateway => "bad_gateway",
            GatewayReason::NotReady => "edge_not_ready",
        }
    }
}

/// Classifies a hyper/connector error from the upstream client into a
/// [`GatewayReason`]. Walks the error source chain: io error kinds are reliable
/// for connect failures; TLS/cert and DNS failures surface as typed errors deep
/// in the chain, matched on their message as a pragmatic fallback. Anything
/// unrecognised degrades to [`GatewayReason::BadGateway`].
pub fn classify_upstream_error(err: &(dyn StdError + 'static)) -> GatewayReason {
    let mut saw_tls = false;
    let mut saw_cert = false;
    let mut current: Option<&(dyn StdError + 'static)> = Some(err);

    while let Some(e) = current {
        if let Some(io_err) = e.downcast_ref::<io::Error>() {
            match io_err.kind() {
                io::ErrorKind::ConnectionRefused => return GatewayReason::WebServerDown,
                io::ErrorKind::TimedOut => return GatewayReason::OriginTimeout,
                io::ErrorKind::ConnectionReset
                | io::ErrorKind::ConnectionAborted
                | io::ErrorKind::BrokenPipe
                | io::ErrorKind::UnexpectedEof => return GatewayReason::BadResponse,
                _ => {}
            }
        }

        let msg = e.to_string().to_ascii_lowercase();
        if msg.contains("dns")
            || msg.contains("failed to lookup")
            || msg.contains("name or service not known")
            || msg.contains("nodename nor servname")
            || msg.contains("no such host")
        {
            return GatewayReason::OriginUnreachable;
        }
        if msg.contains("certificate") || msg.contains("unknownissuer") || msg.contains("not valid for name") {
            saw_cert = true;
        }
        if msg.contains("handshake") || msg.contains("rustls") || msg.contains("corrupt message") || msg.contains("tls") {
            saw_tls = true;
        }

        current = e.source();
    }

    if saw_cert {
        GatewayReason::OriginCertInvalid
    } else if saw_tls {
        GatewayReason::OriginTlsHandshake
    } else {
        GatewayReason::BadGateway
    }
}

/// Branded gateway/origin error page (HTML for browsers, `problem+json`
/// otherwise), reusing the shared error template. Carries the reason both as a
/// response header and, for JSON, in the `type` URL.
pub fn gateway_error(
    reason: GatewayReason,
    wants_html: bool,
    lang: crate::i18n::Lang,
) -> Response<ProxyBody> {
    let status = reason.status();
    if wants_html {
        let s = crate::i18n::gateway_strings(lang, reason);
        let body = ERROR_HTML
            .replace("{lang}", lang.code())
            .replace("{title}", s.title)
            .replace("{detail}", s.detail);
        Response::builder()
            .status(status)
            .header(CONTENT_TYPE, "text/html; charset=utf-8")
            .header(CACHE_CONTROL, "no-store")
            .header(VEIL_ERROR_HEADER, reason.ref_code())
            .body(full(body))
            .expect("static response")
    } else {
        // Problem+json stays English (machine-facing), matching the block /
        // rate-limit JSON responses.
        let s = crate::i18n::gateway_strings(crate::i18n::Lang::En, reason);
        let json = format!(
            r#"{{"type":"https://docs.veil.io/probs/{}","title":"{}","status":{},"detail":"{}"}}"#,
            reason.ref_code(),
            s.title,
            status.as_u16(),
            s.detail
        );
        Response::builder()
            .status(status)
            .header(CONTENT_TYPE, "application/problem+json; charset=utf-8")
            .header(CACHE_CONTROL, "no-store")
            .header(VEIL_ERROR_HEADER, reason.ref_code())
            .body(full(json))
            .expect("static response")
    }
}

const ERROR_HTML: &str = include_str!("../templates/error.html");

pub fn full(body: impl Into<Bytes>) -> ProxyBody {
    Full::new(body.into()).map_err(|never| match never {}).boxed()
}

pub fn text(status: StatusCode, body: &str) -> Response<ProxyBody> {
    Response::builder()
        .status(status)
        .header(CONTENT_TYPE, "text/plain; charset=utf-8")
        .body(full(body.to_owned()))
        .expect("static response")
}

pub fn html(status: StatusCode, body: String) -> Response<ProxyBody> {
    Response::builder()
        .status(status)
        .header(CONTENT_TYPE, "text/html; charset=utf-8")
        .header(CACHE_CONTROL, "no-store")
        .body(full(body))
        .expect("static response")
}

pub fn json_response(status: StatusCode, body: &str) -> Response<ProxyBody> {
    Response::builder()
        .status(status)
        .header(CONTENT_TYPE, "application/json; charset=utf-8")
        .header(CACHE_CONTROL, "no-store")
        .body(full(body.to_owned()))
        .expect("static response")
}

pub fn forbidden(wants_html: bool, lang: crate::i18n::Lang) -> Response<ProxyBody> {
    if wants_html {
        let t = crate::i18n::error_strings(lang);
        let body = ERROR_HTML
            .replace("{lang}", lang.code())
            .replace("{title}", t.forbidden_title)
            .replace("{detail}", t.forbidden_detail);
        html(StatusCode::FORBIDDEN, body)
    } else {
        let json = r#"{"type":"https://docs.veil.io/probs/forbidden","title":"Forbidden","status":403,"detail":"Access denied by Edge policy."}"#;
        Response::builder()
            .status(StatusCode::FORBIDDEN)
            .header(CONTENT_TYPE, "application/problem+json; charset=utf-8")
            .body(full(json))
            .expect("static response")
    }
}

pub fn rate_limited(retry_after_secs: u64, wants_html: bool, lang: crate::i18n::Lang) -> Response<ProxyBody> {
    if wants_html {
        let t = crate::i18n::error_strings(lang);
        let body = ERROR_HTML
            .replace("{lang}", lang.code())
            .replace("{title}", t.rate_limited_title)
            .replace("{detail}", &t.rate_limited_detail_fmt.replace("{}", &retry_after_secs.to_string()));

        Response::builder()
            .status(StatusCode::TOO_MANY_REQUESTS)
            .header(CONTENT_TYPE, "text/html; charset=utf-8")
            .header(CACHE_CONTROL, "no-store")
            .header(RETRY_AFTER, retry_after_secs.to_string())
            .body(full(body))
            .expect("static response")
    } else {
        let json = format!(
            r#"{{"type":"https://docs.veil.io/probs/rate-limited","title":"Too Many Requests","status":429,"detail":"Rate limit exceeded. Please try again later.","retry_after":{}}}"#,
            retry_after_secs
        );
        Response::builder()
            .status(StatusCode::TOO_MANY_REQUESTS)
            .header(CONTENT_TYPE, "application/problem+json; charset=utf-8")
            .header(RETRY_AFTER, retry_after_secs.to_string())
            .body(full(json))
            .expect("static response")
    }
}

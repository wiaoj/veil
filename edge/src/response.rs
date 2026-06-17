//! Shared response body type and small response builders.
//!
//! Generated responses (`Full<Bytes>`) and proxied upstream responses
//! (`hyper::body::Incoming`) are unified behind one boxed body type so the
//! request handler has a single return type.

use http_body_util::combinators::BoxBody;
use http_body_util::{BodyExt, Full};
use hyper::body::Bytes;
use hyper::header::{CACHE_CONTROL, CONTENT_TYPE, RETRY_AFTER};
use hyper::{Response, StatusCode};

pub type ProxyBody = BoxBody<Bytes, hyper::Error>;

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

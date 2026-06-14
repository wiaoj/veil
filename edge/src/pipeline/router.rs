//! Stage 3 — forwards `allow` verdicts to the zone's upstream.
//!
//! Load balancing strategies and per-host connection pool tuning come later;
//! hyper-util's legacy client already pools connections per host, which is
//! enough for a single upstream per zone.

use http_body_util::{BodyExt, Full};
use hyper::body::{Body, Bytes, Incoming};
use hyper::header::{HeaderValue, CONNECTION, HOST};
use hyper::{Request, Response, Uri, Version};
use hyper_util::client::legacy::connect::HttpConnector;
use hyper_util::client::legacy::Client;

use crate::response::ProxyBody;

use super::RequestContext;

/// Client for streamed (un-inspected) request bodies.
pub type UpstreamClient = Client<HttpConnector, Incoming>;
/// Client for buffered bodies — used after body inspection, where the body
/// has already been read into memory.
pub type BufferedClient = Client<HttpConnector, Full<Bytes>>;

/// Forwards a request to the zone upstream. Generic over the body type so both
/// the streamed (`Incoming`) and buffered (`Full<Bytes>`, post-inspection)
/// paths share one implementation.
pub async fn forward<B>(
    req: Request<B>,
    ctx: &RequestContext,
    upstream: &str,
    client: &Client<HttpConnector, B>,
) -> Result<Response<ProxyBody>, Box<dyn std::error::Error + Send + Sync>>
where
    B: Body + Send + Unpin + 'static,
    B::Data: Send,
    B::Error: Into<Box<dyn std::error::Error + Send + Sync>>,
{
    let (mut parts, body) = req.into_parts();

    let path_and_query = parts
        .uri
        .path_and_query()
        .map_or("/", |pq| pq.as_str());
    let uri: Uri = format!("{}{}", upstream.trim_end_matches('/'), path_and_query).parse()?;

    parts.headers.remove(CONNECTION);
    parts.headers.remove(HOST);
    if let Some(authority) = uri.authority() {
        parts
            .headers
            .insert(HOST, HeaderValue::from_str(authority.as_str())?);
    }

    let client_ip = ctx.client_ip.to_string();
    let xff = match parts.headers.get("x-forwarded-for").and_then(|v| v.to_str().ok()) {
        Some(existing) => format!("{existing}, {client_ip}"),
        None => client_ip.clone(),
    };
    parts.headers.insert("x-forwarded-for", HeaderValue::from_str(&xff)?);
    parts.headers.insert("x-real-ip", HeaderValue::from_str(&client_ip)?);
    parts
        .headers
        .insert("x-forwarded-host", HeaderValue::from_str(&ctx.host)?);

    parts.uri = uri;
    // The client speaks HTTP/1.1 to the upstream regardless of what the
    // downstream connection negotiated.
    parts.version = Version::HTTP_11;

    let response = client.request(Request::from_parts(parts, body)).await?;
    Ok(response.map(BodyExt::boxed))
}

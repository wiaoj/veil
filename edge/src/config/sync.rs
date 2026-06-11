//! Config sync client — pulls the initial zone snapshot from the control
//! plane at startup. Runtime updates arrive via the push receiver on the
//! proxy listener (`POST /_veil/internal/config`).

use http_body_util::{BodyExt, Empty};
use hyper::body::Bytes;
use hyper::{Request, Uri};
use hyper_util::client::legacy::connect::HttpConnector;
use hyper_util::client::legacy::Client;
use hyper_util::rt::TokioExecutor;

use super::Config;

pub const NODE_TOKEN_HEADER: &str = "x-veil-node-token";

pub struct SyncSettings {
    pub control_plane_url: String,
    pub node_id: String,
    pub node_token: String,
}

/// All three of `VEIL_CONTROL_PLANE_URL`, `VEIL_NODE_ID` and
/// `VEIL_NODE_TOKEN` must be set for control-plane sync to activate;
/// otherwise the node runs from the local config file.
pub fn settings_from_env() -> Option<SyncSettings> {
    Some(SyncSettings {
        control_plane_url: std::env::var("VEIL_CONTROL_PLANE_URL").ok()?,
        node_id: std::env::var("VEIL_NODE_ID").ok()?,
        node_token: std::env::var("VEIL_NODE_TOKEN").ok()?,
    })
}

/// Fetches the full zone snapshot from
/// `GET {control_plane}/internal/config/{node_id}`.
pub async fn fetch_initial(
    settings: &SyncSettings,
) -> Result<Config, Box<dyn std::error::Error + Send + Sync>> {
    let uri: Uri = format!(
        "{}/internal/config/{}",
        settings.control_plane_url.trim_end_matches('/'),
        settings.node_id
    )
    .parse()?;

    let request = Request::builder()
        .uri(uri)
        .header(NODE_TOKEN_HEADER, &settings.node_token)
        .body(Empty::<Bytes>::new())?;

    let client: Client<HttpConnector, Empty<Bytes>> =
        Client::builder(TokioExecutor::new()).build_http();

    let response = client.request(request).await?;
    let status = response.status();
    if !status.is_success() {
        return Err(format!("control plane returned {status}").into());
    }

    let body = response.into_body().collect().await?.to_bytes();
    let raw = std::str::from_utf8(&body)?;
    Ok(Config::from_json(raw)?)
}

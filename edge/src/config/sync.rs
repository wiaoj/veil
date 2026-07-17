//! Config sync client — pulls the initial zone snapshot from the control
//! plane at startup. Runtime updates arrive via the push receiver on the
//! proxy listener (`POST /_veil/internal/config`).

use std::time::Duration;

use http_body_util::{BodyExt, Empty};
use hyper::body::Bytes;
use hyper::{Request, Uri};
use hyper_util::client::legacy::connect::HttpConnector;
use hyper_util::client::legacy::Client;
use hyper_util::rt::TokioExecutor;
use tracing::warn;

use super::Config;

pub const NODE_TOKEN_HEADER: &str = "x-veil-node-token";

const PULL_ATTEMPTS: u32 = 4;
const PULL_INITIAL_BACKOFF: Duration = Duration::from_millis(500);

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

/// Optional periodic reconcile interval (`VEIL_CONFIG_RECONCILE_SECS`). When set
/// to a positive value, the node re-pulls the full snapshot on this cadence as a
/// safety net for missed runtime pushes. Unset / `0` disables it (push-only).
/// Values below 30s are clamped up — this is a drift corrector, not a poller.
pub fn reconcile_interval_from_env() -> Option<Duration> {
    std::env::var("VEIL_CONFIG_RECONCILE_SECS")
        .ok()
        .and_then(|v| v.parse::<u64>().ok())
        .filter(|&secs| secs > 0)
        .map(|secs| Duration::from_secs(secs.max(30)))
}

/// `fetch_initial` with exponential backoff. Returns the last error once all
/// attempts are exhausted.
pub async fn fetch_with_retry(
    settings: &SyncSettings,
) -> Result<(Config, String), Box<dyn std::error::Error + Send + Sync>> {
    let mut backoff = PULL_INITIAL_BACKOFF;
    let mut last_error: Box<dyn std::error::Error + Send + Sync> = "no attempts made".into();

    for attempt in 1..=PULL_ATTEMPTS {
        match fetch_initial(settings).await {
            Ok(pulled) => return Ok(pulled),
            Err(err) => {
                warn!(attempt, max_attempts = PULL_ATTEMPTS, error = %err, "config pull failed");
                last_error = err;
                if attempt < PULL_ATTEMPTS {
                    tokio::time::sleep(backoff).await;
                    backoff *= 2;
                }
            }
        }
    }

    Err(last_error)
}

/// Fetches the full zone snapshot from
/// `GET {control_plane}/internal/config/{node_id}`. Returns the parsed
/// config together with the raw JSON (for the last-known-good cache).
pub async fn fetch_initial(
    settings: &SyncSettings,
) -> Result<(Config, String), Box<dyn std::error::Error + Send + Sync>> {
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
    let config = Config::from_json(raw)?;
    Ok((config, raw.to_owned()))
}

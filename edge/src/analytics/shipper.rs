//! Background task draining the log buffer to `Veil.Analytics`.
//!
//! Flush cadence: every 500ms, or as soon as the buffer crosses
//! [`super::FLUSH_THRESHOLD`] records — whichever comes first. Each POST
//! carries at most [`MAX_BATCH`] records. Delivery is fire-and-forget: a
//! failed batch is dropped (and counted), never retried, so an analytics
//! outage cannot back up into the proxy.

use std::sync::Arc;
use std::time::Duration;

use http_body_util::Full;
use hyper::body::Bytes;
use hyper::header::CONTENT_TYPE;
use hyper::Request;
use hyper_util::client::legacy::connect::HttpConnector;
use hyper_util::client::legacy::Client;
use hyper_util::rt::TokioExecutor;
use serde::Serialize;
use tracing::{debug, warn};

use super::LogBuffer;
use crate::config::sync::NODE_TOKEN_HEADER;

pub const FLUSH_INTERVAL: Duration = Duration::from_millis(500);

/// Upper bound on records per POST.
pub const MAX_BATCH: usize = 1_000;

/// Ingest endpoint for request logs (relative to `VEIL_ANALYTICS_URL`).
pub const REQUEST_LOG_ENDPOINT: &str = "ingest";
/// Ingest endpoint for human-verification interaction telemetry.
pub const INTERACTION_ENDPOINT: &str = "ingest/interactions";

#[derive(Clone)]
pub struct ShipperSettings {
    /// Analytics base URL (`VEIL_ANALYTICS_URL`, trailing slash trimmed). The
    /// per-stream endpoint path is appended by [`run`].
    pub base_url: String,
    /// Node identity sent in the payload (`VEIL_NODE_ID`, `"local"` when unset).
    pub node_id: String,
    /// `VEIL_NODE_TOKEN`; the control plane authenticates batches with it.
    pub node_token: Option<String>,
}

/// `Some` only when `VEIL_ANALYTICS_URL` is set — mirrors
/// [`super::buffer_from_env`] so buffer and shipper enable together.
pub fn settings_from_env() -> Option<ShipperSettings> {
    let base = std::env::var("VEIL_ANALYTICS_URL").ok()?;
    Some(ShipperSettings {
        base_url: base.trim_end_matches('/').to_owned(),
        node_id: std::env::var("VEIL_NODE_ID").unwrap_or_else(|_| "local".to_owned()),
        node_token: std::env::var("VEIL_NODE_TOKEN").ok(),
    })
}

#[derive(Serialize)]
struct IngestPayload<'a, T> {
    node_id: &'a str,
    records: &'a [T],
}

/// Drains `buffer` to `{base_url}/{endpoint}` on the shared fire-and-forget
/// cadence. Generic over the record type so request logs and interaction
/// telemetry reuse one shipper.
pub async fn run<T: Serialize + Send + 'static>(
    buffer: Arc<LogBuffer<T>>,
    settings: ShipperSettings,
    endpoint: &'static str,
) {
    let url = format!("{}/{}", settings.base_url, endpoint);
    let client: Client<HttpConnector, Full<Bytes>> =
        Client::builder(TokioExecutor::new()).build_http();

    loop {
        tokio::select! {
            _ = tokio::time::sleep(FLUSH_INTERVAL) => {}
            _ = buffer.wait_for_flush_signal() => {}
        }

        loop {
            let batch = buffer.drain(MAX_BATCH);
            if batch.is_empty() {
                break;
            }
            match ship(&client, &url, &settings, &batch).await {
                Ok(()) => debug!(records = batch.len(), endpoint, "analytics batch shipped"),
                Err(err) => {
                    // Drained records are gone by design; stop draining this
                    // cycle so an outage costs one POST per interval, not a
                    // tight error loop.
                    warn!(records = batch.len(), endpoint, error = %err, "analytics batch dropped");
                    break;
                }
            }
        }
    }
}

async fn ship<T: Serialize>(
    client: &Client<HttpConnector, Full<Bytes>>,
    url: &str,
    settings: &ShipperSettings,
    batch: &[T],
) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let payload = serde_json::to_vec(&IngestPayload {
        node_id: &settings.node_id,
        records: batch,
    })?;

    let mut request = Request::post(url)
        .header(CONTENT_TYPE, "application/json");
    if let Some(token) = &settings.node_token {
        request = request.header(NODE_TOKEN_HEADER, token);
    }

    let response = client
        .request(request.body(Full::new(Bytes::from(payload)))?)
        .await?;

    let status = response.status();
    if !status.is_success() {
        return Err(format!("ingest endpoint returned {status}").into());
    }
    Ok(())
}
